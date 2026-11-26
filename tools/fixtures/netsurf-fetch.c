/* Host NetSurf fetcher regression. Real URL parsing and callback ABI, scripted socket. */
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "content/fetch.h"
#include "content/fetchers.h"
#include "utils/corestrings.h"
#include "../../userland/netsurf/fetch.h"
#include "../../userland/netsurf/fetch-transport.h"

static const struct fetcher_operation_table *ops;
static lwc_string *schemes[2];
static size_t scheme_count;
struct fetch {
    void *context;
    char body[128], redirect[512], error[128];
    unsigned length, headers, finished, removed;
    long code;
    bool cancel_on_data;
};
static const char *wire;
static char request[1024];
static size_t request_length, fragment;
static int operations, closes, connects;
static long connect_result;
static bool secure;

nserror fetcher_add(lwc_string *scheme, const struct fetcher_operation_table *table)
{
    assert(scheme_count < 2 && table->initialise(scheme));
    schemes[scheme_count++] = scheme;
    ops = table;
    return NSERROR_OK;
}
void fetch_remove_from_queues(struct fetch *job) { assert(!job->removed++); }
void fetch_free(struct fetch *job)
{
    assert(job->removed && job->context);
    ops->free(job->context);
    job->context = NULL;
}
void fetch_set_http_code(struct fetch *job, long code) { job->code = code; }
void fetch_send_callback(const fetch_msg *message, struct fetch *job)
{
    switch (message->type) {
    case FETCH_DATA: {
        size_t size = message->data.header_or_data.len;
        assert(job->length + size < sizeof(job->body));
        memcpy(job->body + job->length, message->data.header_or_data.buf, size);
        job->length += size;
        if (job->cancel_on_data) ops->abort(job->context);
        break;
    }
    case FETCH_HEADER: job->headers++; break;
    case FETCH_FINISHED: job->finished++; break;
    case FETCH_REDIRECT: snprintf(job->redirect, sizeof(job->redirect), "%s", message->data.redirect); break;
    case FETCH_ERROR: snprintf(job->error, sizeof(job->error), "%s", message->data.error); break;
    default: assert(0);
    }
}
long zenith_web_connect(const char *host, unsigned port, bool tls)
{
    assert(!strcmp(host, "example.org") && (port == 81 || port == 443));
    operations++; connects++; secure = tls;
    return connect_result;
}
long zenith_web_send(long socket, const void *data, size_t size)
{
    assert(socket == 1 && request_length + size < sizeof(request));
    if (size > 7) size = 7;
    memcpy(request + request_length, data, size);
    request_length += size;
    request[request_length] = 0;
    operations++;
    return size;
}
long zenith_web_read(long socket, void *data, size_t size)
{
    assert(socket == 1);
    if (size > fragment) size = fragment;
    if (size > strlen(wire)) size = strlen(wire);
    memcpy(data, wire, size);
    wire += size;
    operations++;
    return size;
}
void zenith_web_close(long socket) { assert(socket == 1); closes++; }
const char *zenith_web_error(long code) { return code == -28 ? "transfer limit reached" : "certificate check failed"; }

static const char *http = "http://example.org:81/p?q=1#fragment";
static const char *https = "https://example.org/p";
static void configure(const char *response)
{
    wire = response;
    closes = connects = 0;
    request_length = 0;
    request[0] = 0;
    fragment = 3;
    connect_result = 1;
}
static void make(struct fetch *job, const char *address)
{
    nsurl *url;
    *job = (struct fetch){0};
    assert(nsurl_create(address, &url) == NSERROR_OK && ops->acceptable(url));
    job->context = ops->setup(job, url, false, false, NULL, NULL, NULL);
    nsurl_unref(url);
    assert(job->context);
}
static void complete(struct fetch *job)
{
    for (size_t tries = 0; job->context && tries < 500; tries++) {
        for (size_t i = 0; i < scheme_count; i++) {
            int before = operations;
            ops->poll(schemes[i]);
            assert(operations - before <= 1);
        }
    }
    assert(!job->context && job->removed == 1);
}
static struct fetch fetch(const char *url)
{
    struct fetch job;
    make(&job, url);
    assert(ops->start(job.context));
    complete(&job);
    return job;
}
int main(void)
{
    assert(corestrings_init() == NSERROR_OK);
    assert(zenith_fetch_register() == NSERROR_OK && scheme_count == 2);
    configure("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 200 OK\r\nContent-Type: text/html\r\n"
              "Transfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n3\r\ndef\r\n0\r\nX-End: yes\r\n\r\n");
    struct fetch job = fetch(http);
    assert(job.code == 200 && job.headers == 4 && job.finished == 1 && !strcmp(job.body, "abcdef"));
    assert(!secure && closes == 1 && strstr(request, "GET /p?q=1 HTTP/1.1\r\nHost: example.org:81\r\n"));
    puts("PASS partial sends, fragmented interim headers/chunks/trailers and URL target");

    const char *errors[] = {
        "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n123",
        "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 9\r\n\r\n",
        "HTTP/1.1 200 OK\r\nContent-Length: 8388609\r\n\r\n",
        "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n4\r\nab",
        "HTTP/1.1 302 Found\r\nLocation: file:///etc/passwd\r\n\r\n",
        "HTTP/1.1 302 Found\r\nLocation: http://example.org/login\r\n\r\n",
    };
    for (size_t i = 0; i < sizeof(errors) / sizeof(*errors); i++) {
        configure(errors[i]);
        job = fetch(https);
        assert(secure && job.error[0] && !job.finished && !job.redirect[0] && closes == 1);
    }
    configure("HTTP/1.1 301 Moved\r\nLocation: ../next\r\n\r\n");
    job = fetch(https);
    assert(!strcmp(job.redirect, "https://example.org/next") && !job.error[0] && closes == 1);
    configure("");
    job = fetch("https://reader:secret@example.org/");
    assert(!connects && !closes && job.error[0]);
    puts("PASS malformed and truncated responses, body limits and redirect policy");

    configure("HTTP/1.0 200 OK\r\n\r\nbody");
    job = fetch(https);
    assert(job.finished == 1 && !strcmp(job.body, "body") && secure && closes == 1);
    for (int i = 0; i < 2; i++) {
        configure("");
        connect_result = i ? -28 : -5;
        job = fetch(https);
        assert(connects == 1 && secure && !closes && !job.finished);
        assert(!strcmp(job.error, i ? "transfer limit reached" : "certificate check failed"));
    }

    configure("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n12345");
    make(&job, http);
    struct fetch queued;
    make(&queued, https);
    assert(ops->start(job.context) && !ops->start(queued.context));
    ops->abort(queued.context);
    job.cancel_on_data = true;
    complete(&job);
    assert(!job.finished && job.length && closes == 1 && connects == 1 && !queued.context);
    configure("HTTP/1.1 204 No Content\r\n\r\n");
    job = fetch(http);
    assert(job.finished == 1 && closes == 1);
    puts("PASS TLS and resource errors, queued/active cancellation and subsequent navigation");

    for (size_t i = 0; i < scheme_count; i++) {
        ops->finalise(schemes[i]);
        lwc_string_unref(schemes[i]);
    }
    assert(corestrings_fini() == NSERROR_OK);
    return 0;
}
