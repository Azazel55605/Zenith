#include "netsurf/browser.h"
#include <dom/dom.h>
#include "content/handlers/html/html_save.h"
#include "content/handlers/html/box.h"
#include "content/handlers/html/box_inspect.h"
#include "content/handlers/html/form_internal.h"
#include "desktop/textarea.h"
#include "desktop/scrollbar.h"

static struct box *live_media_box(struct hlcache_handle *page, const char *id)
{
    lwc_string *name;
    assert(lwc_intern_string(id, strlen(id), &name) == lwc_error_ok);
    struct box *result = box_find_by_id(html_get_box_tree(page), name);
    lwc_string_unref(name);
    return result;
}

/* The source file goes away after navigation. Every transition must use the
 * live document, including a hidden subtree containing a user-edited field. */
static void check_live_media(void)
{
    int dpi = browser_get_dpi();
    bool sidebar = W.sidebar_hidden;
    assert(browser_set_dpi(96) == NSERROR_OK);
    W.sidebar_hidden = true;
    relayout_all();
    char path[] = "/tmp/nebula-live-media-XXXXXX.html";
    int fd = mkstemps(path, 5);
    assert(fd >= 0);
    FILE *file = fdopen(fd, "w");
    assert(file);
    fputs("<!doctype html><title>Live media</title><style>"
          "html,body{margin:0;padding:0}html{font-size:16px}#wide{display:none}"
          "@media(min-width:580px){#wide{display:flex}html{font-size:22px}}"
          "#font{font-size:2rem;width:1em}#sizing{width:50vw;height:25vh;margin-left:2rem}#scroll{height:30px;overflow:auto}#frame{display:none}"
          "#portrait,#ratio,#height,#range{display:none}"
          "@media(max-width:579px){#frame{display:block}}"
          "@media(orientation:PORTRAIT){#portrait{display:block}}"
          "@media(min-aspect-ratio:3/2){#ratio{display:block}}"
          "@media(1/2 < aspect-ratio < 1/1){#range{display:block}}"
          "@media(height >= 320px){#height{display:block}}"
          "</style><div id=wide><textarea id=editor>initial</textarea><img id=hiddenlogo src='data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw=='></div>"
          "<div id=scroll><div style='height:200px'></div></div>"
          "<iframe id=frame src='data:text/html,%3Ctitle%3EChild%3C/title%3E'></iframe><div id=font></div><div id=sizing></div><div id=portrait></div><div id=ratio></div><div id=range></div>"
          "<div id=height></div><img id=logo src='data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw=='>", file);
    assert(fclose(file) == 0);
    enter_address(path);
    await_document("Live media");
    assert(unlink(path) == 0);
    struct hlcache_handle *page = browser_window_get_content(window.browser);
    dom_document *dom = html_get_document(page);
    struct box *field = live_media_box(page, "editor");
    struct box *logo = live_media_box(page, "logo");
    assert(field && field->gadget && logo && logo->object);
    struct textarea *editor = field->gadget->data.text.ta;
    struct hlcache_handle *image = logo->object;
    struct hlcache_handle *hidden_image = live_media_box(page, "hiddenlogo")->object;
    struct browser_window *child = NULL;
    assert(hidden_image);
    assert(render_page(&window, page_rect()));
    struct box *scroll_box = live_media_box(page, "scroll");
    assert(scroll_box->scroll_y);
    scrollbar_set(scroll_box->scroll_y, 17, false);
    assert(textarea_set_text(editor, "draft"));
    assert(textarea_set_caret(editor, 2));
    for (unsigned i = 0; i < 6; ++i) {
        toggle_sidebar();
        bool wide = W.sidebar_hidden;
        struct zui_rect rect = page_rect();
        assert((rect.w >= 580) == wide);
        long deadline = zui_motion_clock() + 2000;
        struct box *area;
        do {
            run_alarms();
            area = live_media_box(page, "wide");
            if (!!area == wide) break;
            usleep(1000);
        } while (zui_motion_clock() < deadline);
        assert(!!area == wide);
        assert(browser_window_get_content(window.browser) == page && html_get_document(page) == dom);
        assert(live_media_box(page, "logo")->object == image);
        field = live_media_box(page, "editor");
        if (wide) {
            assert(live_media_box(page, "hiddenlogo")->object == hidden_image);
            assert(area->type == BOX_FLEX && field && field->gadget->data.text.ta == editor);
            assert(!strcmp(textarea_data(editor, NULL), "draft"));
        } else {
            assert(field == NULL);
            struct box *frame = live_media_box(page, "frame");
            assert(frame && frame->iframe);
            if (child == NULL) child = frame->iframe;
            else assert(frame->iframe == child);
        }
        assert(render_page(&window, rect));
        assert(scrollbar_get_offset(live_media_box(page, "scroll")->scroll_y) == 17);
    }
    assert(textarea_drop_text(editor, "X", 1));
    assert(!strcmp(textarea_data(editor, NULL), "drXaft"));
    const int original_w = W.ui.width, original_h = W.ui.height;
    const int viewports[][2] = {{400, 300}, {300, 320}, {320, 320}, {480, 320},
                                {479, 320}, {520, 370}, {500, 350}, {600, 400}};
    for (size_t i = 0; i < sizeof(viewports)/sizeof(viewports[0]); ++i) {
        int w = viewports[i][0], h = viewports[i][1];
        W.ui.width = w + 24;
        W.ui.height = h + TOOLBAR + 24;
        relayout_all();
        long deadline = zui_motion_clock() + 2000;
        struct box *size;
        do {
            run_alarms();
            size = live_media_box(page, "sizing");
            if (size->width == (w + 1) / 2 && size->height == h / 4) break;
            usleep(1000);
        } while (zui_motion_clock() < deadline);
        assert(size->width == (w + 1) / 2 && size->height == h / 4);
        assert(size->margin[LEFT] == (w >= 580 ? 44 : 32));
        assert(live_media_box(page, "font")->width == (w >= 580 ? 44 : 32));
        assert(!!live_media_box(page, "portrait") == (w <= h));
        assert(!!live_media_box(page, "ratio") == (2*w >= 3*h));
        assert(!!live_media_box(page, "range") == (w < h && 2*w > h));
        assert(!!live_media_box(page, "height") == (h >= 320));
    }
    struct gui_window *original_tab = &window;
    assert(new_tab("about:blank", true, NULL));
    await_document("about:blank");
    toggle_sidebar();
    long deadline = zui_motion_clock() + 2000;
    while (live_media_box(page, "wide") && zui_motion_clock() < deadline) {
        run_alarms();
        usleep(1000);
    }
    assert(!live_media_box(page, "wide") && html_get_document(page) == dom);
    close_tab(W.active);
    assert(&window == original_tab && browser_window_get_content(window.browser) == page);
    W.ui.width = original_w;
    W.ui.height = original_h;
    W.sidebar_hidden = sidebar;
    assert(browser_set_dpi(dpi) == NSERROR_OK);
    relayout_all();
    puts("PASS repeated live media changes preserve DOM, form edits, caret and decoded image; viewport lengths, orientation and ranges");
}
