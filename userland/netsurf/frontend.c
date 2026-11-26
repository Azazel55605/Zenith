/* Nebula, Zenith's browser: the native frontend for the NetSurf 3.11 core. The core owns navigation, page
 * history, the DOM, layout, and page input dispatch; this file owns the window around the page
 * (docs/components/BROWSER.md, docs/design/browser-concept.svg, stages B1-B3). */
#include "frontend.h"
#include "../include/zenith_files.h"
#include "../include/zenith_settings.h"
#include "../ui/text.h"
#include "desktop/browser_history.h"
#include "desktop/download.h"
#include "desktop/search.h"
#include "fetch-transport.h"
#include "fetch.h"
#include "netsurf/bitmap.h"
#include "netsurf/browser_window.h"
#include "netsurf/clipboard.h"
#include "netsurf/download.h"
#include "netsurf/fetch.h"
#include "netsurf/keypress.h"
#include "netsurf/misc.h"
#include "netsurf/mouse.h"
#include "netsurf/netsurf.h"
#include "netsurf/search.h"
#include "netsurf/window.h"
#include "utils/ascii.h"
#include "utils/file.h"
#include "utils/messages.h"
#include "utils/nsoption.h"
#include "utils/nsurl.h"
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <unistd.h>

#define RESOURCES "/usr/share/zenith/netsurf/"
#define START_URL "file://" RESOURCES "welcome.html"
#define GUIDE_URL "file://" RESOURCES "guide.html"
/* Kernel 0.0.81 lets one browser run open six connections; Page info reports use against it. */
#define CONNECTION_BUDGET 6
/* 640x480 is the largest size a window may open at; it can grow after. */
enum { START_W = 640, START_H = 480, TOOLBAR = 52, LIMIT_BAR = 40, FIND_BAR = 52, BUTTON = 36 };
enum {
    BACK = 1, FORWARD, RELOAD, MENU, INFO, ADDRESS,
    ZOOM_OUT, ZOOM_RESET, ZOOM_IN, MENU_FIND, MENU_COPY, MENU_INFO, MENU_GUIDE,
    INFO_COPY, CARD_COPY, CARD_CLOSE, FIND_PREV, FIND_NEXT, FIND_CLOSE, LIMIT_CLOSE
};
enum { POPOVER_NONE, POPOVER_MENU, POPOVER_INFO };

struct gui_window {
    struct browser_window *browser;
    struct zui ui;
    struct zui_input address, find;
    char location[2048], current[2048], status[256], note[64], find_text[128];
    long handle;
    int sx, sy, ex, ey;
    int caret_x, caret_y, caret_height;
    bool material, address_focus, select_address, page_pressed, dragging;
    bool page_dirty, loading, present_document, done;
    int press_x, press_y;
    int popover;                        /* POPOVER_*: the menu or Page info, under the toolbar */
    bool find_open, find_focus, find_missing, overlays_drawn;
    struct zui_rect popover_rect;       /* where the open popover or card was drawn, for hit tests */
    long note_until, strip_at;          /* a transient pill message; the progress line's last frame */
    unsigned limit_seen;                /* refused requests when the limit bar was last closed */
    float scale;
    struct {                            /* a link the engine cannot show: B6 turns it into a download */
        bool open;
        char name[96], detail[96], url[2048];
        download_context *ctx;
    } card;
};
static struct gui_window window;
static uint32_t starting_pixels[START_W * START_H];
static const float zoom_steps[] = {.5f, .67f, .8f, .9f, 1.f, 1.1f, 1.25f, 1.5f, 1.75f, 2.f, 2.5f, 3.f};

struct alarm {
    struct alarm *next;
    long due;
    void (*callback)(void *);
    void *arg;
};
static struct alarm *alarms;

static nserror schedule(int delay, void (*cb)(void *), void *arg)
{
    struct alarm **slot = &alarms, *a = NULL;
    while (*slot && ((*slot)->callback != cb || (*slot)->arg != arg))
        slot = &(*slot)->next;
    if (*slot) {
        a = *slot;
        *slot = a->next;
    }
    if (delay < 0) {
        free(a);
        return NSERROR_OK;
    }
    if (!a && !(a = malloc(sizeof(*a))))
        return NSERROR_NOMEM;
    a->callback = cb;
    a->arg = arg;
    a->due = zui_motion_clock() + delay;
    slot = &alarms;
    while (*slot && (*slot)->due <= a->due)
        slot = &(*slot)->next;
    a->next = *slot;
    *slot = a;
    return NSERROR_OK;
}

static void run_alarms(void)
{
    /* Return to the window queue even when a zero-delay callback re-arms itself. */
    long now = zui_motion_clock();
    for (int i = 0; i < 16 && alarms && alarms->due <= now; i++) {
        struct alarm *a = alarms;
        alarms = a->next;
        void (*cb)(void *) = a->callback;
        void *arg = a->arg;
        free(a);
        cb(arg);
    }
}

static void copy_text(char *dst, size_t capacity, const char *s)
{
    size_t len = s ? strnlen(s, capacity) : 0;
    if (len == capacity) {
        len = capacity - 1;
        while (len && ((unsigned char)s[len] & 0xc0) == 0x80)
            len--;
    }
    if (len)
        memcpy(dst, s, len);
    dst[len] = 0;
}

static const char *file_type(const char *path)
{
    static const struct {
        const char *suffix, *mime;
    } types[] = {{"html", "text/html"},   {"htm", "text/html"},     {"css", "text/css"},
                 {"txt", "text/plain"},   {"gif", "image/gif"},     {"bmp", "image/bmp"},
                 {"ico", "image/x-icon"}, {"png", "image/png"},     {"jpg", "image/jpeg"},
                 {"jpeg", "image/jpeg"},  {"svg", "image/svg+xml"}, {"woff", "font/woff"},
                 {"woff2", "font/woff2"}};
    const char *ext = strrchr(path, '.');
    if (ext)
        for (size_t i = 0; i < sizeof(types) / sizeof(types[0]); i++)
            if (!strcasecmp(ext + 1, types[i].suffix))
                return types[i].mime;
    return "application/octet-stream";
}

static nsurl *resource_url(const char *resource)
{
    /* resource: only asks for the core's built-in list of relative asset names. */
    char path[256];
    if (!resource || resource[0] == '/' || strstr(resource, ".."))
        return NULL;
    int n = snprintf(path, sizeof(path), RESOURCES "%s", resource);
    nsurl *url = NULL;
    if (n > 0 && (size_t)n < sizeof(path) && access(path, R_OK) == 0)
        netsurf_path_to_nsurl(path, &url);
    return url;
}

/* The page starts under the toolbar, and under the limit bar while that is shown. */
static bool limit_bar(const struct gui_window *g)
{
    return zenith_web_refused() > g->limit_seen;
}
static int page_top(const struct gui_window *g)
{
    return TOOLBAR + (limit_bar(g) ? LIMIT_BAR : 0);
}
static int viewport_height(const struct gui_window *g)
{
    int h = g->ui.height - page_top(g);
    return h > 1 ? h : 1;
}
static int bounded(long x, int max)
{
    return x < 0 || max < 0 ? 0 : x > max ? max : (int)x;
}
static void scroll_to(struct gui_window *g, long x, long y)
{
    int sx = bounded(x, g->ex - g->ui.width), sy = bounded(y, g->ey - viewport_height(g));
    if (sx != g->sx || sy != g->sy) {
        g->sx = sx;
        g->sy = sy;
        g->page_dirty = true;
    }
}
static void extent(struct gui_window *g)
{
    int w = 0, h = 0;
    if (browser_window_get_extents(g->browser, true, &w, &h) == NSERROR_OK) {
        g->ex = w;
        g->ey = h;
        scroll_to(g, g->sx, g->sy);
    }
}
/* Chrome and page both: overlays sit on the page, so changing one repaints what is under it. */
static void repaint(struct gui_window *g)
{
    g->ui.dirty = 1;
    g->page_dirty = true;
}

static struct gui_window *create_window(struct browser_window *bw, struct gui_window *existing,
                                        gui_window_create_flags flags)
{
    (void)existing;
    if (window.browser)
        return NULL;
    window.browser = bw;
    window.page_dirty = true;
    window.address_focus = (flags & GW_CREATE_FOCUS_LOCATION) != 0;
    return &window;
}
static void destroy_window(struct gui_window *g)
{
    g->browser = NULL;
    g->done = true;
}
static nserror invalidate(struct gui_window *g, const struct rect *rect)
{
    (void)rect;
    g->page_dirty = true;
    return NSERROR_OK;
}
static bool get_scroll(struct gui_window *g, int *x, int *y)
{
    *x = g->sx;
    *y = g->sy;
    return true;
}
static nserror set_scroll(struct gui_window *g, const struct rect *r)
{
    long x = g->sx, y = g->sy;
    if (r->x0 == r->x1 || r->x0 < x)
        x = r->x0;
    else if (r->x1 > x + g->ui.width)
        x = (long)r->x1 - g->ui.width;
    if (r->y0 == r->y1 || r->y0 < y)
        y = r->y0;
    else if (r->y1 > y + viewport_height(g))
        y = (long)r->y1 - viewport_height(g);
    scroll_to(g, x, y);
    return NSERROR_OK;
}
static nserror dimensions(struct gui_window *g, int *w, int *h)
{
    *w = g->ui.width;
    *h = viewport_height(g);
    return NSERROR_OK;
}
static nserror event(struct gui_window *g, enum gui_window_event ev)
{
    switch (ev) {
    case GW_EVENT_UPDATE_EXTENT:
        extent(g);
        break;
    case GW_EVENT_START_THROBBER:
        g->loading = true;
        g->strip_at = 0;
        break;
    case GW_EVENT_STOP_THROBBER:
        g->loading = false;
        break;
    case GW_EVENT_NEW_CONTENT:
        g->page_dirty = true;
        g->present_document = true;
        g->caret_height = 0;
        if (g->find_open && g->find.length)
            g->find_missing = false;
        break;
    case GW_EVENT_REMOVE_CARET:
        g->caret_height = 0;
        g->page_dirty = true;
        break;
    default:
        break;
    }
    g->ui.dirty = 1;
    return NSERROR_OK;
}
static void title(struct gui_window *g, const char *text)
{
    char safe[80];
    copy_text(safe, sizeof(safe), text);
    for (char *p = safe; *p; p++)
        if ((unsigned char)*p < 32)
            *p = ' ';
    if (*safe)
        zenith_gui_set_title(g->handle, safe, strlen(safe));
}
static void reset_address(struct gui_window *g, const char *text)
{
    copy_text(g->location, sizeof(g->location), text);
    zui_input_init(&g->address, g->location, sizeof(g->location));
    g->ui.dirty = 1;
}
static nserror set_url(struct gui_window *g, nsurl *url)
{
    copy_text(g->current, sizeof(g->current), nsurl_access(url));
    if (!g->address_focus)
        reset_address(g, g->current);
    return NSERROR_OK;
}
/* The core's status is a hovered link's address or a loading message; the pill shows it over the page. */
static void status(struct gui_window *g, const char *s)
{
    if (!s)
        s = "";
    if (strncmp(g->status, s, sizeof(g->status) - 1)) {
        copy_text(g->status, sizeof(g->status), s);
        repaint(g);
    }
}
static void note(struct gui_window *g, const char *s)
{
    copy_text(g->note, sizeof(g->note), s);
    g->note_until = zui_motion_clock() + 2000;
    repaint(g);
}
static void navigation_error(struct gui_window *g, nserror error)
{
    status(g, error == NSERROR_NO_FETCH_HANDLER
                  ? "Unsupported address. Use an HTTP or HTTPS address or open a local file."
                  : messages_get_errorcode(error));
}
static nserror launch_external(nsurl *url)
{
    (void)url;
    navigation_error(&window, NSERROR_NO_FETCH_HANDLER);
    return NSERROR_NO_FETCH_HANDLER;
}
static void caret(struct gui_window *g, int x, int y, int height, const struct rect *clip)
{
    (void)clip;
    g->caret_x = x;
    g->caret_y = y;
    g->caret_height = height;
    g->page_dirty = true;
}

/* Copying: the kernel keeps the clipboard; a window may set it but not read it back. */
static void copy_to_clipboard(const char *text, size_t bytes)
{
    if (bytes && window.handle > 0)
        zenith_call(639, (unsigned long)window.handle, (unsigned long)text, bytes);
}
static void clipboard_get(char **buffer, size_t *length)
{
    *buffer = NULL;
    *length = 0;
}
static void clipboard_set(const char *buffer, size_t length, nsclipboard_styles styles[], int n_styles)
{
    (void)styles;
    (void)n_styles;
    copy_to_clipboard(buffer, length);
}

/* Find on page: the core highlights every match and reports whether there was one. */
static void search_status(bool found, void *p)
{
    struct gui_window *g = p;
    if (g->find_missing != !found) {
        g->find_missing = !found;
        repaint(g);
    }
}
static void search_hourglass(bool active, void *p)
{
    (void)active;
    (void)p;
}
static void search_recent(const char *string, void *p)
{
    (void)string;
    (void)p;
}
static void search_state(bool active, void *p)
{
    (void)active;
    (void)p;
}
static void find(struct gui_window *g, bool backwards)
{
    if (!g->find.length) {
        browser_window_search_clear(g->browser);
        g->find_missing = false;
        return;
    }
    browser_window_search(g->browser, g, SEARCH_FLAG_SHOWALL | (backwards ? SEARCH_FLAG_BACKWARDS : SEARCH_FLAG_FORWARDS),
                          g->find_text);
}
static void close_find(struct gui_window *g)
{
    if (!g->find_open)
        return;
    g->find_open = g->find_focus = g->find_missing = false;
    browser_window_search_clear(g->browser);
    repaint(g);
}

/* A link to a file the engine cannot show. Zenith has no downloads yet (N3): the card names the file and
 * offers its address; the transfer is stopped at its first data. */
static struct gui_download_window *download_create(download_context *ctx, struct gui_window *parent)
{
    struct gui_window *g = parent ? parent : &window;
    const char *name = download_context_get_filename(ctx), *mime = download_context_get_mime_type(ctx);
    unsigned long long bytes = download_context_get_total_length(ctx);
    copy_text(g->card.name, sizeof(g->card.name), name && *name ? name : "Unnamed file");
    char size[32] = "";
    if (bytes >= 1024ull * 1024)
        snprintf(size, sizeof(size), " \xc2\xb7 %llu.%llu MB", bytes / (1024 * 1024), bytes % (1024 * 1024) * 10 / (1024 * 1024));
    else if (bytes)
        snprintf(size, sizeof(size), " \xc2\xb7 %llu KB", (bytes + 1023) / 1024);
    snprintf(g->card.detail, sizeof(g->card.detail), "%s%s", mime && *mime ? mime : "File", size);
    nsurl *url = download_context_get_url(ctx);
    copy_text(g->card.url, sizeof(g->card.url), url ? nsurl_access(url) : "");
    g->card.open = true;
    g->card.ctx = ctx;
    g->popover = POPOVER_NONE;
    repaint(g);
    return (struct gui_download_window *)g;
}
static void download_release(void *p)
{
    struct gui_window *g = p;
    if (g->card.ctx) {
        download_context_destroy(g->card.ctx);
        g->card.ctx = NULL;
    }
}
static nserror download_data(struct gui_download_window *dw, const char *data, unsigned int size)
{
    (void)data;
    (void)size;
    /* The core aborts the fetch on an error; the window owns the context and frees it after the callback. */
    schedule(0, download_release, dw);
    return NSERROR_NOT_IMPLEMENTED;
}
static void download_finished(struct gui_download_window *dw)
{
    schedule(-1, download_release, dw);
    download_release(dw);
}
static void download_error(struct gui_download_window *dw, const char *message)
{
    (void)message;
    download_finished(dw);
}

static struct gui_window_table window_ops = {.create = create_window,
                                             .destroy = destroy_window,
                                             .invalidate = invalidate,
                                             .get_scroll = get_scroll,
                                             .set_scroll = set_scroll,
                                             .get_dimensions = dimensions,
                                             .event = event,
                                             .set_title = title,
                                             .set_url = set_url,
                                             .set_status = status,
                                             .place_caret = caret};
static struct gui_misc_table misc_ops = {.schedule = schedule, .launch_url = launch_external};
static struct gui_fetch_table fetch_ops = {.filetype = file_type, .get_resource_url = resource_url};
static struct gui_clipboard_table clipboard_ops = {.get = clipboard_get, .set = clipboard_set};
static struct gui_search_table search_ops = {.status = search_status,
                                             .hourglass = search_hourglass,
                                             .add_recent = search_recent,
                                             .forward_state = search_state,
                                             .back_state = search_state};
static struct gui_download_table download_ops = {
    .create = download_create, .data = download_data, .error = download_error, .done = download_finished};
static struct netsurf_table operations = {.window = &window_ops,
                                          .misc = &misc_ops,
                                          .clipboard = &clipboard_ops,
                                          .download = &download_ops,
                                          .search = &search_ops,
                                          .bitmap = &zenith_bitmaps,
                                          .layout = &zenith_layout,
                                          .fetch = &fetch_ops};

/* ---- What the address says ------------------------------------------------------------------------------ */

enum { PAGE_INTERNAL, PAGE_LOCAL, PAGE_INSECURE, PAGE_SECURE };
static int page_kind(const char *url)
{
    if (!strncasecmp(url, "https://", 8))
        return PAGE_SECURE;
    if (!strncasecmp(url, "http://", 7))
        return PAGE_INSECURE;
    if (!strncasecmp(url, "file://", 7))
        return strncmp(url + 7, RESOURCES, sizeof(RESOURCES) - 1) ? PAGE_LOCAL : PAGE_INTERNAL;
    return PAGE_INTERNAL;
}
/* Splits a web address into its host and the rest (path, query, fragment); local files show their path. */
static void address_parts(const char *url, const char **host, size_t *host_len, const char **rest)
{
    const char *p = strstr(url, "://");
    if (!p) {
        *host = url;
        *host_len = strlen(url);
        *rest = url + *host_len;
        return;
    }
    p += 3;
    if (page_kind(url) == PAGE_LOCAL || page_kind(url) == PAGE_INTERNAL) {
        *host = p;
        *host_len = 0;
        *rest = p;
        return;
    }
    const char *at = p;
    while (*at && !strchr("/?#", *at))
        at++;
    *host = p;
    *host_len = (size_t)(at - p);
    *rest = (*at == '/' && !at[1]) ? at + 1 : at;
}
static const char *page_icon(int kind)
{
    return kind == PAGE_SECURE ? "lock" : kind == PAGE_INSECURE ? "lock-open" : kind == PAGE_LOCAL ? "file" : "globe";
}

/* ---- Drawing --------------------------------------------------------------------------------------------- */

static struct zui_rect address_rect(const struct gui_window *g)
{
    return (struct zui_rect){8 + 3 * (BUTTON + 4) + 4, 8, g->ui.width - (8 + 3 * (BUTTON + 4) + 4) - (BUTTON + 16), 36};
}
static struct zui_rect menu_button(const struct gui_window *g)
{
    return (struct zui_rect){g->ui.width - BUTTON - 8, 8, BUTTON, BUTTON};
}
static bool is_hovered(const struct zui *u, int id)
{
    return u->hover >= 0 && u->hover < u->count && u->controls[u->hover].id == id;
}
static bool is_pressed(const struct zui *u, int id)
{
    return u->pressed >= 0 && u->pressed < u->count && u->controls[u->pressed].id == id;
}
/* Fits text into a width, ending in an ellipsis when it does not; returns the bytes drawn. */
static int fit(struct zui *u, int x, int y, const char *text, size_t bytes, int width, const struct zui_font *font,
               unsigned int color)
{
    if (bytes > 512)
        bytes = 512;
    if (zui_measure(text, bytes, font) <= width) {
        zui_text(u, x, y, text, bytes, font, color);
        return zui_measure(text, bytes, font);
    }
    int dots = zui_measure("\xe2\x80\xa6", 3, font);
    while (bytes && zui_measure(text, bytes, font) + dots > width) {
        bytes--;
        while (bytes && ((unsigned char)text[bytes] & 0xc0) == 0x80)
            bytes--;
    }
    zui_text(u, x, y, text, bytes, font, color);
    int w = zui_measure(text, bytes, font);
    zui_text(u, x + w, y, "\xe2\x80\xa6", 3, font, color);
    return w + dots;
}
/* A toolbar or bar button: an icon on the bar, a soft tint under the pointer. Registered for hit testing,
 * drawn here (the shared field-style icon button is meant for forms). */
static void icon_button(struct zui *u, int id, struct zui_rect r, const char *icon, bool enabled)
{
    zui_add(u, id, ZUI_BUTTON, r, "", 0, enabled);
    if (enabled && (is_pressed(u, id) || is_hovered(u, id)))
        zui_round_alpha(u, r, 8, u->theme.text, is_pressed(u, id) ? 46 : 26);
    zui_icon(u, icon, r.x + (r.w - 16) / 2, r.y + (r.h - 16) / 2, 16, enabled ? ZUI_ICON_TEXT : ZUI_ICON_DISABLED);
}
static void divider(struct zui *u, int x, int y, int w)
{
    zui_fill(u, (struct zui_rect){x, y, w, 1}, u->theme.border);
}
/* An opaque popover card with a soft edge shadow. */
static void card(struct zui *u, struct zui_rect r)
{
    zui_round_alpha(u, (struct zui_rect){r.x - 1, r.y + 1, r.w + 2, r.h + 3}, 13, 0, 40);
    zui_round_alpha(u, (struct zui_rect){r.x - 3, r.y + 3, r.w + 6, r.h + 6}, 15, 0, 18);
    zui_round(u, r, 12, u->theme.border);
    zui_round(u, (struct zui_rect){r.x + 1, r.y + 1, r.w - 2, r.h - 2}, 11, u->theme.raised);
}

static void draw_address(struct gui_window *g)
{
    struct zui *u = &g->ui;
    struct zui_rect field = address_rect(g);
    if (field.w < 60)
        return;
    if (g->address_focus) {
        zui_input_draw(u, field, &g->address, 1);
        if (g->select_address && g->address.length) {
            zui_clip(u, (struct zui_rect){field.x + 8, field.y + 4, field.w - 16, field.h - 8});
            int w = zui_measure(g->location, g->address.length, &zui_body);
            zui_fill(u, (struct zui_rect){field.x + 8, field.y + 6, w, field.h - 12}, u->theme.accent);
            zui_text(u, field.x + 8, field.y + (field.h - zui_body.height) / 2, g->location, g->address.length,
                     &zui_body, u->theme.on_accent);
            zui_clip(u, (struct zui_rect){0, 0, u->width, u->height});
        }
        return;
    }
    /* At rest: what the page is (the lock opens Page info), then the host in full and the rest quieter. */
    zui_round(u, field, 8, is_hovered(u, ADDRESS) ? u->theme.secondary : u->theme.border);
    zui_round(u, (struct zui_rect){field.x + 1, field.y + 1, field.w - 2, field.h - 2}, 7, u->theme.raised);
    zui_add(u, ADDRESS, ZUI_BUTTON, (struct zui_rect){field.x + 36, field.y, field.w - 36, field.h}, "", 0, 1);
    int kind = page_kind(g->current);
    struct zui_rect lock = {field.x + 4, field.y + 4, 28, 28};
    zui_add(u, INFO, ZUI_BUTTON, lock, "", 0, 1);
    if (is_hovered(u, INFO) || g->popover == POPOVER_INFO)
        zui_round_alpha(u, lock, 6, u->theme.text, 26);
    zui_icon(u, page_icon(kind), lock.x + 6, lock.y + 6, 16, kind == PAGE_INSECURE ? ZUI_ICON_DISABLED : ZUI_ICON_TEXT);
    int x = field.x + 38, y = field.y + (field.h - zui_body.height) / 2, room = field.x + field.w - 12 - x;
    zui_clip(u, (struct zui_rect){x, field.y, room > 0 ? room : 0, field.h});
    if (kind == PAGE_INSECURE) {
        int w = zui_measure("Not secure", 10, &zui_caption);
        zui_text(u, x, field.y + (field.h - zui_caption.height) / 2, "Not secure", 10, &zui_caption, u->theme.secondary);
        zui_fill(u, (struct zui_rect){x + w + 8, field.y + 10, 1, field.h - 20}, u->theme.border);
        x += w + 17;
        room -= w + 17;
    }
    const char *host, *rest;
    size_t host_len;
    if (kind == PAGE_INTERNAL && !strncmp(g->current, START_URL, sizeof(START_URL) - 1)) {
        host = "Nebula";
        host_len = 6;
        rest = "";
    } else
        address_parts(g->current, &host, &host_len, &rest);
    int used = host_len ? fit(u, x, y, host, host_len, room, &zui_label, u->theme.text) : 0;
    if (room - used > 24 && *rest)
        fit(u, x + used, y, rest, strlen(rest), room - used, &zui_body, u->theme.secondary);
    zui_clip(u, (struct zui_rect){0, 0, u->width, u->height});
}

static void draw_toolbar(struct gui_window *g)
{
    struct zui *u = &g->ui;
    struct zui_rect bar = {0, 0, u->width, TOOLBAR};
    if (g->material && zui_material_surface(u, bar, 0, ZUI_SURFACE_NAVIGATION, 2))
        memset(u->pixels, 0, (size_t)u->width * TOOLBAR * 4);
    else
        zui_fill(u, bar, u->theme.subtle);
    zui_fill(u, (struct zui_rect){0, TOOLBAR - 1, u->width, 1}, u->theme.border);
    icon_button(u, BACK, (struct zui_rect){8, 8, BUTTON, BUTTON}, "arrow-left",
                browser_window_history_back_available(g->browser));
    icon_button(u, FORWARD, (struct zui_rect){8 + BUTTON + 4, 8, BUTTON, BUTTON}, "arrow-right",
                browser_window_history_forward_available(g->browser));
    icon_button(u, RELOAD, (struct zui_rect){8 + 2 * (BUTTON + 4), 8, BUTTON, BUTTON}, g->loading ? "x" : "rotate-cw", 1);
    draw_address(g);
    icon_button(u, MENU, menu_button(g), "ellipsis", 1);
    if (g->popover == POPOVER_MENU && !is_hovered(u, MENU))
        zui_round_alpha(u, menu_button(g), 8, u->theme.text, 26);
}

/* Loading: a 3 px accent segment travels along the toolbar's lower edge (the core gives no fraction). */
static void draw_progress(struct gui_window *g, long now)
{
    struct zui *u = &g->ui;
    struct zui_rect line = {0, TOOLBAR - 3, u->width, 2};
    if (g->material && u->material_count)
        memset(u->pixels + (long)line.y * u->width, 0, (size_t)u->width * 2 * 4);
    else
        zui_fill(u, line, u->theme.subtle);
    if (!g->loading)
        return;
    int span = u->width / 3 > 40 ? u->width / 3 : 40, travel = u->width + span;
    int x = (int)((now / 3) % travel) - span;
    zui_round(u, (struct zui_rect){x < 0 ? 0 : x, line.y, x < 0 ? span + x : span, 2}, 1, u->theme.accent);
}

static void draw_limit_bar(struct gui_window *g)
{
    struct zui *u = &g->ui;
    struct zui_rect bar = {0, TOOLBAR, u->width, LIMIT_BAR};
    zui_fill(u, bar, u->theme.raised);
    zui_fill(u, (struct zui_rect){0, TOOLBAR + LIMIT_BAR - 1, u->width, 1}, u->theme.border);
    zui_icon(u, "triangle-alert", 14, bar.y + 12, 16, ZUI_ICON_NORMAL);
    char text[192];
    unsigned refused = zenith_web_refused();
    snprintf(text, sizeof(text), "%u request%s not made: all %d connections are used. Restart Nebula to load more.",
             refused, refused == 1 ? " was" : "s were", CONNECTION_BUDGET);
    zui_clip(u, (struct zui_rect){40, bar.y, u->width - 88, bar.h});
    fit(u, 40, bar.y + (bar.h - zui_body.height) / 2, text, strlen(text), u->width - 92, &zui_body, u->theme.text);
    zui_clip(u, (struct zui_rect){0, 0, u->width, u->height});
    icon_button(u, LIMIT_CLOSE, (struct zui_rect){u->width - 40, bar.y + 6, 28, 28}, "x", 1);
}

/* The core's status says what a hovered link opens, what is loading, and afterwards "Done (0.2s)". Only the
 * first two are shown: a finished page needs no message. */
static bool pill_status(const struct gui_window *g)
{
    return g->loading || strstr(g->status, "://") || !strncmp(g->status, "mailto:", 7);
}
static void draw_pill(struct gui_window *g, long now)
{
    struct zui *u = &g->ui;
    const char *text = now < g->note_until ? g->note : g->status;
    size_t bytes = strlen(text);
    if (!bytes || (text == g->status && !pill_status(g)))
        return;
    int bottom = u->height - (g->find_open ? FIND_BAR : 0) - 10;
    int room = u->width * 2 / 3 - 20, w = zui_measure(text, bytes > 512 ? 512 : bytes, &zui_caption) + 20;
    if (w > room)
        w = room;
    if (w < 40)
        return;
    struct zui_rect r = {10, bottom - 26, w, 26};
    card(u, r);
    zui_clip(u, (struct zui_rect){r.x + 10, r.y, r.w - 20, r.h});
    fit(u, r.x + 10, r.y + (r.h - zui_caption.height) / 2, text, bytes, r.w - 20, &zui_caption, u->theme.secondary);
    zui_clip(u, (struct zui_rect){0, 0, u->width, u->height});
}

static void draw_find_bar(struct gui_window *g)
{
    struct zui *u = &g->ui;
    struct zui_rect bar = {0, u->height - FIND_BAR, u->width, FIND_BAR};
    zui_fill(u, bar, u->theme.subtle);
    zui_fill(u, (struct zui_rect){0, bar.y, u->width, 1}, u->theme.border);
    int field_w = u->width - 200 > 280 ? 280 : u->width - 200;
    if (field_w < 80)
        field_w = 80;
    zui_input_draw(u, (struct zui_rect){12, bar.y + 8, field_w, 36}, &g->find, g->find_focus);
    const char *state = !g->find.length ? "" : g->find_missing ? "No matches" : "";
    zui_text(u, 24 + field_w, bar.y + (bar.h - zui_caption.height) / 2, state, strlen(state), &zui_caption,
             u->theme.secondary);
    int x = u->width - 3 * 34 - 8;
    icon_button(u, FIND_PREV, (struct zui_rect){x, bar.y + 10, 32, 32}, "chevron-up", g->find.length != 0);
    icon_button(u, FIND_NEXT, (struct zui_rect){x + 34, bar.y + 10, 32, 32}, "chevron-down", g->find.length != 0);
    icon_button(u, FIND_CLOSE, (struct zui_rect){x + 68, bar.y + 10, 32, 32}, "x", 1);
}

static unsigned int mix(unsigned int a, unsigned int b, unsigned t)
{
    unsigned int out = 0;
    for (int shift = 0; shift < 24; shift += 8)
        out |= ((((a >> shift) & 255) * (255 - t) + ((b >> shift) & 255) * t + 127) / 255) << shift;
    return out;
}
/* A menu row: icon, label and shortcut, tinted under the pointer. */
static void menu_row(struct zui *u, int id, struct zui_rect r, const char *icon, const char *label, const char *key)
{
    zui_add(u, id, ZUI_BUTTON, r, "", 0, 1);
    if (is_hovered(u, id) || is_pressed(u, id))
        zui_round(u, r, 6, mix(u->theme.raised, u->theme.accent, is_pressed(u, id) ? 96 : 56));
    zui_icon(u, icon, r.x + 10, r.y + (r.h - 16) / 2, 16, ZUI_ICON_TEXT);
    zui_text(u, r.x + 36, r.y + (r.h - zui_body.height) / 2, label, strlen(label), &zui_body, u->theme.text);
    if (key)
        zui_text(u, r.x + r.w - 10 - zui_measure(key, strlen(key), &zui_caption), r.y + (r.h - zui_caption.height) / 2,
                 key, strlen(key), &zui_caption, u->theme.secondary);
}

static void draw_menu(struct gui_window *g)
{
    struct zui *u = &g->ui;
    int w = 280, x = u->width - w - 8;
    if (x < 8)
        x = 8;
    struct zui_rect r = {x, TOOLBAR - 4, w, 6 + 36 + 9 + 3 * 34 + 9 + 34 + 6};
    card(u, r);
    g->popover_rect = r;
    int y = r.y + 6, first = u->count;
    /* Zoom: the steps either side of the current scale, and its value to go back to 100 %. */
    zui_text(u, x + 16, y + (36 - zui_body.height) / 2, "Zoom", 4, &zui_body, u->theme.text);
    char percent[16];
    snprintf(percent, sizeof(percent), "%d %%", (int)(g->scale * 100 + .5f));
    zui_add(u, ZOOM_OUT, ZUI_BUTTON, (struct zui_rect){x + w - 150, y + 2, 40, 32}, "", 0, g->scale > zoom_steps[0] + .01f);
    zui_add(u, ZOOM_RESET, ZUI_BUTTON, (struct zui_rect){x + w - 106, y + 2, 56, 32}, percent, 0, 1);
    zui_add(u, ZOOM_IN, ZUI_BUTTON, (struct zui_rect){x + w - 46, y + 2, 40, 32}, "", 0,
            g->scale < zoom_steps[sizeof(zoom_steps) / sizeof(zoom_steps[0]) - 1] - .01f);
    zui_update_hover(u);
    zui_draw_range(u, first, u->count - first);
    /* The font has no minus sign: the step buttons carry icons. */
    bool smaller = g->scale > zoom_steps[0] + .01f,
         larger = g->scale < zoom_steps[sizeof(zoom_steps) / sizeof(zoom_steps[0]) - 1] - .01f;
    zui_icon(u, "minus", x + w - 150 + 12, y + 10, 16, smaller ? ZUI_ICON_TEXT : ZUI_ICON_DISABLED);
    zui_icon(u, "plus", x + w - 46 + 12, y + 10, 16, larger ? ZUI_ICON_TEXT : ZUI_ICON_DISABLED);
    y += 36 + 4;
    divider(u, x + 10, y, w - 20);
    y += 5;
    menu_row(u, MENU_FIND, (struct zui_rect){x + 6, y, w - 12, 34}, "search", "Find on page", "Ctrl+F");
    menu_row(u, MENU_COPY, (struct zui_rect){x + 6, y + 34, w - 12, 34}, "copy", "Copy page address", NULL);
    menu_row(u, MENU_INFO, (struct zui_rect){x + 6, y + 68, w - 12, 34}, page_icon(page_kind(g->current)), "Page info", NULL);
    y += 3 * 34 + 4;
    divider(u, x + 10, y, w - 20);
    y += 5;
    menu_row(u, MENU_GUIDE, (struct zui_rect){x + 6, y, w - 12, 34}, "book-open", "Using Nebula", NULL);
}

static void info_row(struct zui *u, int x, int y, int w, const char *icon, const char *key, const char *value, bool strong)
{
    zui_icon(u, icon, x, y + 2, 16, ZUI_ICON_TEXT);
    zui_text(u, x + 28, y, key, strlen(key), &zui_body, u->theme.text);
    zui_text(u, x + w - zui_measure(value, strlen(value), strong ? &zui_label : &zui_body), y, value, strlen(value),
             strong ? &zui_label : &zui_body, strong ? u->theme.text : u->theme.secondary);
}

static void draw_info(struct gui_window *g)
{
    struct zui *u = &g->ui;
    int kind = page_kind(g->current), refused = (int)zenith_web_refused();
    int w = 330, x = address_rect(g).x;
    if (x + w > u->width - 8)
        x = u->width - 8 - w;
    if (x < 8)
        x = 8;
    int rows = 3 + (refused > 0);
    struct zui_rect r = {x, TOOLBAR - 4, w, 16 + 64 + 13 + rows * 30 + 10 + 36 + 16};
    card(u, r);
    g->popover_rect = r;
    static const char *const titles[] = {"Built-in page", "Local file", "Not secure", "Secure connection"};
    static const char *const lines[] = {"Part of Nebula", "Read from this computer",
                                        "Others on the network can read or change it", "Certificate checked by Zenith"};
    int ix = x + 18, y = r.y + 16;
    zui_icon(u, page_icon(kind), ix, y + 2, 16, kind == PAGE_INSECURE ? ZUI_ICON_DISABLED : ZUI_ICON_TEXT);
    zui_text(u, ix + 28, y, titles[kind], strlen(titles[kind]), &zui_label, u->theme.text);
    const char *host, *rest;
    size_t host_len;
    address_parts(g->current, &host, &host_len, &rest);
    zui_clip(u, (struct zui_rect){ix + 28, y, w - 64, 64});
    if (host_len)
        fit(u, ix + 28, y + 22, host, host_len, w - 64, &zui_caption, u->theme.secondary);
    else
        fit(u, ix + 28, y + 22, rest, strlen(rest), w - 64, &zui_caption, u->theme.secondary);
    zui_text(u, ix + 28, y + 40, lines[kind], strlen(lines[kind]), &zui_caption, u->theme.secondary);
    zui_clip(u, (struct zui_rect){0, 0, u->width, u->height});
    y += 64;
    divider(u, x + 12, y, w - 24);
    y += 13;
    char used[32], zoom[16], missing[32];
    snprintf(used, sizeof(used), "%u of %d used", zenith_web_attempts(), CONNECTION_BUDGET);
    snprintf(zoom, sizeof(zoom), "%d %%", (int)(g->scale * 100 + .5f));
    snprintf(missing, sizeof(missing), "%d request%s", refused, refused == 1 ? "" : "s");
    info_row(u, ix, y, w - 36, "code", "Scripts", "Not supported", false);
    info_row(u, ix, y + 30, w - 36, "globe", "Connections", used, zenith_web_attempts() >= CONNECTION_BUDGET);
    y += 60;
    if (refused > 0) {
        info_row(u, ix, y, w - 36, "triangle-alert", "Not loaded", missing, true);
        y += 30;
    }
    info_row(u, ix, y, w - 36, "search", "Zoom", zoom, false);
    y += 30 + 10;
    int first = u->count;
    zui_add(u, INFO_COPY, ZUI_BUTTON, (struct zui_rect){ix, y, 150, 36}, "Copy address", 0, 1);
    zui_update_hover(u);
    zui_draw_range(u, first, u->count - first);
}

static void draw_card(struct gui_window *g)
{
    struct zui *u = &g->ui;
    int w = u->width - 32 < 400 ? u->width - 32 : 400;
    if (w < 200)
        return;
    struct zui_rect r = {(u->width - w) / 2, page_top(g) + 16, w, 176};
    card(u, r);
    g->popover_rect = r;
    int x = r.x + 18, y = r.y + 18;
    zui_icon(u, "download", x, y + 2, 16, ZUI_ICON_TEXT);
    zui_clip(u, (struct zui_rect){x + 28, y, w - 64, 120});
    fit(u, x + 28, y, g->card.name, strlen(g->card.name), w - 64, &zui_label, u->theme.text);
    fit(u, x + 28, y + 22, g->card.detail, strlen(g->card.detail), w - 64, &zui_caption, u->theme.secondary);
    static const char a[] = "Nebula can't show this file, and", b[] = "Zenith can't save downloads yet.";
    zui_text(u, x + 28, y + 50, a, sizeof(a) - 1, &zui_body, u->theme.text);
    zui_text(u, x + 28, y + 72, b, sizeof(b) - 1, &zui_body, u->theme.text);
    zui_clip(u, (struct zui_rect){0, 0, u->width, u->height});
    int first = u->count;
    zui_add(u, CARD_COPY, ZUI_BUTTON, (struct zui_rect){x + 28, r.y + r.h - 52, 140, 36}, "Copy address", 0,
            g->card.url[0] != 0);
    zui_add(u, CARD_CLOSE, ZUI_BUTTON, (struct zui_rect){x + 176, r.y + r.h - 52, 90, 36}, "Close", 0, 1);
    zui_update_hover(u);
    zui_draw_range(u, first, u->count - first);
}

static bool overlays(const struct gui_window *g, long now)
{
    return g->popover || g->card.open || g->find_open || (g->status[0] && pill_status(g)) || now < g->note_until;
}

static bool draw(struct gui_window *g)
{
    struct zui *u = &g->ui;
    long now = zui_motion_clock();
    int top = page_top(g), h = viewport_height(g);
    bool layered = overlays(g, now);
    /* Overlays sit on the page: draw everything when they show, or when they just went away. */
    if (layered || g->overlays_drawn) {
        if (g->page_dirty)
            u->dirty = 1;
        if (u->dirty)
            g->page_dirty = true;
    }
    if (u->dirty) {
        u->count = 0;
        u->material_count = 0;
        zui_clip(u, (struct zui_rect){0, 0, u->width, u->height});
        draw_toolbar(g);
        if (limit_bar(g))
            draw_limit_bar(g);
        zui_update_hover(u);
    }
    draw_progress(g, now);
    g->strip_at = now;
    bool document = false;
    if (g->page_dirty) {
        struct rect clip = {0, 0, u->width, h};
        struct page_raster raster = {u->pixels + (long)top * u->width, u->width, h, u->width, clip};
        const struct redraw_context ctx = {
            .interactive = true, .background_images = true, .plot = &zenith_plotters, .priv = &raster};
        zui_fill(u, (struct zui_rect){0, top, u->width, h}, 0xffffff);
        if (browser_window_redraw_ready(g->browser))
            document = browser_window_redraw(g->browser, -g->sx, -g->sy, &clip, &ctx);
        if (g->caret_height > 0 && !g->address_focus && !g->find_focus) {
            zui_clip(u, (struct zui_rect){0, top, u->width, h});
            zui_fill(u, (struct zui_rect){g->caret_x - g->sx, top + g->caret_y - g->sy, 1, g->caret_height},
                     0x303030);
            zui_clip(u, (struct zui_rect){0, 0, u->width, u->height});
        }
        g->popover_rect = (struct zui_rect){0, 0, 0, 0};
        if (layered) {
            if (g->find_open)
                draw_find_bar(g);
            draw_pill(g, now);
            if (g->card.open)
                draw_card(g);
            if (g->popover == POPOVER_MENU)
                draw_menu(g);
            else if (g->popover == POPOVER_INFO)
                draw_info(g);
            zui_update_hover(u);
        }
        g->overlays_drawn = layered;
    }
    if (g->material)
        zui_material_repair(u);
    unsigned long bytes = (unsigned long)u->width * u->height * 4;
    long ret = g->material
                   ? zenith_gui_present_material(g->handle, u->pixels, bytes, u->materials, u->material_count)
                   : zenith_gui_present(g->handle, u->pixels, bytes);
    if (ret < 0)
        return false;
    if (document && g->present_document) {
        static const char msg[] = "NETSURF_DOCUMENT_PRESENTED\n";
        zenith_write(msg, sizeof(msg) - 1);
        g->present_document = false;
    }
    u->dirty = 0;
    g->page_dirty = false;
    return true;
}

/* ---- Actions ---------------------------------------------------------------------------------------------- */

static nserror parse_address(const char *text, nsurl **url)
{
    if (!text || !*text)
        return NSERROR_BAD_URL;
    if (*text == '/')
        return netsurf_path_to_nsurl(text, url);
    if (ascii_is_alpha(*text)) {
        const char *end = text + 1;
        while (ascii_is_alphanumerical(*end) || *end == '-' || *end == '.' || *end == '+')
            end++;
        if (*end == ':') {
            /* A host followed by a decimal port is also valid address-bar input. */
            const char *port = end + 1;
            size_t digits = strspn(port, "0123456789");
            if (!digits || (port[digits] && !strchr("/?#", port[digits])))
                return nsurl_create(text, url);
        }
    }
    char address[2100];
    int count = snprintf(address, sizeof(address), "https://%s", text);
    return count <= 0 || (size_t)count >= sizeof(address) ? NSERROR_BAD_URL : nsurl_create(address, url);
}
static void open_address(struct gui_window *g, const char *text)
{
    nsurl *url = NULL;
    nserror err = parse_address(text, &url);
    if (err == NSERROR_OK) {
        g->address_focus = false;
        g->select_address = false;
        err = browser_window_navigate(g->browser, url, NULL, BW_NAVIGATE_HISTORY, NULL, NULL, NULL);
        nsurl_unref(url);
    }
    if (err != NSERROR_OK) {
        navigation_error(g, err);
        g->address_focus = true;
    }
    repaint(g);
}
static void set_zoom(struct gui_window *g, int direction)
{
    int count = (int)(sizeof(zoom_steps) / sizeof(zoom_steps[0])), at = 4;
    for (int i = 0; i < count; i++)
        if (zoom_steps[i] <= g->scale + .01f)
            at = i;
    at = direction == 0 ? 4 : at + direction;
    if (at < 0 || at >= count || zoom_steps[at] == g->scale)
        return;
    g->scale = zoom_steps[at];
    browser_window_set_scale(g->browser, g->scale, true);
    repaint(g);
}
static void open_find(struct gui_window *g)
{
    if (!g->find.text)
        zui_input_init(&g->find, g->find_text, sizeof(g->find_text));
    g->popover = POPOVER_NONE;
    g->find_open = g->find_focus = true;
    g->address_focus = g->select_address = false;
    reset_address(g, g->current);
    repaint(g);
}
static void ui_action(struct gui_window *g, int id)
{
    if (id != MENU && id != INFO && id != ZOOM_IN && id != ZOOM_OUT && id != ZOOM_RESET)
        g->popover = POPOVER_NONE;
    switch (id) {
    case BACK:
        if (browser_window_history_back_available(g->browser))
            browser_window_history_back(g->browser, false);
        break;
    case FORWARD:
        if (browser_window_history_forward_available(g->browser))
            browser_window_history_forward(g->browser, false);
        break;
    case RELOAD:
        if (g->loading)
            browser_window_stop(g->browser);
        else
            browser_window_reload(g->browser, false);
        break;
    case MENU:
        g->popover = g->popover == POPOVER_MENU ? POPOVER_NONE : POPOVER_MENU;
        break;
    case INFO:
    case MENU_INFO:
        g->popover = g->popover == POPOVER_INFO && id == INFO ? POPOVER_NONE : POPOVER_INFO;
        break;
    case ADDRESS:
        g->address_focus = true;
        g->select_address = true;
        reset_address(g, g->current);
        break;
    case ZOOM_OUT:
    case ZOOM_IN:
    case ZOOM_RESET:
        set_zoom(g, id == ZOOM_OUT ? -1 : id == ZOOM_IN ? 1 : 0);
        break;
    case MENU_FIND:
        open_find(g);
        break;
    case MENU_COPY:
    case INFO_COPY:
        copy_to_clipboard(g->current, strlen(g->current));
        note(g, "Address copied");
        break;
    case MENU_GUIDE:
        open_address(g, GUIDE_URL);
        break;
    case CARD_COPY:
        copy_to_clipboard(g->card.url, strlen(g->card.url));
        g->card.open = false;
        note(g, "Address copied");
        break;
    case CARD_CLOSE:
        g->card.open = false;
        break;
    case FIND_PREV:
    case FIND_NEXT:
        find(g, id == FIND_PREV);
        break;
    case FIND_CLOSE:
        close_find(g);
        break;
    case LIMIT_CLOSE:
        g->limit_seen = zenith_web_refused();
        scroll_to(g, g->sx, g->sy);
        break;
    }
    repaint(g);
}

static void key(struct gui_window *g, const struct zenith_gui_event *e)
{
    bool ctrl = (e->modifiers & 2) != 0, alt = (e->modifiers & 4) != 0, shift = (e->modifiers & 1) != 0;
    int letter = e->code;
    if (letter >= 'A' && letter <= 'Z')
        letter += 'a' - 'A';
    if (ctrl && letter == 'q') {
        g->done = true;
        return;
    }
    if (e->extra == 1 && (g->popover || g->card.open)) {
        g->popover = POPOVER_NONE;
        g->card.open = false;
        repaint(g);
        return;
    }
    if (ctrl && (letter == 'l' || (letter == 'a' && g->address_focus))) {
        g->popover = POPOVER_NONE;
        g->find_focus = false;
        g->address_focus = true;
        g->select_address = true;
        repaint(g);
        return;
    }
    if (ctrl && letter == 'f') {
        open_find(g);
        return;
    }
    if (ctrl && !e->extra && (letter == '+' || letter == '=' || letter == '-' || letter == '0')) {
        set_zoom(g, letter == '-' ? -1 : letter == '0' ? 0 : 1);
        return;
    }
    if (alt && (e->extra == 5 || e->extra == 6)) {
        ui_action(g, e->extra == 5 ? BACK : FORWARD);
        return;
    }
    if ((ctrl && letter == 'r') || e->extra == 18) {
        ui_action(g, RELOAD);
        return;
    }
    if (g->find_open && g->find_focus) {
        if (e->extra == 1) {
            close_find(g);
            return;
        }
        if (e->extra == 2) {
            find(g, shift);
            repaint(g);
            return;
        }
        if (zui_input_event(&g->find, e)) {
            g->find_text[g->find.length] = 0;
            find(g, false);
            repaint(g);
        }
        return;
    }
    if (g->address_focus) {
        if (e->extra == 1) {
            g->address_focus = false;
            g->select_address = false;
            reset_address(g, g->current);
            repaint(g);
            return;
        }
        if (e->extra == 2) {
            open_address(g, g->location);
            return;
        }
        if (g->select_address && !ctrl && !alt) {
            if ((!e->extra && e->code >= 32) || e->extra == 3 || e->extra == 9)
                reset_address(g, "");
            g->select_address = false;
            g->ui.dirty = 1;
        }
        if (zui_input_event(&g->address, e))
            g->ui.dirty = 1;
        return;
    }
    if (e->extra == 1 && g->find_open) {
        close_find(g);
        return;
    }
    if (e->extra == 1 && g->loading) {
        browser_window_stop(g->browser);
        return;
    }
    static const uint32_t special[] = {0,
                                       NS_KEY_ESCAPE,
                                       NS_KEY_CR,
                                       NS_KEY_DELETE_LEFT,
                                       NS_KEY_TAB,
                                       NS_KEY_LEFT,
                                       NS_KEY_RIGHT,
                                       NS_KEY_UP,
                                       NS_KEY_DOWN,
                                       NS_KEY_DELETE_RIGHT,
                                       NS_KEY_LINE_START,
                                       NS_KEY_LINE_END,
                                       NS_KEY_PAGE_UP,
                                       NS_KEY_PAGE_DOWN};
    uint32_t k = e->extra == 0                                               ? (uint32_t)e->code
                 : (unsigned)e->extra < sizeof(special) / sizeof(special[0]) ? special[e->extra]
                                                                             : 0;
    if (ctrl && letter == 'a')
        k = NS_KEY_SELECT_ALL;
    else if (ctrl && letter == 'c')
        k = NS_KEY_COPY_SELECTION;
    else if (ctrl || alt)
        return;
    if (k && browser_window_key_press(g->browser, k))
        return;
    long step = viewport_height(g) - 32;
    switch (e->extra) {
    case 7:
        scroll_to(g, g->sx, g->sy - 40L);
        break;
    case 8:
        scroll_to(g, g->sx, g->sy + 40L);
        break;
    case 10:
        scroll_to(g, g->sx, 0);
        break;
    case 11:
        scroll_to(g, g->sx, g->ey);
        break;
    case 12:
        scroll_to(g, g->sx, g->sy - step);
        break;
    case 13:
        scroll_to(g, g->sx, g->sy + step);
        break;
    }
}

static bool inside(struct zui_rect r, int x, int y)
{
    return x >= r.x && x < r.x + r.w && y >= r.y && y < r.y + r.h;
}
/* Chrome: the toolbar, the limit bar, the find bar, and an open popover or card. */
static bool on_popover(const struct gui_window *g, int x, int y)
{
    return (g->popover || g->card.open) && inside(g->popover_rect, x, y);
}
static bool on_chrome(const struct gui_window *g, int x, int y)
{
    return y < page_top(g) || (g->find_open && y >= g->ui.height - FIND_BAR) || on_popover(g, x, y);
}

/* The pointer left the page (onto the chrome or out of the window): a link's address no longer applies. */
static void leave_page(struct gui_window *g)
{
    if (!g->loading && g->status[0])
        status(g, "");
}
static void pointer(struct gui_window *g, const struct zenith_gui_event *e)
{
    int x = e->x, y = e->y;
    /* Taking pointer capture clears the host hover target. Its (-1,-1)
     * notification is also delivered between a normal press and release. */
    if (e->type == Z_GUI_MOVE && (x < 0 || y < 0)) {
        int before = g->ui.hover;
        zui_event(&g->ui, e);
        if (g->ui.hover != before)
            g->ui.dirty = 1;
        if (!g->page_pressed)
            leave_page(g);
        return;
    }
    int top = page_top(g);
    /* A press outside the open popover closes it. On the page it goes no further; on the toolbar it also
     * reaches its button (the menu button and the lock toggle their own popover). */
    if (e->type == Z_GUI_DOWN && g->popover && !on_popover(g, x, y)) {
        g->popover = POPOVER_NONE;
        repaint(g);
        if (y >= TOOLBAR)
            return;
    }
    if (!g->page_pressed && (on_chrome(g, x, y) || g->ui.pressed >= 0)) {
        int before = g->ui.hover;
        if (e->type == Z_GUI_MOVE)
            leave_page(g);
        int id = zui_event(&g->ui, e);
        if (id)
            ui_action(g, id);
        else if (g->ui.hover != before || e->type == Z_GUI_DOWN || e->type == Z_GUI_UP)
            g->ui.dirty = 1;
        if (e->type == Z_GUI_DOWN && g->find_open && y >= g->ui.height - FIND_BAR && x < g->ui.width - 3 * 34 - 8) {
            g->find_focus = true;
            g->address_focus = false;
            repaint(g);
        }
        return;
    }
    if (g->ui.hover >= 0 && e->type == Z_GUI_MOVE) {
        zui_event(&g->ui, e);
        g->ui.dirty = 1;
    }
    int px = (int)bounded((long)x + g->sx, INT_MAX), py = (int)bounded((long)y - top + g->sy, INT_MAX);
    if (e->type == Z_GUI_SCROLL) {
        int delta = e->code > 8 ? -320 : e->code < -8 ? 320 : -40 * e->code;
        int dx = (e->modifiers & 1) ? delta : 0, dy = dx ? 0 : delta;
        if (!browser_window_scroll_at_point(g->browser, px, py, dx, dy))
            scroll_to(g, (long)g->sx + dx, (long)g->sy + dy);
    } else if (e->type == Z_GUI_DOWN && e->code == 1) {
        if (g->address_focus)
            reset_address(g, g->current);
        if (g->address_focus || g->find_focus)
            repaint(g);
        g->address_focus = false;
        g->select_address = false;
        g->find_focus = false;
        g->page_pressed = true;
        g->dragging = false;
        g->press_x = px;
        g->press_y = py;
        browser_window_mouse_click(g->browser, BROWSER_MOUSE_PRESS_1, px, py);
    } else if (e->type == Z_GUI_UP && g->page_pressed) {
        if (y >= top && !g->dragging)
            browser_window_mouse_click(g->browser, BROWSER_MOUSE_CLICK_1, px, py);
        browser_window_mouse_track(g->browser, BROWSER_MOUSE_HOVER, px, py);
        g->page_pressed = false;
        g->dragging = false;
    } else if (e->type == Z_GUI_MOVE || e->type == Z_GUI_DRAG) {
        if (g->page_pressed && !g->dragging &&
            (labs((long)px - g->press_x) > 3 || labs((long)py - g->press_y) > 3)) {
            browser_window_mouse_click(g->browser, BROWSER_MOUSE_DRAG_1, g->press_x, g->press_y);
            g->dragging = true;
        }
        browser_window_mouse_track(
            g->browser, g->dragging ? (BROWSER_MOUSE_DRAG_ON | BROWSER_MOUSE_HOLDING_1) : BROWSER_MOUSE_HOVER,
            px, py);
    }
}

int zenith_browser_run(const char *address)
{
    struct gui_window *g = &window;
    g->scale = 1.f;
    zui_init(&g->ui, starting_pixels, START_W, START_H);
    zui_icons_use_packages();
    zui_apply_appearance(&g->ui);
    zui_apply_style(&g->ui);
    struct zenith_material_caps caps = {0};
    g->material = zenith_gui_material_caps(&caps) == 0 && caps.version == 2;
    g->handle = g->material ? zenith_gui_open_material(START_W, START_H, "Nebula", 6)
                            : zenith_gui_open(START_W, START_H, "Nebula", 6);
    if (g->handle < 0)
        return 1;
    g->ui.foreground = g->material;
    if (zenith_gui_interactive(g->handle) < 0 || zenith_gui_resize_enable(g->handle) < 0) {
        zenith_gui_close(g->handle);
        return 1;
    }
    g->ui.hover_supported = zenith_gui_input(g->handle, 15) == 0 || zenith_gui_input(g->handle, 7) == 0 ||
                            zenith_gui_pointer_motion(g->handle) == 0;
    reset_address(g, address);
    nsurl *url = NULL;
    nserror err = parse_address(address, &url);
    if (err == NSERROR_OK) {
        err = browser_window_create(BW_CREATE_HISTORY, url, NULL, NULL, NULL);
        nsurl_unref(url);
    }
    if (err != NSERROR_OK || !g->browser) {
        zenith_gui_close(g->handle);
        return 1;
    }
    static const char ready[] = "NETSURF_READY\n";
    zenith_write(ready, sizeof(ready) - 1);
    int result = 0;
    unsigned refused = 0;
    while (!g->done) {
        run_alarms();
        if (g->done)
            break;
        long now = zui_motion_clock();
        if (g->note_until && now >= g->note_until) {
            g->note_until = 0;
            repaint(g);
        }
        if (zenith_web_refused() != refused) {
            /* The limit bar appears (or grows): the page moves down under it. */
            refused = zenith_web_refused();
            scroll_to(g, g->sx, g->sy);
            browser_window_schedule_reformat(g->browser);
            repaint(g);
        }
        bool strip = g->loading && now - g->strip_at >= 40;
        if ((g->ui.dirty || g->page_dirty || strip) && !draw(g)) {
            result = zui_window_closed(g->handle) ? 0 : 1;
            break;
        }
        struct zenith_gui_event e = {0};
        long rc = zenith_gui_poll(g->handle, &e);
        if (rc < 0) {
            result = zui_window_closed(g->handle) ? 0 : 1;
            break;
        }
        if (!rc)
            continue;
        if (e.type == Z_GUI_CLOSE || e.type == Z_GUI_CLOSE_INTENT)
            break;
        if (e.type == Z_GUI_RESIZE) {
            if (zui_apply_viewport(&g->ui, g->handle, (unsigned long)START_W * START_H) < 0) {
                result = zui_window_closed(g->handle) ? 0 : 1;
                break;
            }
            browser_window_schedule_reformat(g->browser);
            repaint(g);
        } else if (e.type == Z_GUI_RESET || e.type == Z_GUI_FOCUS) {
            zui_event(&g->ui, &e);
            zui_apply_appearance(&g->ui);
            zui_apply_style(&g->ui);
            g->page_pressed = false;
            g->dragging = false;
            repaint(g);
        } else if (e.type == Z_GUI_KEY)
            key(g, &e);
        else
            pointer(g, &e);
    }
    if (g->browser)
        browser_window_destroy(g->browser);
    zenith_gui_close(g->handle);
    return result;
}

int main(int argc, char **argv)
{
    if (argc == 2 && !strcmp(argv[1], "--version")) {
        puts("netsurf " ZENITH_BROWSER_VERSION " (Nebula, NetSurf 3.11)");
        return 0;
    }
    if (argc > 2 || (argc == 2 && !strcmp(argv[1], "--help"))) {
        puts("netsurf [URL | /absolute/path.html]");
        return argc > 2 ? 2 : 0;
    }
    if (netsurf_register(&operations) != NSERROR_OK ||
        nsoption_init(NULL, &nsoptions, &nsoptions_default) != NSERROR_OK)
        return 1;
    nsoption_set_bool(enable_javascript, false);
    nsoption_set_bool(target_blank, false);
    nsoption_set_uint(disc_cache_size, 0);
    int result = 1;
    if (messages_add_from_file(RESOURCES "Messages") != NSERROR_OK) {
        puts("netsurf: unable to load Messages");
        goto done;
    }
    nserror init = netsurf_init(NULL);
    if (init != NSERROR_OK) {
        puts("netsurf: browser initialization failed");
        goto done;
    }
    bitmap_fmt_t format = {.layout = BITMAP_LAYOUT_ARGB8888, .pma = false};
    bitmap_set_format(&format);
    if (zenith_fetch_register() == NSERROR_OK)
        result = zenith_browser_run(argc == 2 ? argv[1] : START_URL);
    netsurf_exit();
done:
    while (alarms) {
        struct alarm *next = alarms->next;
        free(alarms);
        alarms = next;
    }
    nsoption_finalise(nsoptions, nsoptions_default);
    return result;
}
