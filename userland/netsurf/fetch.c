/* Zenith HTTP/HTTPS back end for NetSurf's cooperative fetch scheduler. */
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <stdio.h>
#include "fetch.h"
#include "fetch-transport.h"
#include "content/fetch.h"
#include "content/fetchers.h"

#define MAX_HTTP (8UL * 1024 * 1024)
static const char *parse_error;
static int failure(const char *message) { parse_error = message; return 1; }
static unsigned char lower(unsigned char c) { return c >= 'A' && c <= 'Z' ? c + ('a' - 'A') : c; }
static int equal_i(const char *a, const char *b) { return strcasecmp(a, b) == 0; }
static size_t len(const char *s) { return strlen(s); }
static int decimal(const char *s, unsigned long max, unsigned long *out)
{
    unsigned long n = 0;
    if (!*s) return 0;
    for (; *s; s++) {
        if (*s < '0' || *s > '9') return 0;
        unsigned digit = *s - '0';
        if (n > max / 10 || (n == max / 10 && digit > max % 10)) return 0;
        n = n * 10 + digit;
    }
    *out = n;
    return 1;
}
/* The framing parser also serves the signed CLI; only one socket is admitted at a time. */
#include "../net-tools/http_response.h"

enum web_phase { CONNECT, SEND, RECEIVE };
struct web_fetch {
    struct fetch *parent;
    nsurl *url;
    struct response response;
    char *request;
    size_t sent;
    long socket;
    enum web_phase phase;
    bool tls, only_2xx, aborted, inside_poll;
    const char *error;
};
static struct web_fetch *active;
static uint8_t wire[16384];

static void notify(struct web_fetch *f, fetch_msg_type type, const char *text)
{
    fetch_msg msg = {.type = type};
    if (type == FETCH_ERROR) msg.data.error = text;
    if (type == FETCH_REDIRECT) msg.data.redirect = text;
    fetch_send_callback(&msg, f->parent);
}

static void block(struct web_fetch *f, fetch_msg_type type, const void *data, size_t size)
{
    fetch_msg msg = {.type = type, .data.header_or_data = {.buf = data, .len = size}};
    fetch_send_callback(&msg, f->parent);
}

static bool web_init(lwc_string *scheme) { (void)scheme; return true; }
static void web_fini(lwc_string *scheme) { (void)scheme; }
static bool web_accept(const nsurl *url) { return nsurl_has_component(url, NSURL_HOST); }

static void *web_setup(struct fetch *parent, nsurl *url, bool only_2xx,
                       bool downgrade_tls, const char *post, const struct fetch_multipart_data *multipart,
                       const char **headers)
{
    (void)headers; /* This GET-only backend always requests a fresh representation. */
    (void)downgrade_tls; /* Each HTTPS connection uses the same strict kernel verifier. */
    struct web_fetch *f = calloc(1, sizeof(*f));
    if (!f) return NULL;
    f->socket = -1;
    f->parent = parent;
    f->url = nsurl_ref(url);
    f->only_2xx = only_2xx;
    f->tls = nsurl_get_scheme_type(url) == NSURL_SCHEME_HTTPS;
    if (post || multipart) f->error = "Form submissions are unavailable on this port.";
    if (nsurl_has_component(url, NSURL_USERNAME) || nsurl_has_component(url, NSURL_PASSWORD))
        f->error = "Credentials in web addresses are unsupported.";
    return f;
}

/* Let NetSurf queue the rest, including subresources discovered in a callback. */
static bool web_start(void *ctx)
{
    if (active) return false;
    active = ctx;
    return true;
}

static void web_free(void *ctx)
{
    struct web_fetch *f = ctx;
    if (active == f) active = NULL;
    if (f->socket >= 0) zenith_web_close(f->socket);
    free(f->request);
    nsurl_unref(f->url);
    free(f);
}

static void retire(struct web_fetch *f)
{
    fetch_remove_from_queues(f->parent);
    fetch_free(f->parent);
}

static void web_abort(void *ctx)
{
    struct web_fetch *f = ctx;
    f->aborted = true;
    if (!f->inside_poll) retire(f);
}

static bool request_field(const char *s)
{
    for (; *s; s++) {
        unsigned char c = *s;
        if (c < 33 || c > 126 || c == '\\') return false;
    }
    return true;
}

static void connect_fetch(struct web_fetch *f)
{
    lwc_string *host = nsurl_get_component(f->url, NSURL_HOST);
    lwc_string *port = nsurl_get_component(f->url, NSURL_PORT);
    char *path = NULL;
    size_t path_length = 0;
    unsigned long number = f->tls ? 443 : 80;
    if (!host || lwc_string_length(host) > 253 || !request_field(lwc_string_data(host)) ||
        (port && (!decimal(lwc_string_data(port), 65535, &number) || !number)) ||
        nsurl_get(f->url, NSURL_PATH | NSURL_QUERY, &path, &path_length) != NSERROR_OK ||
        path_length > 4096 || !request_field(path)) {
        f->error = "Invalid or oversized HTTP address.";
    } else {
        const char *prefix = path[0] == '/' ? "" : "/";
        size_t cap = path_length + lwc_string_length(host) + 192;
        f->request = malloc(cap);
        if (!f->request) {
            f->error = "Out of memory preparing HTTP request.";
        } else {
            int n = snprintf(f->request, cap, "GET %s%s HTTP/1.1\r\nHost: %s:%lu\r\n"
                             "User-Agent: NetSurf/3.11 (Zenith)\r\nConnection: close\r\n"
                             "Accept-Encoding: identity\r\n\r\n", prefix, path, lwc_string_data(host), number);
            if (n < 0 || (size_t)n >= cap) {
                f->error = "HTTP request too large.";
            } else {
                f->socket = zenith_web_connect(lwc_string_data(host), (unsigned)number, f->tls);
                if (f->socket < 0) f->error = zenith_web_error(f->socket);
                else f->phase = SEND;
            }
        }
    }
    if (host) lwc_string_unref(host);
    if (port) lwc_string_unref(port);
    free(path);
}

static void send_request(struct web_fetch *f)
{
    size_t left = strlen(f->request) - f->sent;
    long n = zenith_web_send(f->socket, f->request + f->sent, left);
    if (n <= 0 || (size_t)n > left) {
        f->error = n < 0 ? zenith_web_error(n) : "Could not send HTTP request.";
    } else {
        f->sent += (size_t)n;
        if ((size_t)n == left) f->phase = RECEIVE;
    }
}

static bool redirect_fetch(struct web_fetch *f)
{
    nsurl *target = NULL;
    if (!*redirect_location || nsurl_join(f->url, redirect_location, &target) != NSERROR_OK) {
        f->error = "Invalid HTTP redirect.";
        return true;
    }
    enum nsurl_scheme_type scheme = nsurl_get_scheme_type(target);
    if (scheme != NSURL_SCHEME_HTTPS && (scheme != NSURL_SCHEME_HTTP || f->tls))
        f->error = "Redirect to an unsupported or insecure scheme.";
    else
        notify(f, FETCH_REDIRECT, nsurl_access(target));
    nsurl_unref(target);
    return true;
}

/* Emit only validated final headers. A callback may abort the fetch. */
static bool final_headers(struct web_fetch *f)
{
    struct response *r = &f->response;
    fetch_set_http_code(f->parent, r->status);
    if (redirect_status(r->status)) return redirect_fetch(f);
    if (r->status == 304) { notify(f, FETCH_NOTMODIFIED, NULL); return true; }
    if (f->only_2xx && (r->status < 200 || r->status >= 300)) {
        f->error = "HTTP request returned an unsuccessful status.";
        return true;
    }
    if (r->has_length && r->expected > MAX_HTTP) {
        f->error = "HTTP body exceeds 8 MiB."; return true;
    }
    for (char *line = head_copy; !f->aborted && *line;) {
        char *next = strstr(line, "\r\n");
        if (!next) break;
        block(f, FETCH_HEADER, line, (size_t)(next + 2 - line));
        line = next + 2;
    }
    if (r->status == 204 || r->status == 205) {
        if (r->chunked || r->expected) f->error = "Unexpected HTTP body for this status.";
        r->has_length = 1;
    }
    return f->aborted || f->error;
}

static bool receive_data(struct web_fetch *f)
{
    struct response *r = &f->response;
    long got = zenith_web_read(f->socket, wire, sizeof(wire));
    if (got < 0) { f->error = zenith_web_error(got); return true; }
    if (!got) {
        if (r->status && !r->chunked && !r->has_length) notify(f, FETCH_FINISHED, NULL);
        else f->error = "Truncated HTTP response.";
        return true;
    }
    if ((size_t)got > sizeof(wire)) { f->error = "Invalid transport read size."; return true; }
    size_t pos = 0;
    while (!r->status && pos < (size_t)got) {
        uint8_t byte = wire[pos++];
        if (!byte || r->used >= sizeof(header)-1) { f->error = "Invalid or oversized HTTP headers."; return true; }
        header[r->used++] = byte;
        if (r->used < 4 || memcmp(header + r->used - 4, "\r\n\r\n", 4)) continue;
        memcpy(head_copy, header, r->used);
        head_copy[r->used] = 0;
        redirect_location[0] = 0;
        if (parse_headers(r, 0, 1)) { f->error = parse_error; return true; }
        if (r->status < 200) {
            if (r->status == 101 || r->has_length || r->chunked || r->interim >= 4) {
                f->error = "Unsupported interim HTTP response."; return true;
            }
            unsigned interim = r->interim + 1;
            *r = (struct response){.interim = interim};
        } else if (final_headers(f)) {
            return true;
        }
    }
    if (!r->status) return false;
    unsigned long count = (unsigned long)got - pos;
    if (r->chunked) {
        if (chunks(r, wire + pos, count, MAX_HTTP, &count)) { f->error = parse_error; return true; }
    } else if (r->has_length && count > r->expected - r->received) {
        f->error = "HTTP body exceeds Content-Length."; return true;
    }
    if (count > MAX_HTTP - r->received) { f->error = "HTTP body exceeds 8 MiB."; return true; }
    r->received += count;
    if (count) block(f, FETCH_DATA, wire + pos, count);
    if (f->aborted) return true;
    if ((r->has_length && r->received == r->expected) || (r->chunked && r->phase == CHUNK_END)) {
        notify(f, FETCH_FINISHED, NULL);
        return true;
    }
    return false;
}

static void web_poll(lwc_string *scheme)
{
    struct web_fetch *f = active;
    if (!f || f->inside_poll || (f->tls != (strcmp(lwc_string_data(scheme), "https") == 0))) return;
    bool done = false;
    f->inside_poll = true;
    if (!f->error) {
        switch (f->phase) {
        case CONNECT: connect_fetch(f); break;
        case SEND: send_request(f); break;
        case RECEIVE: done = receive_data(f); break;
        }
    }
    if (f->error && !f->aborted) notify(f, FETCH_ERROR, f->error);
    f->inside_poll = false;
    if (done || f->aborted || f->error) retire(f);
}

nserror zenith_fetch_register(void)
{
    static const struct fetcher_operation_table ops = {
        .initialise = web_init, .acceptable = web_accept, .setup = web_setup,
        .start = web_start, .abort = web_abort, .free = web_free, .poll = web_poll,
        .finalise = web_fini
    };
    const char *schemes[] = {"http", "https"};
    for (size_t i = 0; i < 2; i++) {
        lwc_string *s = NULL;
        if (lwc_intern_string(schemes[i], strlen(schemes[i]), &s) != lwc_error_ok) return NSERROR_NOMEM;
        nserror error = fetcher_add(s, &ops);
        if (error != NSERROR_OK) { lwc_string_unref(s); return error; }
    }
    return NSERROR_OK;
}
