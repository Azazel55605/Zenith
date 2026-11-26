/* Host integration of the native frontend and actual NetSurf core. Replace
 * only the clock, installed resources, window title and network transport.
 * tools/netsurf-smoke.py exercises those native boundaries on Zenith. */
#define main zenith_browser_main
#include "../../../userland/netsurf/frontend.c"
#undef main
#include "../../../userland/netsurf/fetch-transport.h"
#include "netsurf/content.h"
#include <assert.h>
#include <time.h>

static const char *resources;
static unsigned connections;

long zui_motion_clock(void)
{
    struct timespec ts;
    assert(clock_gettime(CLOCK_MONOTONIC, &ts) == 0);
    return 1000L * ts.tv_sec + ts.tv_nsec / 1000000;
}

static nsurl *host_resource(const char *name)
{
    char path[PATH_MAX];
    int length = snprintf(path, sizeof(path), "%s/%s", resources, name);
    nsurl *url = NULL;
    if (length > 0 && (size_t)length < sizeof(path) && access(path, R_OK) == 0)
        netsurf_path_to_nsurl(path, &url);
    return url;
}

static void host_title(struct gui_window *g, const char *text)
{
    (void)g;
    (void)text;
}

long zenith_web_connect(const char *host, unsigned port, bool tls)
{
    assert(!strcmp(host, "fixture.invalid") && tls && port == 443);
    connections++;
    return -13;
}
const char *zenith_web_error(long code)
{
    assert(code == -13);
    return "The fixture certificate was not trusted.";
}
long zenith_web_send(long fd, const void *data, size_t bytes)
{
    (void)fd; (void)data; (void)bytes;
    abort();
}
long zenith_web_read(long fd, void *data, size_t bytes)
{
    (void)fd; (void)data; (void)bytes;
    abort();
}
void zenith_web_close(long fd) { (void)fd; abort(); }
unsigned zenith_web_attempts(void) { return connections; }
unsigned zenith_web_refused(void) { return 0; }

static void await_document(const char *title)
{
    long deadline = zui_motion_clock() + 5000;
    while (zui_motion_clock() < deadline) {
        run_alarms();
        if (browser_window_redraw_ready(window.browser) && !window.loading &&
            !strcmp(browser_window_get_title(window.browser), title))
            return;
        usleep(1000);
    }
    fprintf(stderr, "Expected document '%s', found '%s' (%s)\n", title,
            browser_window_get_title(window.browser), window.status);
    abort();
}

static void enter_address(const char *address)
{
    window.address_focus = true;
    reset_address(&window, address);
    key(&window, &(struct zenith_gui_event){.type = Z_GUI_KEY, .extra = 2});
}

int main(int argc, char **argv)
{
    assert(argc == 3);
    resources = argv[1];
    window_ops.set_title = host_title;
    fetch_ops.get_resource_url = host_resource;
    assert(netsurf_register(&operations) == NSERROR_OK);
    assert(nsoption_init(NULL, &nsoptions, &nsoptions_default) == NSERROR_OK);
    nsoption_set_bool(enable_javascript, false);
    nsoption_set_bool(target_blank, false);
    nsoption_set_uint(disc_cache_size, 0);
    assert(messages_add_from_file(argv[2]) == NSERROR_OK);
    assert(netsurf_init(NULL) == NSERROR_OK);
    assert(zenith_fetch_register() == NSERROR_OK);
    bitmap_fmt_t format = {.layout = BITMAP_LAYOUT_ARGB8888, .pma = false};
    bitmap_set_format(&format);
    zui_init(&window.ui, starting_pixels, START_W, START_H);

    const char *page = "data:text/html,%3Ctitle%3ELocal%20test%3C/title%3E"
                       "%3Cbody%20style%3D%27background%3A%23c7d4a5%27%3E"
                       "%3Ca%20href%3D%27https://fixture.invalid/%27%3EVisit%3C/a%3E%3C/body%3E";
    nsurl *url;
    assert(nsurl_create(page, &url) == NSERROR_OK);
    assert(browser_window_create(BW_CREATE_HISTORY, url, NULL, NULL, NULL) == NSERROR_OK);
    nsurl_unref(url);
    await_document("Local test");
    struct rect clip = {0, 0, START_W, START_H - TOOLBAR};
    struct page_raster raster = {starting_pixels, START_W, clip.y1, START_W, clip};
    const struct redraw_context ctx = {
        .interactive = true, .background_images = true, .plot = &zenith_plotters, .priv = &raster};
    assert(browser_window_redraw(window.browser, 0, 0, &clip, &ctx));
    assert(starting_pixels[60 * START_W + 60] == 0xffc7d4a5);

    enter_address("mailto:postmaster@localhost");
    assert(window.address_focus && strstr(window.status, "Unsupported address") && connections == 0);
    /* Scan for the core's actual link hit box and reproduce the host's leave
     * notification when a click takes capture. */
    int link_x = -1, link_y = -1;
    for (int y = TOOLBAR; y < TOOLBAR + 50 && link_x < 0; y += 2) {
        for (int x = 0; x < 120; x += 2) {
            pointer(&window, &(struct zenith_gui_event){.type = Z_GUI_MOVE, .x = x, .y = y});
            if (!strcmp(window.status, "https://fixture.invalid/")) {
                link_x = x; link_y = y; break;
            }
        }
    }
    assert(link_x >= 0);
    pointer(&window, &(struct zenith_gui_event){.type = Z_GUI_DOWN, .code = 1, .x = link_x, .y = link_y});
    pointer(&window, &(struct zenith_gui_event){.type = Z_GUI_MOVE, .x = -1, .y = -1});
    pointer(&window, &(struct zenith_gui_event){.type = Z_GUI_UP, .code = 1, .x = link_x, .y = link_y});
    await_document("Error occurred fetching page");
    assert(connections == 1);
    size_t size = 0;
    const uint8_t *data = content_get_source_data(browser_window_get_content(window.browser), &size);
    assert(data && size);
    char *html = strndup((const char *)data, size);
    assert(html && strstr(html, "fixture certificate was not trusted") && strstr(html, "Try Again"));
    free(html);
    enter_address(page);
    await_document("Local test");
    assert(!window.address_focus);
    browser_window_destroy(window.browser);
    netsurf_exit();
    nsoption_finalise(nsoptions, nsoptions_default);
    puts("PASS browser core paints author CSS, follows a link through pointer capture, shows the HTTPS failure reason and retry, and accepts another address");
    return 0;
}
