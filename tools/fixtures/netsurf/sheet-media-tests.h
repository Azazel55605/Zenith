#include "netsurf/browser.h"

/* The CSS inside these sheets is unconditional. Only the HTML media attribute
 * can make the pixels change when the viewport crosses the breakpoint. */
static void check_stylesheet_media(void)
{
    bool previous_sidebar = W.sidebar_hidden;
    W.sidebar_hidden = false;
    relayout_all();
    char path[] = "/tmp/nebula-sheet-media-XXXXXX.html";
    int fd = mkstemps(path, 5);
    assert(fd >= 0);
    FILE *file = fdopen(fd, "w");
    assert(file);
    fputs("<!doctype html><title>Conditional sheets</title>"
          "<style>body{margin:0}div{width:100px;height:24px;background:#998877}</style>"
          "<style media='(max-width:619px)'>#embedded{background:#21587c}</style>"
          "<link rel=stylesheet media='(min-width:620px)'"
          " href='data:text/css,%23linked%7Bbackground:%237b4930%7D'>"
          "<style media='print'>div{background:#ffee00}</style>"
          "<style media='small'>div{background:#ff00ee}</style>"
          "<style media=' \t '>#empty{background:#435627}</style>"
          "<div id=embedded></div><div id=linked></div><div id=empty></div>", file);
    assert(fclose(file) == 0);
    enter_address(path);
    await_document("Conditional sheets");
    assert(unlink(path) == 0);
    struct hlcache_handle *document = browser_window_get_content(window.browser);
    for (int step = 0; step < 3; ++step) {
        if (step) toggle_sidebar();
        bool wide = W.sidebar_hidden;
        struct zui_rect page = page_rect();
        long deadline = zui_motion_clock() + 2000;
        uint32_t expected = wide ? 0xff7b4930 : 0xff998877;
        do {
            run_alarms();
            assert(render_page(&window, page));
            if (W.page[32 * page.w + 10] == expected) break;
            usleep(1000);
        } while (zui_motion_clock() < deadline);
        assert(W.page[10 * page.w + 10] == (wide ? 0xff998877 : 0xff21587c));
        assert(W.page[32 * page.w + 10] == expected);
        assert(W.page[54 * page.w + 10] == 0xff435627);
        assert(browser_window_get_content(window.browser) == document);
    }
    W.sidebar_hidden = previous_sidebar;
    relayout_all();
    puts("PASS HTML style/link media changes on the same loaded page, print, unknown and empty media");
}

/* Find is an overlay. Zoom changes the document's CSS viewport even while
 * that overlay has keyboard focus; closing it must retain the chosen zoom. */
static void check_zoom_with_find(void)
{
    int dpi = browser_get_dpi();
    assert(browser_set_dpi(96) == NSERROR_OK);
    bool sidebar = W.sidebar_hidden;
    W.sidebar_hidden = false;
    relayout_all();
    char path[] = "/tmp/nebula-zoom-find-XXXXXX.html";
    int fd = mkstemps(path, 5);
    assert(fd >= 0);
    FILE *file = fdopen(fd, "w");
    assert(file);
    fputs("<!doctype html><title>Find and zoom</title><style>body{margin:0}html{font-size:20px}"
          "#s{width:2rem;height:28px;background:#317140}"
          "@media(min-width:540px) and (min-height:400px){html{font-size:30px}#s{background:#5b3287}}"
          "</style><div id=s></div><p>needle</p>", file);
    assert(fclose(file) == 0);
    enter_address(path);
    await_document("Find and zoom");
    assert(unlink(path) == 0);
    struct hlcache_handle *document = browser_window_get_content(window.browser);
    struct zui_rect page = page_rect();
    assert(page.w == 548 && page.h == 404);
    const struct {
        int code, extra;
        bool find_open;
        int width;
        uint32_t rgb;
    } steps[] = {
        {'f', 0, true, 60, 0xff5b3287},
        {'+', 0, true, 44, 0xff317140},
        {0, 1, false, 44, 0xff317140},
        {'0', 0, false, 60, 0xff5b3287}
    };
    for (size_t i = 0; i < sizeof(steps)/sizeof(steps[0]); i++) {
        key(&window, &(struct zenith_gui_event){.type=Z_GUI_KEY, .code=steps[i].code,
                .extra=steps[i].extra, .modifiers=steps[i].code ? 2 : 0});
        assert(window.find_open == steps[i].find_open);
        int width = 0;
        long deadline = zui_motion_clock() + 2000;
        do {
            run_alarms();
            assert(render_page(&window, page));
            width = 0;
            while (width < page.w && W.page[8 * page.w + width] == steps[i].rgb)
                width++;
            if (width == steps[i].width) break;
            usleep(1000);
        } while (zui_motion_clock() < deadline);
        if (width != steps[i].width) {
            fprintf(stderr, "Ctrl+F/zoom step %zu: expected %d px, painted %d\n", i, steps[i].width, width);
            abort();
        }
        assert(browser_window_get_content(window.browser) == document);
    }
    W.sidebar_hidden = sidebar;
    assert(browser_set_dpi(dpi) == NSERROR_OK);
    relayout_all();
    puts("PASS Find overlay preserves viewport, keyboard zoom updates live width/height media and rem layout, reset restores geometry");
}
