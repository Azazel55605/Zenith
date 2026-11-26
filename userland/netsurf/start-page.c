/* The internal page borrows a window's profile. Its contents never form part of a URL or a disk file. */
#define _POSIX_C_SOURCE 200809L
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "start-page.h"
#include "profile.h"
#include "../ui/ui.h"
#include "content/fetch.h"
#include "content/fetchers.h"

#ifndef NEBULA_RESOURCE_DIR
#define NEBULA_RESOURCE_DIR "/usr/share/zenith/netsurf/"
#endif
#define GUIDE_URL "file:///usr/share/zenith/netsurf/guide.html"

static const struct nebula_profile *user_profile;
static const struct zui_theme *user_theme;

static void escaped(FILE *output, const char *text)
{
    while (*text) {
        size_t plain = strcspn(text, "&<>\"'");
        if (plain) {
            fwrite(text, 1, plain, output);
            text += plain;
        }
        switch (*text) {
        case '&': fputs("&amp;", output); break;
        case '<': fputs("&lt;", output); break;
        case '>': fputs("&gt;", output); break;
        case '"': fputs("&quot;", output); break;
        case '\'': fputs("&#39;", output); break;
        default: return;
        }
        text++;
    }
}

static void links(FILE *output, const char *heading, const char *empty, const struct nebula_entry *entries,
                  unsigned count)
{
    fprintf(output, "<h2>%s</h2>\n", heading);
    unsigned listed = 0;
    for (unsigned i = 0; i < count; i++) {
        const struct nebula_entry *entry = &entries[i];
        if (!memchr(entry->url, 0, sizeof(entry->url)) || !memchr(entry->title, 0, sizeof(entry->title)) ||
            !nebula_profile_accepts(entry->url))
            continue;
        if (listed++ == 0) fputs("<ul class=\"start-list\">\n", output);
        fputs("<li><a href=\"", output);
        escaped(output, entry->url);
        fputs("\">", output);
        escaped(output, *entry->title ? entry->title : entry->url);
        fputs("</a></li>\n", output);
    }
    if (listed) fputs("</ul>\n", output);
    else fprintf(output, "<p>%s</p>\n", empty);
}

static bool style(FILE *output)
{
    FILE *css = fopen(NEBULA_RESOURCE_DIR "welcome.css", "r");
    if (!css) return false;
    fputs("<style>\n", output);
    char buffer[512];
    size_t total = 0, bytes;
    while ((bytes = fread(buffer, 1, sizeof(buffer), css)) != 0) {
        total += bytes;
        if (total > 8192) break;
        fwrite(buffer, 1, bytes, output);
    }
    bool ok = !ferror(css) && total <= 8192;
    fclose(css);
    if (user_theme) {
        fprintf(output, "html{background:#%06x;color:#%06x}h1{color:#%06x;border-color:#%06x}"
                "a{color:#%06x}.start-list a{background:#%06x}\n",
                user_theme->surface & 0xffffff, user_theme->text & 0xffffff, user_theme->text & 0xffffff,
                user_theme->accent & 0xffffff, user_theme->accent & 0xffffff, user_theme->raised & 0xffffff);
    }
    fputs("</style>\n", output);
    return ok;
}

static bool render(char **body, size_t *bytes)
{
    FILE *layout = fopen(NEBULA_RESOURCE_DIR "welcome.html", "r");
    if (!layout) return false;
    FILE *output = open_memstream(body, bytes);
    if (!output) { fclose(layout); return false; }

    bool ok = true, saw_profile = false, saw_style = false;
    size_t total = 0;
    char line[512];
    while (ok && fgets(line, sizeof(line), layout)) {
        total += strlen(line);
        if (total > 8192) break;
        if (!strncmp(line, "<!--NEBULA-STYLE-->", sizeof("<!--NEBULA-STYLE-->") - 1)) {
            ok = !saw_style && style(output);
            saw_style = true;
        } else if (!strncmp(line, "<!--NEBULA-PROFILE-->", sizeof("<!--NEBULA-PROFILE-->") - 1)) {
            if (saw_profile) { ok = false; break; }
            saw_profile = true;
            if (user_profile->bookmark_count > NEBULA_BOOKMARKS || user_profile->recent_count > NEBULA_RECENT) {
                ok = false;
                break;
            }
            links(output, "Bookmarks", "Use the star in the address bar to save a page.",
                  user_profile->bookmarks, user_profile->bookmark_count);
            links(output, "Recent pages", "Pages you visit will appear here.", user_profile->recent,
                  user_profile->recent_count);
        } else {
            fputs(line, output);
        }
    }
    ok = ok && total <= 8192 && !ferror(layout) && !ferror(output) && saw_style && saw_profile;
    fclose(layout);
    if (fclose(output)) ok = false;
    return ok;
}

struct home_fetch {
    struct fetch *parent;
    bool polling, aborted;
};
static struct home_fetch *current;

static bool is_start_page(const nsurl *url)
{
    return strcmp(nsurl_access(url), NEBULA_START_URL) == 0;
}
static void *setup(struct fetch *parent, nsurl *url, bool only_2xx, bool downgrade, const char *post,
                   const struct fetch_multipart_data *form, const char **headers)
{
    (void)only_2xx; (void)downgrade; (void)headers;
    const nsurl *referer;
    if (post || form || !is_start_page(url) || !fetch_navigation_context(parent, &referer)) return NULL;
    if (referer && !is_start_page(referer) && strcmp(nsurl_access(referer), GUIDE_URL)) return NULL;
    struct home_fetch *f = calloc(1, sizeof(*f));
    if (f) f->parent = parent;
    return f;
}
static bool start(void *ctx)
{
    if (current) return false;
    current = ctx;
    return true;
}
static void release(void *ctx)
{
    if (ctx == current) current = NULL;
    free(ctx);
}
static void finish(struct home_fetch *f)
{
    fetch_remove_from_queues(f->parent);
    fetch_free(f->parent);
}
static void cancel(void *ctx)
{
    struct home_fetch *f = ctx;
    f->aborted = true;
    if (!f->polling) finish(f);
}
static void send_bytes(struct home_fetch *f, fetch_msg_type type, const char *data, size_t bytes)
{
    if (f->aborted) return;
    fetch_msg message = {.type = type, .data.header_or_data = {(const uint8_t *)data, bytes}};
    fetch_send_callback(&message, f->parent);
}
static void poll(lwc_string *scheme)
{
    (void)scheme;
    struct home_fetch *f = current;
    if (!f || f->polling) return;
    f->polling = true;
    char *body = NULL;
    size_t bytes = 0;
    if (render(&body, &bytes)) {
        static const char type[] = "Content-Type: text/html; charset=utf-8\r\n";
        static const char cache[] = "Cache-Control: no-store\r\n";
        fetch_set_http_code(f->parent, 200);
        send_bytes(f, FETCH_HEADER, type, sizeof(type) - 1);
        send_bytes(f, FETCH_HEADER, cache, sizeof(cache) - 1);
        send_bytes(f, FETCH_DATA, body, bytes);
        if (!f->aborted) {
            fetch_msg message = {.type = FETCH_FINISHED};
            fetch_send_callback(&message, f->parent);
        }
    } else {
        fetch_msg message = {.type = FETCH_ERROR, .data.error = "Could not open the Nebula start page."};
        fetch_send_callback(&message, f->parent);
    }
    free(body);
    finish(f);
}
static bool initialise(lwc_string *scheme) { (void)scheme; return true; }
static void finalise(lwc_string *scheme) { (void)scheme; user_profile = NULL; user_theme = NULL; }

nserror nebula_start_register(const struct nebula_profile *profile, const struct zui_theme *theme)
{
    static const struct fetcher_operation_table operations = {
        .initialise = initialise, .acceptable = is_start_page, .setup = setup, .start = start,
        .abort = cancel, .free = release, .poll = poll, .finalise = finalise
    };
    if (!profile) return NSERROR_BAD_PARAMETER;
    lwc_string *scheme;
    if (lwc_intern_string("nebula", 6, &scheme) != lwc_error_ok) return NSERROR_NOMEM;
    nserror result = fetcher_add(scheme, &operations);
    if (result != NSERROR_OK) {
        lwc_string_unref(scheme);
    } else {
        user_profile = profile;
        user_theme = theme;
    }
    return result;
}
