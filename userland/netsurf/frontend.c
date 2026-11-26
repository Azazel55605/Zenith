/* Nebula, Zenith's browser: the native frontend for the NetSurf 3.11 core. The core owns navigation, page
 * history, the DOM, layout, and page input dispatch; this file owns the window around the pages
 * (docs/components/BROWSER.md, docs/design/browser-concept.svg, stages B1-B4). One window holds up to 16 tabs:
 * the sidebar lists them and the active one fills the content inset (docs/components/BROWSER-TABS.md, T0-T4). */
#include "frontend.h"
#include "../include/zenith_files.h"
#include "../include/zenith_settings.h"
#include "../ui/text.h"
#include "desktop/browser_history.h"
#include "desktop/download.h"
#include "desktop/search.h"
#include "fetch-transport.h"
#include "fetch.h"
#include "profile.h"
#include "source-view.h"
#include "start-page.h"
#include "content/content.h"
#include "content/handlers/html/html.h"
#include "netsurf/bitmap.h"
#include "netsurf/browser_window.h"
#include "netsurf/clipboard.h"
#include "netsurf/content.h"
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
#define PACKAGED_WELCOME "file://" RESOURCES "welcome.html"
#define GUIDE_URL "file://" RESOURCES "guide.html"
/* 640x480 is the largest size a window may open at; it can grow after. */
enum { START_W = 640, START_H = 480, TOOLBAR = 52, FIND_BAR = 52, BUTTON = 36, HISTORY_ROWS = 8, SUGGESTIONS = 5 };
/* Tabs: at most 16, of which 8 keep their page; the least recently used beyond that sleep. */
enum { TABS_MAX = 16, LIVE_TABS = 8, CLOSED_KEPT = 8, SIDEBAR_W = 240, RAIL_W = 56, RAIL_BELOW = 820,
       ROW_H = 46, ROW_GAP = 4, RAIL_ROW = 44 };
enum {
    BACK = 1, FORWARD, RELOAD, MENU, INFO, ADDRESS,
    ZOOM_OUT, ZOOM_RESET, ZOOM_IN, MENU_FIND, MENU_COPY, MENU_INFO, MENU_GUIDE, MENU_SOURCE,
    INFO_COPY, CARD_COPY, CARD_CLOSE, FIND_PREV, FIND_NEXT, FIND_CLOSE, LINK_OPEN, LINK_COPY,
    BOOKMARK, MENU_HOME, HISTORY_FIRST, SUGGEST_FIRST = HISTORY_FIRST + HISTORY_ROWS,
    TAB_FIRST = SUGGEST_FIRST + SUGGESTIONS, TAB_CLOSE_FIRST = TAB_FIRST + TABS_MAX,
    NEW_TAB = TAB_CLOSE_FIRST + TABS_MAX, PLACE_BOOKMARKS, PLACE_HISTORY, MENU_SIDEBAR, LINK_TAB,
    TAB_MENU_NEW, TAB_MENU_DUPLICATE, TAB_MENU_RELOAD, TAB_MENU_SLEEP, TAB_MENU_CLOSE, TAB_MENU_OTHERS, TAB_MENU_REOPEN,
    SIDEBAR_TOGGLE, MENU_SCRIPTS
};
enum { POPOVER_NONE, POPOVER_MENU, POPOVER_INFO, POPOVER_BACK, POPOVER_FORWARD, POPOVER_LINK, POPOVER_SUGGEST,
       POPOVER_TAB };

/* One tab: the core's window for one browser_window, and everything that belongs to its page. */
struct gui_window {
    struct browser_window *browser;
    nsurl *script_scope;
    struct source_view source;
    struct zui_input address, find;
    char location[2048], current[2048], status[256], find_text[128], title[96];
    int sx, sy, ex, ey;
    int caret_x, caret_y, caret_height;
    bool used, address_focus, select_address, page_dirty, loading, present_document;
    bool find_open, find_focus, find_missing;
    bool sleeping, keep;                /* sleeping: no browser_window, only its address; keep: destroyed to sleep */
    int wake_sy, laid_w, laid_h;        /* the scroll to restore on waking; the page size it was laid out for */
    long used_at;
    float scale;
    struct {                            /* a link the engine cannot show: B6 turns it into a download */
        bool open;
        char name[96], detail[96], url[2048];
        download_context *ctx;
    } card;
};
/* The window: chrome, popovers and the tab list. The page is rendered into its own buffer, so chrome and overlay
 * frames reuse it without asking the engine to draw again. */
struct nebula_window {
    struct zui ui;
    long handle;
    bool material, done, page_pressed, dragging, sidebar_hidden, overlays_drawn;
    int press_x, press_y;
    int popover;                        /* POPOVER_*: the menu or Page info, under the toolbar */
    struct zui_rect popover_rect;       /* where the open popover or card was drawn, for hit tests */
    char note[64];
    long note_until, strip_at;          /* a transient pill message; the progress line's last frame */
    nsurl *context_link;
    const struct history_entry *entries[HISTORY_ROWS];
    int entry_count;
    int menu_x, menu_y, menu_tab;
    struct nebula_profile profile;
    const struct nebula_entry *suggestions[SUGGESTIONS];
    int suggestion_count, selection;
    struct gui_window slots[TABS_MAX];
    struct gui_window *tabs[TABS_MAX];  /* in sidebar order */
    int count, active, children;        /* children: tabs opened from the active one, placed after it in turn */
    int insert_at;                      /* where create_window puts the next tab, or -1 for after its parent */
    bool foreground;                    /* whether that tab becomes active */
    struct gui_window *waking;          /* the sleeping tab that create_window gives the new browser_window */
    struct gui_window *created;         /* the tab create_window made last */
    nsurl *initial_script_scope;        /* transferred to the first tab on CLI opt-in */
    char closed[CLOSED_KEPT][2048];
    int closed_count, tab_scroll, drag_tab, drag_y;
    bool tab_dragging;
    uint32_t *page;
    int page_w, page_h;
    size_t page_capacity;
};
static struct nebula_window W = {.active = -1, .insert_at = -1, .drag_tab = -1, .menu_tab = -1};
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
                 {"woff2", "font/woff2"}, {"js", "text/javascript"}};
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

/* ---- Layout: the workspace every Zenith app uses (zui_workspace_begin) ------------------------------------ */

/* The sidebar: full rows, a rail of tiles in a narrow window, or hidden. */
static int sidebar_width(void)
{
    if (W.sidebar_hidden)
        return 0;
    int side = W.ui.width >= RAIL_BELOW ? SIDEBAR_W : RAIL_W;
    int maximum = W.ui.width > 360 ? W.ui.width - 360 : 0;
    return side < maximum ? side : maximum;
}
static struct zui_rect content_rect(void)
{
    int side = sidebar_width();
    if (side <= 0)
        return (struct zui_rect){12, 12, W.ui.width - 24, W.ui.height - 24};
    return (struct zui_rect){side + 24, 12, W.ui.width - side - 36, W.ui.height - 24};
}
/* The page: the inset below its toolbar row. */
static struct zui_rect page_rect(void)
{
    struct zui_rect c = content_rect();
    int h = c.h - TOOLBAR;
    return (struct zui_rect){c.x, c.y + TOOLBAR, c.w > 1 ? c.w : 1, h > 1 ? h : 1};
}
static int viewport_height(const struct gui_window *g)
{
    (void)g;
    return page_rect().h;
}
static struct gui_window *active_tab(void)
{
    return W.active >= 0 && W.active < W.count ? W.tabs[W.active] : NULL;
}

/* Decisions use the content's canonical final URL, including on redirects.
 * Each new tab starts with no grant; sleeping keeps the original tab's choice. */
static nsurl_component script_scope_parts(const nsurl *url)
{
    if (url) {
        switch (nsurl_get_scheme_type(url)) {
        case NSURL_SCHEME_FILE:
            return NSURL_SCHEME | NSURL_AUTHORITY | NSURL_PATH;
        case NSURL_SCHEME_HTTP:
        case NSURL_SCHEME_HTTPS:
            if (nsurl_has_component(url, NSURL_HOST) && !nsurl_has_component(url, NSURL_USERNAME) &&
                !nsurl_has_component(url, NSURL_PASSWORD))
                return NSURL_SCHEME | NSURL_HOST | NSURL_PORT;
            break;
        default:
            break;
        }
    }
    return 0;
}

static bool tab_allows_scripts(struct gui_window *g, const nsurl *url)
{
    nsurl_component parts = script_scope_parts(url);
    return g && g->script_scope && parts && nsurl_compare(g->script_scope, url, parts);
}

static bool allow_page_scripts(nsurl *url)
{
    if (!script_scope_parts(url))
        return false;
    struct gui_window *g = active_tab();
    nsurl **scope = g ? &g->script_scope : &W.initial_script_scope;
    nsurl *previous = *scope;
    *scope = nsurl_ref(url);
    if (previous)
        nsurl_unref(previous);
    return true;
}

static nsurl *script_page(struct gui_window *g)
{
    struct hlcache_handle *h = !g->loading && g->browser ? browser_window_get_content(g->browser) : NULL;
    if (!h || content_get_type(h) != CONTENT_HTML || content_get_status(h) != CONTENT_STATUS_DONE)
        return NULL;
    nsurl *url = hlcache_handle_get_url(h);
    return script_scope_parts(url) ? url : NULL;
}
static int tab_index(const struct gui_window *g)
{
    for (int i = 0; i < W.count; i++)
        if (W.tabs[i] == g)
            return i;
    return -1;
}
static bool is_active(const struct gui_window *g)
{
    return g && g == active_tab();
}
static int bounded(long x, int max)
{
    return x < 0 || max < 0 ? 0 : x > max ? max : (int)x;
}
static void scroll_to(struct gui_window *g, long x, long y)
{
    int sx = bounded(x, g->ex - page_rect().w), sy = bounded(y, g->ey - viewport_height(g));
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
        if (g->wake_sy > 0 && g->ey >= g->wake_sy) {
            /* A tab woken from sleep returns to where it was read. */
            scroll_to(g, g->sx, g->wake_sy);
            g->wake_sy = 0;
        }
        scroll_to(g, g->sx, g->sy);
    }
}
/* Chrome and page both: overlays sit on the page, so changing one repaints what is under it. */
static void repaint(struct gui_window *g)
{
    W.ui.dirty = 1;
    if (g)
        g->page_dirty = true;
}

static bool contains_folded(const char *text, const char *query)
{
    size_t n = strlen(query);
    if (!n) return true;
    for (; *text; ++text)
        if (!strncasecmp(text, query, n)) return true;
    return false;
}

/* Matches stay on the device. A bookmark takes precedence over a duplicate history entry. */
static void suggest(struct gui_window *g)
{
    W.selection = -1;
    W.suggestion_count = 0;
    const char *query = g->select_address ? "" : g->location;
    int capacity = bounded((W.ui.height - TOOLBAR - 12) / 42, SUGGESTIONS);
    for (int group = 0; group < 2; ++group) {
        unsigned count = group ? W.profile.recent_count : W.profile.bookmark_count;
        const struct nebula_entry *entries = group ? W.profile.recent : W.profile.bookmarks;
        for (unsigned i = 0; i < count && W.suggestion_count < capacity; ++i) {
            const struct nebula_entry *entry = &entries[i];
            if (!contains_folded(entry->url, query) && !contains_folded(entry->title, query))
                continue;
            int index = 0;
            while (index < W.suggestion_count && strcmp(W.suggestions[index]->url, entry->url))
                ++index;
            if (index == W.suggestion_count)
                W.suggestions[W.suggestion_count++] = entry;
        }
    }
    W.popover = W.suggestion_count ? POPOVER_SUGGEST : POPOVER_NONE;
    repaint(g);
}

/* Use the committed top-level content. An internal error page can show the attempted remote URL. */
static const char *profile_url(struct gui_window *g)
{
    struct hlcache_handle *h = g->browser ? browser_window_get_content(g->browser) : NULL;
    nsurl *url = h ? hlcache_handle_get_url(h) : NULL;
    if (!url) return NULL;
    const char *value = nsurl_access(url);
    if (!strncmp(value, "file://" RESOURCES, sizeof("file://" RESOURCES) - 1))
        return NULL;
    return nebula_profile_accepts(value) ? value : NULL;
}

/* The core asks for a window for each browser_window: the first tab, a new tab, or a sleeping tab waking up. */
static struct gui_window *create_window(struct browser_window *bw, struct gui_window *existing,
                                        gui_window_create_flags flags)
{
    if (W.waking) {
        struct gui_window *g = W.waking;
        W.waking = NULL;
        g->browser = bw;
        g->sleeping = false;
        g->page_dirty = true;
        return g;
    }
    struct gui_window *g = NULL;
    for (int i = 0; i < TABS_MAX && !g; i++)
        if (!W.slots[i].used)
            g = &W.slots[i];
    if (!g || W.count >= TABS_MAX)
        return NULL;
    memset(g, 0, sizeof(*g));
    if (!W.count) {
        g->script_scope = W.initial_script_scope;
        W.initial_script_scope = NULL;
    }
    g->used = true;
    g->browser = bw;
    g->scale = 1.f;
    g->page_dirty = true;
    g->used_at = zui_motion_clock();
    g->address_focus = (flags & GW_CREATE_FOCUS_LOCATION) != 0;
    zui_input_init(&g->address, g->location, sizeof(g->location));
    /* Placement: where the caller asked, else after the tab it was opened from, else last. */
    int at = W.insert_at;
    if (at < 0 && existing && tab_index(existing) >= 0)
        at = tab_index(existing) + 1;
    if (at < 0 || at > W.count)
        at = W.count;
    memmove(&W.tabs[at + 1], &W.tabs[at], (size_t)(W.count - at) * sizeof(W.tabs[0]));
    W.tabs[at] = g;
    W.count++;
    if (W.active >= at && W.active >= 0 && W.count > 1)
        W.active++;
    if (W.active < 0 || W.foreground || (flags & GW_CREATE_FOREGROUND)) {
        W.active = at;
        W.children = 0;
    }
    W.insert_at = -1;
    W.foreground = false;
    W.created = g;
    W.ui.dirty = 1;
    return g;
}
/* Removes a tab from the list. The neighbour to its right (else left) becomes active; the last tab ends the window. */
static void forget_tab(struct gui_window *g)
{
    int at = tab_index(g);
    if (at < 0)
        return;
    if (g->script_scope) {
        nsurl_unref(g->script_scope);
        g->script_scope = NULL;
    }
    memmove(&W.tabs[at], &W.tabs[at + 1], (size_t)(W.count - at - 1) * sizeof(W.tabs[0]));
    W.count--;
    if (at == W.active)
        W.children = 0;
    if (at < W.active || (at == W.active && W.active >= W.count))
        W.active--;
    g->used = false;
    if (W.menu_tab >= W.count)
        W.menu_tab = -1;
    if (!W.count)
        W.done = true;
    else if (W.active < 0)
        W.active = 0;
    if (active_tab())
        active_tab()->page_dirty = true;
    W.ui.dirty = 1;
}
static void destroy_window(struct gui_window *g)
{
    source_close(&g->source);
    g->browser = NULL;
    if (g->keep) {
        /* Put to sleep: the tab stays in the list with its address. */
        g->keep = false;
        g->sleeping = true;
        W.ui.dirty = 1;
        return;
    }
    forget_tab(g);
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
    struct zui_rect page = page_rect();
    long x = g->sx, y = g->sy;
    if (r->x0 == r->x1 || r->x0 < x)
        x = r->x0;
    else if (r->x1 > x + page.w)
        x = (long)r->x1 - page.w;
    if (r->y0 == r->y1 || r->y0 < y)
        y = r->y0;
    else if (r->y1 > y + page.h)
        y = (long)r->y1 - page.h;
    scroll_to(g, x, y);
    return NSERROR_OK;
}
static nserror dimensions(struct gui_window *g, int *w, int *h)
{
    (void)g;
    struct zui_rect page = page_rect();
    *w = page.w;
    *h = page.h;
    return NSERROR_OK;
}
static nserror event(struct gui_window *g, enum gui_window_event ev)
{
    switch (ev) {
    case GW_EVENT_UPDATE_EXTENT:
        extent(g);
        break;
    case GW_EVENT_START_THROBBER:
        source_close(&g->source);
        g->loading = true;
        W.strip_at = 0;
        break;
    case GW_EVENT_STOP_THROBBER:
        g->loading = false;
        break;
    case GW_EVENT_NEW_CONTENT:
        source_close(&g->source);
        g->page_dirty = true;
        g->present_document = true;
        g->caret_height = 0;
        if (g->find_open && g->find.length)
            g->find_missing = false;
        {
            const char *url = profile_url(g);
            if (url) {
                nebula_profile_visit(&W.profile, url, browser_window_get_title(g->browser));
                if (g->address_focus && is_active(g))
                    suggest(g);
            }
        }
        break;
    case GW_EVENT_REMOVE_CARET:
        g->caret_height = 0;
        g->page_dirty = true;
        break;
    default:
        break;
    }
    W.ui.dirty = 1;
    return NSERROR_OK;
}
static void show_title(const struct gui_window *g)
{
    const char *text = g && *g->title ? g->title : "Nebula";
    if (W.handle > 0)
        zenith_gui_set_title(W.handle, text, strlen(text));
}
/* Each tab keeps its title for its sidebar row; the window takes the active tab's. */
static void title(struct gui_window *g, const char *text)
{
    char safe[80];
    copy_text(safe, sizeof(safe), text);
    for (char *p = safe; *p; p++)
        if ((unsigned char)*p < 32)
            *p = ' ';
    if (!*safe)
        return;
    copy_text(g->title, sizeof(g->title), safe);
    W.ui.dirty = 1;
    if (is_active(g))
        show_title(g);
}
static void reset_address(struct gui_window *g, const char *text)
{
    copy_text(g->location, sizeof(g->location), text);
    zui_input_init(&g->address, g->location, sizeof(g->location));
    W.ui.dirty = 1;
}
static nserror set_url(struct gui_window *g, nsurl *url)
{
    if (is_active(g) && strcmp(g->current, nsurl_access(url)) && W.popover >= POPOVER_BACK &&
        W.popover != POPOVER_SUGGEST && W.popover != POPOVER_TAB) {
        W.popover = POPOVER_NONE;
        W.entry_count = 0;
        repaint(g);
    }
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
        if (is_active(g))
            repaint(g);
    }
}
static void note(struct gui_window *g, const char *s)
{
    copy_text(W.note, sizeof(W.note), s);
    W.note_until = zui_motion_clock() + 2000;
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
    if (active_tab())
        navigation_error(active_tab(), NSERROR_NO_FETCH_HANDLER);
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
    if (bytes && W.handle > 0)
        zenith_call(639, (unsigned long)W.handle, (unsigned long)text, bytes);
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
    struct gui_window *g = parent ? parent : active_tab();
    if (!g)
        return NULL;
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
    W.popover = POPOVER_NONE;
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
                                             .allow_scripts = tab_allows_scripts,
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

enum { PAGE_INTERNAL, PAGE_LOCAL, PAGE_INSECURE, PAGE_SECURE, PAGE_UNVERIFIED, PAGE_MIXED };
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
/* Internal error documents can retain the failed HTTPS address in the toolbar.
 * Only the loaded document can establish the identity shown beside that address. */
static int displayed_kind(const struct gui_window *g)
{
    int kind = page_kind(g->current);
    if (kind != PAGE_SECURE)
        return kind;
    struct hlcache_handle *content = g->browser ? browser_window_get_content(g->browser) : NULL;
    nsurl *loaded = content ? hlcache_handle_get_url(content) : NULL;
    if (!loaded)
        return PAGE_UNVERIFIED;
    const char *url = nsurl_access(loaded);
    size_t length = strcspn(url, "#");
    if (strcspn(g->current, "#") != length || strncmp(g->current, url, length))
        return PAGE_UNVERIFIED;
    browser_window_page_info_state state = browser_window_get_page_info_state(g->browser);
    if (state == PAGE_STATE_SECURE_ISSUES)
        return PAGE_MIXED;
    return state == PAGE_STATE_SECURE ? PAGE_SECURE : PAGE_UNVERIFIED;
}

static const char *page_icon(int kind)
{
    if (kind == PAGE_UNVERIFIED || kind == PAGE_MIXED)
        return "triangle-alert";
    return kind == PAGE_SECURE ? "lock" : kind == PAGE_INSECURE ? "lock-open" : kind == PAGE_LOCAL ? "file" : "globe";
}

/* ---- Drawing --------------------------------------------------------------------------------------------- */

static bool inside(struct zui_rect r, int x, int y)
{
    return x >= r.x && x < r.x + r.w && y >= r.y && y < r.y + r.h;
}
/* The toolbar row at the top of the content inset: the sidebar toggle, then Back, Forward and Reload. */
enum { TB_SIDEBAR, TB_BACK, TB_FORWARD, TB_RELOAD, TB_BUTTONS };
static struct zui_rect toolbar_button(int index)
{
    struct zui_rect c = content_rect();
    return (struct zui_rect){c.x + 8 + index * (BUTTON + 4), c.y + 8, BUTTON, BUTTON};
}
static struct zui_rect address_rect(const struct gui_window *g)
{
    (void)g;
    struct zui_rect c = content_rect();
    int left = 8 + TB_BUTTONS * (BUTTON + 4) + 4;
    return (struct zui_rect){c.x + left, c.y + 8, c.w - left - (BUTTON + 16), 36};
}
static struct zui_rect menu_button(const struct gui_window *g)
{
    (void)g;
    struct zui_rect c = content_rect();
    return (struct zui_rect){c.x + c.w - BUTTON - 8, c.y + 8, BUTTON, BUTTON};
}
/* Hover by position: the control list is rebuilt every frame, so an index from the last frame can name another
 * control. */
static bool is_hovered(const struct zui *u, int id)
{
    if (!u->hover_supported || u->pointer_x < 0)
        return false;
    for (int i = u->count - 1; i >= 0; i--)
        if (u->controls[i].id == id) {
            struct zui_rect r = zui_control_area(u, i);
            return u->pointer_x >= r.x && u->pointer_x < r.x + r.w && u->pointer_y >= r.y && u->pointer_y < r.y + r.h;
        }
    return false;
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
    struct zui *u = &W.ui;
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
    const char *bookmark_url = profile_url(g);
    int star_space = bookmark_url && field.w >= 112 ? 34 : 0;
    zui_add(u, ADDRESS, ZUI_BUTTON, (struct zui_rect){field.x + 36, field.y, field.w - 36 - star_space, field.h}, "", 0, 1);
    if (star_space) {
        struct zui_rect star = {field.x + field.w - star_space, field.y + 4, 28, 28};
        bool saved = nebula_profile_bookmarked(&W.profile, bookmark_url);
        zui_add(u, BOOKMARK, ZUI_BUTTON, star, "", 0, 1);
        if (saved || is_hovered(u, BOOKMARK))
            zui_round_alpha(u, star, 6, saved ? u->theme.accent : u->theme.text, saved ? 60 : 26);
        zui_icon(u, "star", star.x + 6, star.y + 6, 16, saved ? ZUI_ICON_NORMAL : ZUI_ICON_TEXT);
    }
    int kind = displayed_kind(g);
    struct zui_rect lock = {field.x + 4, field.y + 4, 28, 28};
    zui_add(u, INFO, ZUI_BUTTON, lock, "", 0, 1);
    if (is_hovered(u, INFO) || W.popover == POPOVER_INFO)
        zui_round_alpha(u, lock, 6, u->theme.text, 26);
    zui_icon(u, page_icon(kind), lock.x + 6, lock.y + 6, 16, kind == PAGE_INSECURE ? ZUI_ICON_DISABLED : ZUI_ICON_TEXT);
    int x = field.x + 38, y = field.y + (field.h - zui_body.height) / 2, room = field.x + field.w - 12 - star_space - x;
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
    if (kind == PAGE_INTERNAL && !strcmp(g->current, NEBULA_START_URL)) {
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

/* The toolbar sits in the top row of the content inset, over the inset's own surface. */
static void draw_toolbar(struct gui_window *g)
{
    struct zui *u = &W.ui;
    struct zui_rect c = content_rect();
    zui_fill(u, (struct zui_rect){c.x, c.y + TOOLBAR - 1, c.w, 1}, u->theme.border);
    bool live = g->browser != NULL;
    /* The sidebar toggle: tinted while the sidebar shows, so its state reads at a glance. */
    if (!W.sidebar_hidden && !is_hovered(u, SIDEBAR_TOGGLE))
        zui_round_alpha(u, toolbar_button(TB_SIDEBAR), 8, u->theme.accent, 40);
    icon_button(u, SIDEBAR_TOGGLE, toolbar_button(TB_SIDEBAR), "panel-left", 1);
    icon_button(u, BACK, toolbar_button(TB_BACK), "arrow-left", live && browser_window_history_back_available(g->browser));
    icon_button(u, FORWARD, toolbar_button(TB_FORWARD), "arrow-right", live && browser_window_history_forward_available(g->browser));
    icon_button(u, RELOAD, toolbar_button(TB_RELOAD), g->loading ? "x" : "rotate-cw", live);
    draw_address(g);
    icon_button(u, MENU, menu_button(g), "ellipsis", 1);
    if (W.popover == POPOVER_MENU && !is_hovered(u, MENU))
        zui_round_alpha(u, menu_button(g), 8, u->theme.text, 26);
}

/* Loading: a 3 px accent segment travels along the toolbar's lower edge (the core gives no fraction). */
static void draw_progress(struct gui_window *g, long now)
{
    struct zui *u = &W.ui;
    struct zui_rect c = content_rect();
    if (!g->loading || c.w < 40)
        return;
    int span = c.w / 3 > 40 ? c.w / 3 : 40, travel = c.w + span;
    int x = (int)((now / 3) % travel) - span;
    int left = x < 0 ? 0 : x, right = x + span > c.w ? c.w : x + span;
    zui_clip(u, (struct zui_rect){c.x, c.y + TOOLBAR - 3, c.w, 3});
    zui_round(u, (struct zui_rect){c.x + left, c.y + TOOLBAR - 3, right - left, 3}, 1, u->theme.accent);
    zui_clip(u, (struct zui_rect){0, 0, u->width, u->height});
}

/* The core's status says what a hovered link opens, what is loading, and afterwards "Done (0.2s)". Only the
 * first two are shown: a finished page needs no message. */
static bool pill_status(const struct gui_window *g)
{
    return g->loading || strstr(g->status, "://") || !strncmp(g->status, "mailto:", 7);
}
static void draw_pill(struct gui_window *g, long now)
{
    struct zui *u = &W.ui;
    const char *text = now < W.note_until ? W.note : g->status;
    size_t bytes = strlen(text);
    if (!bytes || (text == g->status && !pill_status(g)))
        return;
    struct zui_rect pr = page_rect();
    int bottom = pr.y + pr.h - (g->find_open ? FIND_BAR : 0) - 10;
    int room = pr.w * 2 / 3 - 20, w = zui_measure(text, bytes > 512 ? 512 : bytes, &zui_caption) + 20;
    if (w > room)
        w = room;
    if (w < 40)
        return;
    struct zui_rect r = {pr.x + 10, bottom - 26, w, 26};
    card(u, r);
    zui_clip(u, (struct zui_rect){r.x + 10, r.y, r.w - 20, r.h});
    fit(u, r.x + 10, r.y + (r.h - zui_caption.height) / 2, text, bytes, r.w - 20, &zui_caption, u->theme.secondary);
    zui_clip(u, (struct zui_rect){0, 0, u->width, u->height});
}

static void draw_find_bar(struct gui_window *g)
{
    struct zui *u = &W.ui;
    struct zui_rect pr = page_rect(), bar = {pr.x, pr.y + pr.h - FIND_BAR, pr.w, FIND_BAR};
    zui_fill(u, bar, u->theme.subtle);
    zui_fill(u, (struct zui_rect){bar.x, bar.y, bar.w, 1}, u->theme.border);
    int field_w = bar.w - 200 > 280 ? 280 : bar.w - 200;
    if (field_w < 80)
        field_w = 80;
    zui_input_draw(u, (struct zui_rect){bar.x + 12, bar.y + 8, field_w, 36}, &g->find, g->find_focus);
    const char *state = !g->find.length ? "" : g->find_missing ? "No matches" : "";
    zui_text(u, bar.x + 24 + field_w, bar.y + (bar.h - zui_caption.height) / 2, state, strlen(state), &zui_caption,
             u->theme.secondary);
    int x = bar.x + bar.w - 3 * 34 - 8;
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
    struct zui *u = &W.ui;
    struct zui_rect mb = menu_button(g);
    int w = 280, x = mb.x + mb.w - w;
    if (x < 8)
        x = 8;
    nsurl *page = script_page(g);
    struct zui_rect r = {x, mb.y + mb.h + 4, w, 6 + 36 + 9 + 5 * 34 + 9 + (page ? 3 : 2) * 34 + 6};
    card(u, r);
    W.popover_rect = r;
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
    menu_row(u, MENU_INFO, (struct zui_rect){x + 6, y + 68, w - 12, 34}, page_icon(displayed_kind(g)), "Page info", NULL);
    menu_row(u, MENU_HOME, (struct zui_rect){x + 6, y + 102, w - 12, 34}, "star", "Bookmarks and history", "Alt+Home");
    menu_row(u, MENU_SOURCE, (struct zui_rect){x + 6, y + 136, w - 12, 34}, "code",
             g->source.bytes ? "Return to page" : "View page source", "Ctrl+U");
    y += 5 * 34 + 4;
    divider(u, x + 10, y, w - 20);
    y += 5;
    menu_row(u, MENU_SIDEBAR, (struct zui_rect){x + 6, y, w - 12, 34}, "panel-left",
             W.sidebar_hidden ? "Show the sidebar" : "Hide the sidebar", "Ctrl+Shift+S");
    menu_row(u, MENU_GUIDE, (struct zui_rect){x + 6, y + 34, w - 12, 34}, "book-open", "Using Nebula", NULL);
    if (page)
        menu_row(u, MENU_SCRIPTS, (struct zui_rect){x + 6, y + 68, w - 12, 34}, "code",
                 tab_allows_scripts(g, page) ? "Block JavaScript and reload" : "Allow JavaScript and reload", NULL);
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
    struct zui *u = &W.ui;
    int kind = displayed_kind(g);
    int w = 330, x = address_rect(g).x;
    if (x + w > u->width - 8)
        x = u->width - 8 - w;
    if (x < 8)
        x = 8;
    int rows = 3;
    struct zui_rect r = {x, page_rect().y - 4, w, 16 + 64 + 13 + rows * 30 + 10 + 36 + 16};
    card(u, r);
    W.popover_rect = r;
    static const char *const titles[] = {"Built-in page", "Local file", "Not secure", "Secure connection",
                                         "Unverified connection", "Mixed content"};
    static const char *const lines[] = {"Part of Nebula", "Read from this computer",
                                        "Others on the network can read or change it", "Certificate checked by Zenith",
                                        "No verified page at this address", "Some page content uses HTTP"};
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
    char used[32], zoom[16];
    snprintf(used, sizeof(used), "%u", zenith_web_attempts());
    snprintf(zoom, sizeof(zoom), "%d %%", (int)(g->scale * 100 + .5f));
    nsurl *url = script_page(g);
    const char *scripts = "Off";
    if (url && html_page_restricts_scripts(browser_window_get_content(g->browser)))
        scripts = "Blocked by page policy";
    else if (tab_allows_scripts(g, url))
        scripts = nsurl_get_scheme_type(url) == NSURL_SCHEME_FILE ? "On for this file" : "On for this origin";
    info_row(u, ix, y, w - 36, "code", "Scripts", scripts, false);
    info_row(u, ix, y + 30, w - 36, "globe", "Connection attempts", used, false);
    y += 60;
    info_row(u, ix, y, w - 36, "search", "Zoom", zoom, false);
    y += 30 + 10;
    int first = u->count;
    zui_add(u, INFO_COPY, ZUI_BUTTON, (struct zui_rect){ix, y, 150, 36}, "Copy address", 0, 1);
    zui_update_hover(u);
    zui_draw_range(u, first, u->count - first);
}

static void draw_context_menu(struct gui_window *g)
{
    (void)g;
    struct zui *u = &W.ui;
    bool links = W.popover == POPOVER_LINK;
    int rows = links ? 3 : W.entry_count;
    int width = u->width - 16;
    if (width > 280)
        width = 280;
    int height = 12 + rows * 32;
    int x = bounded(W.menu_x, u->width - width - 4);
    int y = bounded(W.menu_y, u->height - height - 4);
    W.popover_rect = (struct zui_rect){x, y, width, height};
    card(u, W.popover_rect);
    for (int i = 0; i < rows; ++i) {
        struct zui_rect r = {x + 6, y + 6 + i * 32, width - 12, 32};
        if (links) {
            static const int ids[] = {LINK_OPEN, LINK_TAB, LINK_COPY};
            static const char *const icons[] = {"globe", "plus", "copy"};
            static const char *const labels[] = {"Open link", "Open in new tab", "Copy link address"};
            menu_row(u, ids[i], r, icons[i], labels[i], NULL);
        } else {
            int id = HISTORY_FIRST + i;
            const char *title = browser_window_history_entry_get_title(W.entries[i]);
            zui_add(u, id, ZUI_BUTTON, r, "", 0, 1);
            if (is_hovered(u, id) || is_pressed(u, id))
                zui_round(u, r, 6, mix(u->theme.raised, u->theme.accent, 56));
            nsurl *url = browser_window_history_entry_get_url(W.entries[i]);
            const char *label = title && *title ? title : nsurl_access(url);
            fit(u, r.x + 10, r.y + (r.h - zui_body.height) / 2, label, strlen(label), r.w - 20,
                &zui_body, u->theme.text);
            nsurl_unref(url);
        }
    }
}

static void draw_suggestions(struct gui_window *g)
{
    struct zui *u = &W.ui;
    struct zui_rect field = address_rect(g);
    struct zui_rect r = {field.x, page_rect().y - 4, field.w, W.suggestion_count * 42 + 8};
    if (field.w < 60) return;
    W.popover_rect = r;
    card(u, r);
    for (int i = 0; i < W.suggestion_count; ++i) {
        const struct nebula_entry *entry = W.suggestions[i];
        struct zui_rect row = {r.x + 4, r.y + 4 + i * 42, r.w - 8, 42};
        zui_add(u, SUGGEST_FIRST + i, ZUI_BUTTON, row, "", 0, 1);
        if (W.selection == i || is_hovered(u, SUGGEST_FIRST + i))
            zui_round(u, row, 6, mix(u->theme.raised, u->theme.accent, 56));
        const char *title = *entry->title ? entry->title : entry->url;
        fit(u, row.x + 8, row.y + 2, title, strlen(title), row.w - 16, &zui_label, u->theme.text);
        fit(u, row.x + 8, row.y + 22, entry->url, strlen(entry->url), row.w - 16, &zui_caption, u->theme.secondary);
    }
}

static void draw_card(struct gui_window *g)
{
    struct zui *u = &W.ui;
    struct zui_rect pr = page_rect();
    int w = pr.w - 32 < 400 ? pr.w - 32 : 400;
    if (w < 200)
        return;
    struct zui_rect r = {pr.x + (pr.w - w) / 2, pr.y + 16, w, 176};
    card(u, r);
    W.popover_rect = r;
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
    return W.popover || g->card.open || g->find_open || (g->status[0] && pill_status(g)) || now < W.note_until;
}

/* ---- The sidebar: tabs as navigation rows ----------------------------------------------------------------- */

/* A tab's tile: its site's initial on one of the accent swatches, chosen by the host so a site keeps its colour. */
static void tab_tile(struct zui *u, const struct gui_window *g, int x, int y, int size)
{
    const char *host, *rest;
    size_t host_len;
    address_parts(g->current, &host, &host_len, &rest);
    if (host_len > 4 && !strncasecmp(host, "www.", 4)) {
        host += 4;
        host_len -= 4;
    }
    const char *name = host_len ? host : *g->title ? g->title : "N";
    if (!strcmp(g->current, NEBULA_START_URL) || !*g->current)
        name = "+";
    unsigned hash = 5381;
    for (size_t i = 0; i < (host_len ? host_len : 1); i++)
        hash = hash * 33 + (unsigned char)name[i];
    char letter[2] = {name[0] >= 'a' && name[0] <= 'z' ? (char)(name[0] - 32) : name[0], 0};
    if ((unsigned char)letter[0] >= 0x80)
        letter[0] = '#';
    zui_round(u, (struct zui_rect){x, y, size, size}, 5, zui_accent_color(u, (int)(hash % 6)));
    zui_text(u, x + (size - zui_measure(letter, 1, &zui_label)) / 2, y + (size - zui_label.height) / 2, letter, 1,
             &zui_label, u->theme.on_accent);
}
/* Loading: eight dots around the tile's place, the brightest one travelling. */
static void tab_spinner(struct zui *u, int x, int y, int size, long now)
{
    static const signed char dx[8] = {0, 5, 7, 5, 0, -5, -7, -5}, dy[8] = {-7, -5, 0, 5, 7, 5, 0, -5};
    int head = (int)((now / 90) % 8), cx = x + size / 2, cy = y + size / 2;
    for (int i = 0; i < 8; i++) {
        int age = (head - i + 8) % 8;
        zui_round(u, (struct zui_rect){cx + dx[i] - 2, cy + dy[i] - 2, 4, 4}, 2,
                  age < 3 ? u->theme.accent : mix(u->theme.subtle, u->theme.secondary, 110));
    }
}
static void tab_host(const struct gui_window *g, char *out, size_t capacity)
{
    const char *host, *rest;
    size_t host_len;
    address_parts(g->current, &host, &host_len, &rest);
    if (!strcmp(g->current, NEBULA_START_URL))
        copy_text(out, capacity, "Start page");
    else if (host_len)
        snprintf(out, capacity, "%.*s", (int)(host_len < capacity - 1 ? host_len : capacity - 1), host);
    else if (page_kind(g->current) == PAGE_INTERNAL)
        copy_text(out, capacity, "Built-in page");
    else
        copy_text(out, capacity, rest);
}
static int rows_visible(struct zui_rect s, int row)
{
    int room = s.h - 76 - 2 * 38 - 16;
    return room > row ? room / row : 1;
}
static void keep_active_visible(struct zui_rect s, int row)
{
    int visible = rows_visible(s, row);
    if (W.active < W.tab_scroll)
        W.tab_scroll = W.active;
    else if (W.active >= W.tab_scroll + visible)
        W.tab_scroll = W.active - visible + 1;
    W.tab_scroll = bounded(W.tab_scroll, W.count - visible);
}
static void draw_rail(struct zui_rect s, long now)
{
    struct zui *u = &W.ui;
    icon_button(u, NEW_TAB, (struct zui_rect){s.x + (s.w - 40) / 2, s.y, 40, 36}, "plus", W.count < TABS_MAX);
    keep_active_visible(s, RAIL_ROW);
    int y = s.y + 52;
    for (int i = W.tab_scroll; i < W.count && y + RAIL_ROW <= s.y + s.h - 8; i++, y += RAIL_ROW) {
        struct gui_window *t = W.tabs[i];
        struct zui_rect r = {s.x + (s.w - 44) / 2, y, 44, 38};
        zui_add(u, TAB_FIRST + i, ZUI_BUTTON, r, "", 0, 1);
        if (i == W.active) {
            zui_round(u, r, 6, mix(u->theme.subtle, u->theme.accent, 60));
            zui_round(u, (struct zui_rect){r.x, r.y + 9, 3, 20}, 1, u->theme.accent);
        } else if (is_hovered(u, TAB_FIRST + i))
            zui_round_alpha(u, r, 6, u->theme.text, 26);
        if (t->loading)
            tab_spinner(u, r.x + 12, r.y + 9, 20, now);
        else {
            tab_tile(u, t, r.x + 12, r.y + 9, 20);
            if (t->sleeping)
                zui_round_alpha(u, (struct zui_rect){r.x + 12, r.y + 9, 20, 20}, 5, u->theme.subtle, 150);
        }
    }
}
static void draw_sidebar(struct zui_rect s, long now)
{
    struct zui *u = &W.ui;
    if (s.w < SIDEBAR_W) {
        draw_rail(s, now);
        return;
    }
    /* New tab, then the tabs, then the places. */
    struct zui_rect add = {s.x, s.y, s.w, 36};
    zui_add(u, NEW_TAB, ZUI_BUTTON, add, "", 0, W.count < TABS_MAX);
    zui_round(u, add, 8, u->theme.border);
    zui_round(u, (struct zui_rect){add.x + 1, add.y + 1, add.w - 2, add.h - 2}, 7,
              is_pressed(u, NEW_TAB) ? u->theme.subtle : is_hovered(u, NEW_TAB) ? mix(u->theme.raised, u->theme.text, 18) : u->theme.raised);
    zui_icon(u, "plus", add.x + 12, add.y + 10, 16, W.count < TABS_MAX ? ZUI_ICON_TEXT : ZUI_ICON_DISABLED);
    zui_text(u, add.x + 38, add.y + (add.h - zui_label.height) / 2, "New tab", 7, &zui_label, u->theme.text);
    zui_text(u, add.x + add.w - 12 - zui_measure("Ctrl+T", 6, &zui_caption), add.y + (add.h - zui_caption.height) / 2,
             "Ctrl+T", 6, &zui_caption, u->theme.secondary);
    char heading[24];
    snprintf(heading, sizeof(heading), "Tabs \xc2\xb7 %d", W.count);
    zui_text(u, s.x + 4, s.y + 52, heading, strlen(heading), &zui_caption, u->theme.secondary);
    keep_active_visible(s, ROW_H + ROW_GAP);
    int visible = rows_visible(s, ROW_H + ROW_GAP), y = s.y + 76;
    for (int i = W.tab_scroll; i < W.count && i < W.tab_scroll + visible; i++, y += ROW_H + ROW_GAP) {
        struct gui_window *t = W.tabs[i];
        struct zui_rect r = {s.x, y, s.w, ROW_H};
        bool active = i == W.active, under = u->hover_supported && inside(r, u->pointer_x, u->pointer_y);
        bool dragged = W.tab_dragging && W.drag_tab == i;
        zui_add(u, TAB_FIRST + i, ZUI_BUTTON, r, "", 0, 1);
        if (active) {
            zui_round(u, r, 6, mix(u->theme.subtle, u->theme.accent, 60));
            zui_round(u, (struct zui_rect){r.x + 1, r.y + 13, 3, 20}, 1, u->theme.accent);
        } else if (under || dragged)
            zui_round(u, r, 6, u->theme.raised);
        if (t->loading)
            tab_spinner(u, r.x + 12, r.y + 13, 20, now);
        else {
            tab_tile(u, t, r.x + 12, r.y + 13, 20);
            if (t->sleeping)
                zui_round_alpha(u, (struct zui_rect){r.x + 12, r.y + 13, 20, 20}, 5, u->theme.subtle, 150);
        }
        bool closable = active || under;
        int room = r.w - 42 - (closable ? 34 : 10);
        const char *name = *t->title ? t->title : !strcmp(t->current, NEBULA_START_URL) ? "New tab" : t->current;
        char sub[96], host[64];
        tab_host(t, host, sizeof(host));
        if (t->loading)
            copy_text(sub, sizeof(sub), "Loading\xe2\x80\xa6");
        else if (t->sleeping)
            snprintf(sub, sizeof(sub), "Sleeping \xc2\xb7 %s", host);
        else
            copy_text(sub, sizeof(sub), host);
        zui_clip(u, (struct zui_rect){r.x + 42, r.y, room > 0 ? room : 0, r.h});
        fit(u, r.x + 42, r.y + 5, name, strlen(name), room, active ? &zui_label : &zui_body,
            t->sleeping ? u->theme.secondary : u->theme.text);
        fit(u, r.x + 42, r.y + 25, sub, strlen(sub), room, &zui_caption, u->theme.secondary);
        zui_clip(u, (struct zui_rect){0, 0, u->width, u->height});
        if (closable)
            icon_button(u, TAB_CLOSE_FIRST + i, (struct zui_rect){r.x + r.w - 32, r.y + 11, 24, 24}, "x", 1);
    }
    int by = s.y + s.h - 2 * 38;
    divider(u, s.x + 4, by - 8, s.w - 8);
    menu_row(u, PLACE_BOOKMARKS, (struct zui_rect){s.x, by, s.w, 36}, "star", "Bookmarks", "Ctrl+B");
    menu_row(u, PLACE_HISTORY, (struct zui_rect){s.x, by + 38, s.w, 36}, "clock", "History", "Ctrl+H");
}

static void draw_tab_menu(void)
{
    struct zui *u = &W.ui;
    struct gui_window *t = W.menu_tab >= 0 && W.menu_tab < W.count ? W.tabs[W.menu_tab] : NULL;
    if (!t) {
        W.popover = POPOVER_NONE;
        return;
    }
    int rows = 5 + (W.count > 1) + (W.closed_count > 0);
    int width = 270, height = 12 + rows * 32 + 10;
    int x = bounded(W.menu_x, u->width - width - 4), y = bounded(W.menu_y, u->height - height - 4);
    W.popover_rect = (struct zui_rect){x, y, width, height};
    card(u, W.popover_rect);
    int ry = y + 6;
    menu_row(u, TAB_MENU_NEW, (struct zui_rect){x + 6, ry, width - 12, 32}, "plus", "New tab below", NULL);
    menu_row(u, TAB_MENU_DUPLICATE, (struct zui_rect){x + 6, ry + 32, width - 12, 32}, "copy", "Duplicate", NULL);
    menu_row(u, TAB_MENU_RELOAD, (struct zui_rect){x + 6, ry + 64, width - 12, 32}, "rotate-cw", "Reload", NULL);
    if (!t->sleeping && t != active_tab())
        menu_row(u, TAB_MENU_SLEEP, (struct zui_rect){x + 6, ry + 96, width - 12, 32}, "moon", "Put to sleep", NULL);
    else {
        zui_icon(u, "moon", x + 16, ry + 96 + 8, 16, ZUI_ICON_DISABLED);
        zui_text(u, x + 42, ry + 96 + (32 - zui_body.height) / 2, "Put to sleep", 12, &zui_body, u->theme.secondary);
    }
    divider(u, x + 10, ry + 132, width - 20);
    ry += 6;
    menu_row(u, TAB_MENU_CLOSE, (struct zui_rect){x + 6, ry + 128, width - 12, 32}, "x", "Close", "Ctrl+W");
    if (W.count > 1)
        menu_row(u, TAB_MENU_OTHERS, (struct zui_rect){x + 6, ry + 160, width - 12, 32}, "x", "Close other tabs", NULL);
    if (W.closed_count)
        menu_row(u, TAB_MENU_REOPEN, (struct zui_rect){x + 6, ry + 192, width - 12, 32}, "undo-2", "Reopen closed tab",
                 "Ctrl+Shift+T");
}

/* ---- The frame -------------------------------------------------------------------------------------------- */

/* The page renders into its own buffer only when the engine asks; every frame copies it into the inset. */
static bool render_page(struct gui_window *g, struct zui_rect pr)
{
    size_t need = (size_t)pr.w * pr.h;
    if (need > W.page_capacity) {
        uint32_t *grown = realloc(W.page, need * 4);
        if (!grown)
            return false;
        W.page = grown;
        W.page_capacity = need;
    }
    W.page_w = pr.w;
    W.page_h = pr.h;
    struct rect clip = {0, 0, pr.w, pr.h};
    struct page_raster raster = {W.page, pr.w, pr.h, pr.w, clip};
    if (g->source.bytes) {
        if (source_layout(&g->source, pr.w)) {
            source_paint(&g->source, &raster, &W.ui.theme);
            return false;
        }
        source_close(&g->source);
        note(g, "Not enough memory to show page source");
    }
    for (size_t i = 0; i < need; i++)
        W.page[i] = 0xffffffffu;
    if (!g->browser || !browser_window_redraw_ready(g->browser))
        return false;
    const struct redraw_context ctx = {
        .interactive = true, .background_images = true, .plot = &zenith_plotters, .priv = &raster};
    return browser_window_redraw(g->browser, -g->sx, -g->sy, &clip, &ctx);
}
/* Copies the page into the inset; the inset's rounded lower corners stay as the workspace drew them. */
static void blit_page(struct zui_rect pr)
{
    struct zui *u = &W.ui;
    enum { R = 12 };
    uint32_t corners[2][R * R];
    int bottom = pr.y + pr.h - R;
    for (int y = 0; y < R; y++)
        for (int x = 0; x < R; x++) {
            corners[0][y * R + x] = u->pixels[(long)(bottom + y) * u->width + pr.x + x];
            corners[1][y * R + x] = u->pixels[(long)(bottom + y) * u->width + pr.x + pr.w - R + x];
        }
    int w = pr.w < W.page_w ? pr.w : W.page_w, h = pr.h < W.page_h ? pr.h : W.page_h;
    for (int y = 0; y < h; y++)
        memcpy(u->pixels + (long)(pr.y + y) * u->width + pr.x, W.page + (long)y * W.page_w, (size_t)w * 4);
    for (int y = 0; y < R; y++)
        for (int x = 0; x < R; x++) {
            /* Outside the corner circle the workspace shows through. */
            int fx = R - x, fy = y + 1;
            if (fx * fx + fy * fy > R * R) {
                u->pixels[(long)(bottom + y) * u->width + pr.x + x] = corners[0][y * R + x];
            }
            int gx = x + 1;
            if (gx * gx + fy * fy > R * R)
                u->pixels[(long)(bottom + y) * u->width + pr.x + pr.w - R + x] = corners[1][y * R + x];
        }
}

static bool draw(void)
{
    struct zui *u = &W.ui;
    struct gui_window *g = active_tab();
    if (!g)
        return true;
    long now = zui_motion_clock();
    struct zui_rect pr = page_rect();
    bool document = false;
    if (g->page_dirty || W.page_w != pr.w || W.page_h != pr.h) {
        document = render_page(g, pr);
        g->page_dirty = false;
    }
    u->count = 0;
    u->material_count = 0;
    struct zui_workspace ws = zui_workspace_begin(u, sidebar_width());
    if (sidebar_width() > 0)
        draw_sidebar(ws.sidebar, now);
    draw_toolbar(g);
    draw_progress(g, now);
    W.strip_at = now;
    blit_page(pr);
    if (!g->source.bytes && g->caret_height > 0 && !g->address_focus && !g->find_focus) {
        zui_clip(u, pr);
        zui_fill(u, (struct zui_rect){pr.x + g->caret_x - g->sx, pr.y + g->caret_y - g->sy, 1, g->caret_height}, 0x303030);
        zui_clip(u, (struct zui_rect){0, 0, u->width, u->height});
    }
    W.popover_rect = (struct zui_rect){0, 0, 0, 0};
    if (overlays(g, now)) {
        if (g->find_open)
            draw_find_bar(g);
        draw_pill(g, now);
        if (g->card.open)
            draw_card(g);
        if (W.popover == POPOVER_MENU)
            draw_menu(g);
        else if (W.popover == POPOVER_INFO)
            draw_info(g);
        else if (W.popover == POPOVER_SUGGEST)
            draw_suggestions(g);
        else if (W.popover == POPOVER_TAB)
            draw_tab_menu();
        else if (W.popover >= POPOVER_BACK)
            draw_context_menu(g);
    }
    zui_update_hover(u);
    if (W.material)
        zui_material_repair(u);
    unsigned long bytes = (unsigned long)u->width * u->height * 4;
    long ret = W.material
                   ? zenith_gui_present_material(W.handle, u->pixels, bytes, u->materials, u->material_count)
                   : zenith_gui_present(W.handle, u->pixels, bytes);
    if (ret < 0)
        return false;
    if (document && g->present_document) {
        static const char msg[] = "NETSURF_DOCUMENT_PRESENTED\n";
        zenith_write(msg, sizeof(msg) - 1);
        g->present_document = false;
    }
    u->dirty = 0;
    return true;
}

/* ---- Actions ---------------------------------------------------------------------------------------------- */

static nserror parse_address(const char *text, nsurl **url)
{
    if (!text || !*text)
        return NSERROR_BAD_URL;
    if (!strcmp(text, PACKAGED_WELCOME) || !strcmp(text, RESOURCES "welcome.html"))
        return nsurl_create(NEBULA_START_URL, url);
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
        W.popover = POPOVER_NONE;
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
    if (g->source.bytes) {
        note(g, "Return to the page to search it");
        return;
    }
    if (!g->find.text)
        zui_input_init(&g->find, g->find_text, sizeof(g->find_text));
    W.popover = POPOVER_NONE;
    g->find_open = g->find_focus = true;
    g->address_focus = g->select_address = false;
    reset_address(g, g->current);
    repaint(g);
}
/* Only inspect completed, user-visible text documents. The toolbar address can still
 * name a failed HTTPS request while the loaded handle holds a private engine error page. */
static void toggle_source(struct gui_window *g)
{
    if (g->source.bytes) {
        source_close(&g->source);
        return;
    }
    struct hlcache_handle *h = g->browser ? browser_window_get_content(g->browser) : NULL;
    nsurl *url = h ? hlcache_handle_get_url(h) : NULL;
    if (!url || g->loading || content_get_status(h) != CONTENT_STATUS_DONE ||
        page_kind(nsurl_access(url)) == PAGE_INTERNAL) {
        note(g, "Page source is unavailable for this page");
        return;
    }
    content_type type = content_get_type(h);
    if (type != CONTENT_HTML && type != CONTENT_TEXTPLAIN && type != CONTENT_CSS) {
        note(g, "This page has no text source");
        return;
    }
    size_t bytes = 0;
    const uint8_t *body = content_get_source_data(h, &bytes);
    if ((!body && bytes) || bytes >= INT_MAX) {
        note(g, "Page source is unavailable for this page");
        return;
    }
    g->source.bytes = body ? body : (const uint8_t *)"";
    g->source.length = bytes;
    close_find(g);
    g->address_focus = g->select_address = false;
    reset_address(g, g->current);
    g->card.open = false;
    W.page_pressed = W.dragging = false;
    W.note_until = 0;
}
/* ---- Tabs ------------------------------------------------------------------------------------------------- */

/* Every live tab lays out again for a new page size (a resize, or the sidebar shown, folded or hidden). */
static void relayout_all(void)
{
    struct zui_rect pr = page_rect();
    for (int i = 0; i < W.count; i++) {
        struct gui_window *t = W.tabs[i];
        if (t->browser && (t->laid_w != pr.w || t->laid_h != pr.h)) {
            t->laid_w = pr.w;
            t->laid_h = pr.h;
            browser_window_schedule_reformat(t->browser);
        }
        t->page_dirty = true;
    }
    W.ui.dirty = 1;
}
/* Past LIVE_TABS live pages, the least recently used background tab lets go of its page. */
static void sleep_tab(struct gui_window *t)
{
    if (!t->browser || t == active_tab())
        return;
    t->wake_sy = t->sy;
    t->keep = true;
    t->loading = false;
    browser_window_destroy(t->browser);
}
static void enforce_live_limit(void)
{
    for (;;) {
        int live = 0;
        struct gui_window *oldest = NULL;
        for (int i = 0; i < W.count; i++) {
            struct gui_window *t = W.tabs[i];
            if (!t->browser)
                continue;
            live++;
            if (t != active_tab() && (!oldest || t->used_at < oldest->used_at))
                oldest = t;
        }
        if (live <= LIVE_TABS || !oldest)
            return;
        sleep_tab(oldest);
    }
}
static void wake_tab(struct gui_window *t)
{
    nsurl *url = NULL;
    if (t->browser || nsurl_create(*t->current ? t->current : NEBULA_START_URL, &url) != NSERROR_OK)
        return;
    W.waking = t;
    if (browser_window_create(BW_CREATE_HISTORY, url, NULL, NULL, NULL) != NSERROR_OK || W.waking) {
        W.waking = NULL;
        note(t, "Could not wake this tab");
    }
    nsurl_unref(url);
}
static void activate(int index)
{
    if (index < 0 || index >= W.count)
        return;
    if (index != W.active)
        W.children = 0;
    W.active = index;
    struct gui_window *t = W.tabs[index];
    t->used_at = zui_motion_clock();
    W.popover = POPOVER_NONE;
    W.page_pressed = W.dragging = false;
    if (t->sleeping)
        wake_tab(t);
    else if (t->browser && (t->laid_w != page_rect().w || t->laid_h != page_rect().h)) {
        t->laid_w = page_rect().w;
        t->laid_h = page_rect().h;
        browser_window_schedule_reformat(t->browser);
    }
    show_title(t);
    enforce_live_limit();
    repaint(t);
}
/* Opens a tab at an address: after the active one, or after the tabs already opened from it in the background. */
static struct gui_window *new_tab(const char *address, bool foreground, nsurl *referrer)
{
    if (W.count >= TABS_MAX) {
        note(active_tab(), "16 tabs are open: close one first");
        return NULL;
    }
    nsurl *url = NULL;
    if (parse_address(address, &url) != NSERROR_OK)
        return NULL;
    W.insert_at = W.active < 0 ? W.count : W.active + 1 + (foreground ? 0 : W.children);
    W.foreground = foreground || W.count == 0;
    struct gui_window *parent = active_tab();
    W.created = NULL;
    nserror err = browser_window_create(BW_CREATE_HISTORY | (parent ? BW_CREATE_TAB : 0) | (foreground ? BW_CREATE_FOREGROUND : 0),
                                        url, referrer, parent ? parent->browser : NULL, NULL);
    nsurl_unref(url);
    W.insert_at = -1;
    W.foreground = false;
    struct gui_window *t = W.created;
    if (err != NSERROR_OK || !t)
        return NULL;
    t->laid_w = page_rect().w;
    t->laid_h = page_rect().h;
    if (!foreground)
        W.children++;
    else if (t)
        activate(tab_index(t));
    enforce_live_limit();
    W.ui.dirty = 1;
    return t;
}
static void remember_closed(const struct gui_window *t)
{
    if (!*t->current || !strcmp(t->current, NEBULA_START_URL))
        return;
    memmove(W.closed[1], W.closed[0], sizeof(W.closed[0]) * (CLOSED_KEPT - 1));
    copy_text(W.closed[0], sizeof(W.closed[0]), t->current);
    if (W.closed_count < CLOSED_KEPT)
        W.closed_count++;
}
static void close_tab(int index)
{
    if (index < 0 || index >= W.count)
        return;
    struct gui_window *t = W.tabs[index];
    remember_closed(t);
    if (W.popover && (W.popover != POPOVER_TAB || W.menu_tab == index))
        W.popover = POPOVER_NONE;
    if (t->card.ctx)
        download_finished((struct gui_download_window *)t);
    if (t->browser)
        browser_window_destroy(t->browser);   /* the core calls destroy_window, which forgets the tab */
    else
        forget_tab(t);
    if (active_tab())
        activate(W.active);
}
static void reopen_closed(void)
{
    if (!W.closed_count)
        return;
    char url[2048];
    copy_text(url, sizeof(url), W.closed[0]);
    memmove(W.closed[0], W.closed[1], sizeof(W.closed[0]) * (CLOSED_KEPT - 1));
    W.closed_count--;
    new_tab(url, true, NULL);
}
static void move_tab(int from, int to)
{
    if (from == to || from < 0 || to < 0 || from >= W.count || to >= W.count)
        return;
    struct gui_window *t = W.tabs[from], *current = active_tab();
    if (from < to)
        memmove(&W.tabs[from], &W.tabs[from + 1], (size_t)(to - from) * sizeof(W.tabs[0]));
    else
        memmove(&W.tabs[to + 1], &W.tabs[to], (size_t)(from - to) * sizeof(W.tabs[0]));
    W.tabs[to] = t;
    W.active = tab_index(current);
    W.ui.dirty = 1;
}
static void toggle_sidebar(void)
{
    W.sidebar_hidden = !W.sidebar_hidden;
    W.popover = POPOVER_NONE;
    relayout_all();
}

static void ui_action(struct gui_window *g, int id)
{
    if (W.popover == POPOVER_SUGGEST && id >= SUGGEST_FIRST && id < SUGGEST_FIRST + W.suggestion_count) {
        open_address(g, W.suggestions[id - SUGGEST_FIRST]->url);
        return;
    }
    if (id >= HISTORY_FIRST && id < HISTORY_FIRST + W.entry_count &&
        (W.popover == POPOVER_BACK || W.popover == POPOVER_FORWARD)) {
        /* The enumeration API exposes const entries; its navigation API takes the same entry as mutable. */
        struct history_entry *entry = (struct history_entry *)W.entries[id - HISTORY_FIRST];
        W.popover = POPOVER_NONE;
        browser_window_history_go(g->browser, entry, false);
        repaint(g);
        return;
    }
    if (id >= TAB_CLOSE_FIRST && id < TAB_CLOSE_FIRST + W.count) {
        close_tab(id - TAB_CLOSE_FIRST);
        return;
    }
    if (id >= TAB_FIRST && id < TAB_FIRST + W.count) {
        if (!W.tab_dragging)
            activate(id - TAB_FIRST);
        return;
    }
    int menu_tab = W.menu_tab;
    if (id != MENU && id != INFO && id != ZOOM_IN && id != ZOOM_OUT && id != ZOOM_RESET)
        W.popover = POPOVER_NONE;
    switch (id) {
    case NEW_TAB:
        if (new_tab(NEBULA_START_URL, true, NULL) && active_tab()) {
            /* A new tab waits for an address: the field is empty and focused. */
            reset_address(active_tab(), "");
            active_tab()->address_focus = true;
            active_tab()->select_address = false;
        }
        return;
    case PLACE_BOOKMARKS:
    case PLACE_HISTORY:
        open_address(g, NEBULA_START_URL);
        return;
    case MENU_SIDEBAR:
    case SIDEBAR_TOGGLE:
        toggle_sidebar();
        return;
    case LINK_TAB:
        if (W.context_link)
            new_tab(nsurl_access(W.context_link), false, NULL);
        return;
    case TAB_MENU_NEW:
        if (menu_tab >= 0 && menu_tab < W.count) {
            W.active = menu_tab;
            new_tab(NEBULA_START_URL, true, NULL);
        }
        return;
    case TAB_MENU_DUPLICATE:
        if (menu_tab >= 0 && menu_tab < W.count && *W.tabs[menu_tab]->current) {
            W.active = menu_tab;
            new_tab(W.tabs[menu_tab]->current, true, NULL);
        }
        return;
    case TAB_MENU_RELOAD:
        if (menu_tab >= 0 && menu_tab < W.count) {
            struct gui_window *t = W.tabs[menu_tab];
            if (t->browser)
                browser_window_reload(t->browser, false);
            else
                activate(menu_tab);
        }
        return;
    case TAB_MENU_SLEEP:
        if (menu_tab >= 0 && menu_tab < W.count)
            sleep_tab(W.tabs[menu_tab]);
        W.ui.dirty = 1;
        return;
    case TAB_MENU_CLOSE:
        close_tab(menu_tab);
        return;
    case TAB_MENU_OTHERS:
        if (menu_tab >= 0 && menu_tab < W.count) {
            struct gui_window *keep = W.tabs[menu_tab];
            for (int i = W.count - 1; i >= 0; i--)
                if (i < W.count && W.tabs[i] != keep)
                    close_tab(i);
            activate(tab_index(keep));
        }
        return;
    case TAB_MENU_REOPEN:
        reopen_closed();
        return;
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
        W.popover = W.popover == POPOVER_MENU ? POPOVER_NONE : POPOVER_MENU;
        break;
    case INFO:
    case MENU_INFO:
        W.popover = W.popover == POPOVER_INFO && id == INFO ? POPOVER_NONE : POPOVER_INFO;
        break;
    case ADDRESS:
        g->address_focus = true;
        g->select_address = true;
        reset_address(g, g->current);
        suggest(g);
        break;
    case MENU_HOME:
        open_address(g, NEBULA_START_URL);
        break;
    case BOOKMARK: {
        const char *url = profile_url(g);
        if (!url) {
            note(g, "Open a page to bookmark it");
            break;
        }
        bool add = !nebula_profile_bookmarked(&W.profile, url);
        if (nebula_profile_set_bookmark(&W.profile, url, browser_window_get_title(g->browser), add))
            note(g, add ? "Bookmark saved" : "Bookmark removed");
        else
            note(g, add && W.profile.bookmark_count >= NEBULA_BOOKMARKS ? "Bookmark list is full" : "Could not save bookmarks");
        break;
    }
    case ZOOM_OUT:
    case ZOOM_IN:
    case ZOOM_RESET:
        set_zoom(g, id == ZOOM_OUT ? -1 : id == ZOOM_IN ? 1 : 0);
        break;
    case MENU_FIND:
        open_find(g);
        break;
    case MENU_SOURCE:
        toggle_source(g);
        break;
    case MENU_COPY:
    case INFO_COPY:
        copy_to_clipboard(g->current, strlen(g->current));
        note(g, "Address copied");
        break;
    case MENU_GUIDE:
        open_address(g, GUIDE_URL);
        break;
    case MENU_SCRIPTS: {
        nsurl *url = script_page(g);
        if (!url)
            break;
        if (tab_allows_scripts(g, url)) {
            nsurl_unref(g->script_scope);
            g->script_scope = NULL;
            browser_window_revoke_scripts(g->browser);
        } else {
            allow_page_scripts(url);
        }
        nserror err = browser_window_reload(g->browser, false);
        if (err != NSERROR_OK)
            navigation_error(g, err);
        break;
    }
    case LINK_OPEN:
        if (W.context_link) {
            struct hlcache_handle *content = browser_window_get_content(g->browser);
            nserror err = browser_window_navigate(g->browser, W.context_link,
                content ? hlcache_handle_get_url(content) : NULL, BW_NAVIGATE_HISTORY, NULL, NULL, NULL);
            if (err != NSERROR_OK)
                navigation_error(g, err);
        }
        break;
    case LINK_COPY:
        if (W.context_link) {
            copy_to_clipboard(nsurl_access(W.context_link), nsurl_length(W.context_link));
            note(g, "Link address copied");
        }
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
        W.done = true;
        return;
    }
    /* Tabs. */
    if (ctrl && shift && letter == 't') {
        reopen_closed();
        return;
    }
    if (ctrl && shift && letter == 's') {
        toggle_sidebar();
        return;
    }
    if (ctrl && !shift && letter == 't') {
        ui_action(g, NEW_TAB);
        return;
    }
    if (ctrl && letter == 'w') {
        close_tab(W.active);
        return;
    }
    if (ctrl && e->extra == 4 && W.count > 1) {
        activate((W.active + (shift ? W.count - 1 : 1)) % W.count);
        return;
    }
    if (ctrl && !e->extra && letter >= '1' && letter <= '9') {
        activate(letter == '9' ? W.count - 1 : letter - '1');
        return;
    }
    if (ctrl && (letter == 'b' || letter == 'h')) {
        open_address(g, NEBULA_START_URL);
        return;
    }
    if (e->extra == 1 && (W.popover || g->card.open)) {
        W.popover = POPOVER_NONE;
        g->card.open = false;
        if (g->address_focus) {
            g->address_focus = g->select_address = false;
            reset_address(g, g->current);
        }
        repaint(g);
        return;
    }
    if (ctrl && letter == 'd') {
        ui_action(g, BOOKMARK);
        return;
    }
    if ((ctrl && !alt && !shift && letter == 'u') ||
        (g->source.bytes && !g->address_focus && e->extra == 1)) {
        ui_action(g, MENU_SOURCE);
        return;
    }
    if (alt && e->extra == 10) {
        ui_action(g, MENU_HOME);
        return;
    }
    if (ctrl && (letter == 'l' || (letter == 'a' && g->address_focus))) {
        W.popover = POPOVER_NONE;
        g->find_focus = false;
        g->address_focus = true;
        g->select_address = true;
        suggest(g);
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
        if (W.popover == POPOVER_SUGGEST && (e->extra == 7 || e->extra == 8)) {
            if (e->extra == 8)
                W.selection = W.selection + 1 < W.suggestion_count ? W.selection + 1 : -1;
            else
                W.selection = W.selection >= 0 ? W.selection - 1 : W.suggestion_count - 1;
            repaint(g);
            return;
        }
        if (e->extra == 1) {
            g->address_focus = false;
            g->select_address = false;
            reset_address(g, g->current);
            repaint(g);
            return;
        }
        if (e->extra == 2) {
            open_address(g, W.popover == POPOVER_SUGGEST && W.selection >= 0
                         ? W.suggestions[W.selection]->url : g->location);
            return;
        }
        if (g->select_address && !ctrl && !alt) {
            if ((!e->extra && e->code >= 32) || e->extra == 3 || e->extra == 9)
                reset_address(g, "");
            g->select_address = false;
            W.ui.dirty = 1;
        }
        if (zui_input_event(&g->address, e))
            suggest(g);
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
    if (g->source.bytes) {
        long target = g->source.first;
        int rows = source_page_rows(page_rect().h);
        switch (e->extra) {
        case 7: target--; break;
        case 8: target++; break;
        case 10: target = 0; break;
        case 11: target = g->source.rows; break;
        case 12: target -= rows; break;
        case 13: target += rows; break;
        default: return;
        }
        source_scroll(&g->source, target, page_rect().h);
        repaint(g);
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

static bool on_popover(const struct gui_window *g, int x, int y)
{
    return (W.popover || g->card.open) && inside(W.popover_rect, x, y);
}
/* Everything but the page: the sidebar, the toolbar row, the find bar and open overlays. */
static bool on_chrome(const struct gui_window *g, int x, int y)
{
    struct zui_rect pr = page_rect();
    return !inside(pr, x, y) || (g->find_open && y >= pr.y + pr.h - FIND_BAR) || on_popover(g, x, y);
}
static bool on_sidebar(int x)
{
    return sidebar_width() > 0 && x < content_rect().x - 12;
}
/* The tab row under a sidebar position, or -1. */
static int tab_at(int x, int y)
{
    if (!on_sidebar(x))
        return -1;
    for (int i = 0; i < W.ui.count; i++) {
        int id = W.ui.controls[i].id;
        if (id >= TAB_FIRST && id < TAB_FIRST + W.count && inside(zui_control_area(&W.ui, i), x, y))
            return id - TAB_FIRST;
    }
    return -1;
}

/* The pointer left the page (onto the chrome or out of the window): a link's address no longer applies. */
static void leave_page(struct gui_window *g)
{
    if (!g->loading && g->status[0])
        status(g, "");
}

static bool collect_entry(const struct browser_window *bw, int x0, int y0, int x1, int y1,
                          const struct history_entry *entry, void *context)
{
    (void)bw; (void)x0; (void)y0; (void)x1; (void)y1; (void)context;
    if (W.entry_count >= HISTORY_ROWS || (W.entry_count + 1) * 32 + 12 > page_rect().h)
        return false;
    W.entries[W.entry_count++] = entry;
    return true;
}

static nsurl *link_at(struct gui_window *g, int x, int y)
{
    struct zui_rect pr = page_rect();
    struct browser_window_features features;
    int px = bounded((long)x - pr.x + g->sx, INT_MAX), py = bounded((long)y - pr.y + g->sy, INT_MAX);
    if (g->browser && browser_window_get_features(g->browser, px, py, &features) == NSERROR_OK && features.link)
        return features.link;
    return NULL;
}

static void secondary_press(struct gui_window *g, int x, int y)
{
    if (g->card.open || on_popover(g, x, y))
        return;
    W.popover = POPOVER_NONE;
    W.entry_count = 0;
    if (W.context_link) {
        nsurl_unref(W.context_link);
        W.context_link = NULL;
    }
    W.menu_x = x;
    W.menu_y = y;
    int tab = tab_at(x, y);
    if (tab >= 0) {
        W.menu_tab = tab;
        W.popover = POPOVER_TAB;
    } else if (y < page_rect().y && !on_sidebar(x)) {
        struct zui_rect back = toolbar_button(TB_BACK), forward = toolbar_button(TB_FORWARD);
        if (inside(back, x, y) && g->browser) {
            browser_window_history_enumerate_back(g->browser, collect_entry, g);
            if (W.entry_count) W.popover = POPOVER_BACK;
            W.menu_x = back.x;
        } else if (inside(forward, x, y) && g->browser) {
            browser_window_history_enumerate_forward(g->browser, collect_entry, g);
            if (W.entry_count) W.popover = POPOVER_FORWARD;
            W.menu_x = forward.x;
        }
        W.menu_y = page_rect().y - 4;
    } else if (!g->source.bytes && !on_chrome(g, x, y)) {
        nsurl *link = link_at(g, x, y);
        if (link) {
            W.context_link = nsurl_ref(link);
            W.popover = POPOVER_LINK;
        }
    }
    repaint(g);
}
/* Middle button: a link opens in a background tab; on a tab row it closes that tab. */
static void middle_press(struct gui_window *g, int x, int y)
{
    int tab = tab_at(x, y);
    if (tab >= 0) {
        close_tab(tab);
        return;
    }
    nsurl *link = g->source.bytes || on_chrome(g, x, y) ? NULL : link_at(g, x, y);
    if (link)
        new_tab(nsurl_access(link), false, NULL);
}

/* Dragging a tab row moves it among the others. */
static bool drag_tab(const struct zenith_gui_event *e)
{
    if (W.drag_tab < 0 || W.drag_tab >= W.count)
        return false;
    if (e->type == Z_GUI_UP) {
        bool moved = W.tab_dragging;
        W.drag_tab = -1;
        W.tab_dragging = false;
        if (moved) {
            W.ui.pressed = -1;
            W.ui.dirty = 1;
        }
        return moved;
    }
    if (e->type != Z_GUI_DRAG && e->type != Z_GUI_MOVE)
        return false;
    if (!W.tab_dragging && labs((long)e->y - W.drag_y) <= 6)
        return false;
    W.tab_dragging = true;
    int row = sidebar_width() >= SIDEBAR_W ? ROW_H + ROW_GAP : RAIL_ROW;
    int to = W.drag_tab + (int)((e->y - W.drag_y) / row);
    to = bounded(to, W.count - 1);
    if (to != W.drag_tab) {
        move_tab(W.drag_tab, to);
        W.drag_y += (to - W.drag_tab) * row;
        W.drag_tab = to;
    }
    W.ui.dirty = 1;
    return true;
}

static void pointer(struct gui_window *g, const struct zenith_gui_event *e)
{
    int x = e->x, y = e->y;
    if (e->type == Z_GUI_SECONDARY && e->code == 2) {
        secondary_press(g, x, y);
        return;
    }
    if (e->type == Z_GUI_SECONDARY && e->code == 3) {
        middle_press(g, x, y);
        return;
    }
    /* Taking pointer capture clears the host hover target. Its (-1,-1)
     * notification is also delivered between a normal press and release. */
    if (e->type == Z_GUI_MOVE && (x < 0 || y < 0)) {
        zui_event(&W.ui, e);
        W.ui.pointer_x = W.ui.pointer_y = -1;
        W.ui.dirty = 1;
        if (!W.page_pressed)
            leave_page(g);
        return;
    }
    if (e->type == Z_GUI_MOVE || e->type == Z_GUI_DRAG) {
        /* Rows and buttons are hovered by position, also while the pointer is over the page. */
        bool was_chrome = W.ui.pointer_x >= 0 && on_chrome(g, W.ui.pointer_x, W.ui.pointer_y);
        W.ui.pointer_x = x;
        W.ui.pointer_y = y;
        if (was_chrome || on_chrome(g, x, y))
            W.ui.dirty = 1;
    }
    if (drag_tab(e))
        return;
    struct zui_rect pr = page_rect();
    /* A press outside the open popover closes it. On the page it goes no further; elsewhere it also
     * reaches its control (the menu button and the lock toggle their own popover). */
    if (e->type == Z_GUI_DOWN && W.popover && !on_popover(g, x, y)) {
        W.popover = POPOVER_NONE;
        repaint(g);
        if (inside(pr, x, y))
            return;
    }
    if (e->type == Z_GUI_SCROLL && on_sidebar(x)) {
        W.tab_scroll = bounded(W.tab_scroll + (e->code > 0 ? 1 : -1), W.count - 1);
        W.ui.dirty = 1;
        return;
    }
    if (!W.page_pressed && (on_chrome(g, x, y) || W.ui.pressed >= 0)) {
        if (e->type == Z_GUI_MOVE)
            leave_page(g);
        if (e->type == Z_GUI_DOWN) {
            int tab = tab_at(x, y);
            W.drag_tab = tab;
            W.drag_y = y;
            W.tab_dragging = false;
        }
        int id = zui_event(&W.ui, e);
        if (id)
            ui_action(g, id);
        else if (e->type == Z_GUI_DOWN || e->type == Z_GUI_UP)
            W.ui.dirty = 1;
        g = active_tab();
        if (g && e->type == Z_GUI_DOWN && g->find_open && inside(pr, x, y) && y >= pr.y + pr.h - FIND_BAR &&
            x < pr.x + pr.w - 3 * 34 - 8) {
            g->find_focus = true;
            g->address_focus = false;
            repaint(g);
        }
        return;
    }
    if (g->source.bytes) {
        if (e->type == Z_GUI_SCROLL) {
            source_scroll(&g->source, g->source.first - 3L * e->code, pr.h);
            repaint(g);
        } else if (e->type == Z_GUI_DOWN && g->address_focus) {
            g->address_focus = g->select_address = false;
            reset_address(g, g->current);
        }
        return;
    }
    int px = (int)bounded((long)x - pr.x + g->sx, INT_MAX), py = (int)bounded((long)y - pr.y + g->sy, INT_MAX);
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
        /* Ctrl+click opens a link in a background tab instead of following it here. */
        nsurl *link = (e->modifiers & 2) ? link_at(g, x, y) : NULL;
        if (link) {
            new_tab(nsurl_access(link), false, NULL);
            return;
        }
        W.page_pressed = true;
        W.dragging = false;
        W.press_x = px;
        W.press_y = py;
        browser_window_mouse_click(g->browser, BROWSER_MOUSE_PRESS_1, px, py);
    } else if (e->type == Z_GUI_UP && W.page_pressed) {
        if (inside(pr, x, y) && !W.dragging)
            browser_window_mouse_click(g->browser, BROWSER_MOUSE_CLICK_1, px, py);
        browser_window_mouse_track(g->browser, BROWSER_MOUSE_HOVER, px, py);
        W.page_pressed = false;
        W.dragging = false;
    } else if (e->type == Z_GUI_MOVE || e->type == Z_GUI_DRAG) {
        if (W.page_pressed && !W.dragging &&
            (labs((long)px - W.press_x) > 3 || labs((long)py - W.press_y) > 3)) {
            browser_window_mouse_click(g->browser, BROWSER_MOUSE_DRAG_1, W.press_x, W.press_y);
            W.dragging = true;
        }
        browser_window_mouse_track(
            g->browser, W.dragging ? (BROWSER_MOUSE_DRAG_ON | BROWSER_MOUSE_HOLDING_1) : BROWSER_MOUSE_HOVER,
            px, py);
    }
}

int zenith_browser_run(const char *address)
{
    zui_init(&W.ui, starting_pixels, START_W, START_H);
    zui_icons_use_packages();
    zui_apply_appearance(&W.ui);
    zui_apply_style(&W.ui);
    struct zenith_material_caps caps = {0};
    W.material = zenith_gui_material_caps(&caps) == 0 && caps.version == 2;
    W.handle = W.material ? zenith_gui_open_material(START_W, START_H, "Nebula", 6)
                          : zenith_gui_open(START_W, START_H, "Nebula", 6);
    if (W.handle < 0)
        return 1;
    W.ui.foreground = W.material;
    if (zenith_gui_interactive(W.handle) < 0 || zenith_gui_resize_enable(W.handle) < 0) {
        zenith_gui_close(W.handle);
        return 1;
    }
    W.ui.hover_supported = zenith_gui_input(W.handle, 15) == 0 || zenith_gui_input(W.handle, 7) == 0 ||
                           zenith_gui_pointer_motion(W.handle) == 0;
    struct gui_window *first = new_tab(address, true, NULL);
    if (!first || !active_tab()) {
        zenith_gui_close(W.handle);
        return 1;
    }
    reset_address(first, address);
    static const char ready[] = "NETSURF_READY\n";
    zenith_write(ready, sizeof(ready) - 1);
    int result = 0;
    while (!W.done) {
        run_alarms();
        struct gui_window *g = active_tab();
        if (W.done || !g)
            break;
        long now = zui_motion_clock();
        if (W.note_until && now >= W.note_until) {
            W.note_until = 0;
            repaint(g);
        }
        bool moving = false;
        for (int i = 0; i < W.count && !moving; i++)
            moving = W.tabs[i]->loading;
        bool strip = moving && now - W.strip_at >= 40;
        if ((W.ui.dirty || g->page_dirty || strip) && !draw()) {
            result = zui_window_closed(W.handle) ? 0 : 1;
            break;
        }
        struct zenith_gui_event e = {0};
        long rc = zenith_gui_poll(W.handle, &e);
        if (rc < 0) {
            result = zui_window_closed(W.handle) ? 0 : 1;
            break;
        }
        if (!rc)
            continue;
        if (e.type == Z_GUI_CLOSE || e.type == Z_GUI_CLOSE_INTENT)
            break;
        if (e.type == Z_GUI_RESIZE) {
            if (zui_apply_viewport(&W.ui, W.handle, (unsigned long)START_W * START_H) < 0) {
                result = zui_window_closed(W.handle) ? 0 : 1;
                break;
            }
            relayout_all();
        } else if (e.type == Z_GUI_RESET || e.type == Z_GUI_FOCUS) {
            zui_event(&W.ui, &e);
            zui_apply_appearance(&W.ui);
            zui_apply_style(&W.ui);
            W.page_pressed = false;
            W.dragging = false;
            W.drag_tab = -1;
            W.tab_dragging = false;
            repaint(g);
        } else if (e.type == Z_GUI_KEY)
            key(g, &e);
        else
            pointer(g, &e);
    }
    if (W.context_link)
        nsurl_unref(W.context_link);
    while (W.count) {
        struct gui_window *t = W.tabs[W.count - 1];
        if (t->browser)
            browser_window_destroy(t->browser);
        else
            forget_tab(t);
    }
    free(W.page);
    zenith_gui_close(W.handle);
    return result;
}

int main(int argc, char **argv)
{
    if (argc == 2 && !strcmp(argv[1], "--version")) {
        puts("netsurf " ZENITH_BROWSER_VERSION " (Nebula, NetSurf 3.11)");
        return 0;
    }
    bool opt_in = argc == 3 && !strcmp(argv[1], "--javascript");
    if ((argc > 2 && !opt_in) || (opt_in && (!*argv[2] || argv[2][0] == '-')) ||
        (argc == 2 && argv[1][0] == '-')) {
        puts("netsurf [--javascript] [URL | /absolute/path.html]");
        return argc == 2 && !strcmp(argv[1], "--help") ? 0 : 2;
    }
    if (netsurf_register(&operations) != NSERROR_OK ||
        nsoption_init(NULL, &nsoptions, &nsoptions_default) != NSERROR_OK)
        return 1;
    nsoption_set_bool(enable_javascript, true); /* The owning tab must also grant the document. */
    nsoption_set_bool(target_blank, true);   /* target=_blank opens a tab */
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
    nebula_profile_load(&W.profile);
    const char *address = opt_in ? argv[2] : argc == 2 ? argv[1] : NEBULA_START_URL;
    if (opt_in) {
        nsurl *url = NULL;
        bool allowed = parse_address(address, &url) == NSERROR_OK && allow_page_scripts(url);
        if (url)
            nsurl_unref(url);
        if (!allowed) {
            puts("netsurf: JavaScript is unavailable for this address");
            result = 2;
            goto exit_core;
        }
    }
    if (zenith_fetch_register() == NSERROR_OK && nebula_start_register(&W.profile, &W.ui.theme) == NSERROR_OK)
        result = zenith_browser_run(address);
exit_core:
    if (W.initial_script_scope) {
        nsurl_unref(W.initial_script_scope);
        W.initial_script_scope = NULL;
    }
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
