/* Exercise the native button path and assert on the painted and searchable
 * document. Looking at the script source or its DOM wrapper is insufficient. */
static void script_click(const char *id)
{
    struct box *control = live_media_box(browser_window_get_content(window.browser), id);
    int x, y;
    assert(control && control->width > 0 && control->height > 0);
    box_coords(control, &x, &y);
    struct zui_rect pr = page_rect();
    x += pr.x + control->width / 2 - window.sx;
    y += pr.y + control->height / 2 - window.sy;
    pointer(&window, &(struct zenith_gui_event){.type=Z_GUI_MOVE, .x=x, .y=y});
    pointer(&window, &(struct zenith_gui_event){.type=Z_GUI_DOWN, .code=1, .x=x, .y=y});
    pointer(&window, &(struct zenith_gui_event){.type=Z_GUI_UP, .code=1, .x=x, .y=y});
}

static void script_expect_panel(const char *id, int width, uint32_t color)
{
    long deadline = zui_motion_clock() + 5000;
    do {
        run_alarms();
        struct box *b = live_media_box(browser_window_get_content(window.browser), id);
        if (b && b->width == width && render_page(&window, page_rect())) {
            int x, y;
            box_coords(b, &x, &y);
            x += b->width - 2 - window.sx;
            y += b->height - 2 - window.sy;
            assert(x >= 0 && x < W.page_w && y >= 0 && y < W.page_h);
            if (W.page[y*W.page_w+x] == color) return;
        }
        usleep(1000);
    } while (zui_motion_clock() < deadline);
    fprintf(stderr, "Scripted panel %s did not paint %dpx wide in %08x\n", id, width, color);
    abort();
}

static void script_expect_text(const char *text, bool visible)
{
    open_find(&window);
    snprintf(window.find_text, sizeof(window.find_text), "%s", text);
    window.find.length = strlen(window.find_text);
    find(&window, false);
    if (window.find_missing == visible) fprintf(stderr, "Page search '%s' expected %s\n", text, visible ? "visible" : "absent");
    assert(window.find_missing == !visible);
    close_find(&window);
}

static void check_local_javascript(void)
{
    char path[] = "/tmp/nebula-js-XXXXXX.html";
    int fd = mkstemps(path, 5);
    FILE *file = fd < 0 ? NULL : fdopen(fd, "w");
    assert(file);
    fputs("<!doctype html><meta charset=utf-8><title>Script interaction</title>"
          "<style>body{margin:0}button,a{display:block;width:130px;height:28px}"
          "#panel{width:140px;height:32px;background:#d88224}"
          "#panel.changed{width:210px;background:#37865c}</style>"
          "<noscript><p id=fallback>Script fallback</p></noscript>"
          "<button type=button id=change>Change text</button>"
          "<button type=button id=loop>Busy handler</button>"
          "<a id=stay href='about:blank'>Stay here</a>"
          "<div id=panel>Before any click</div><script>"
          "var count=0, panel=document.getElementById('panel');"
          "document.getElementById('change').addEventListener('click',function(){"
          "panel.textContent='Saved click '+(++count); panel.classList.toggle('changed');});"
          "document.getElementById('loop').addEventListener('click',function(){"
          "panel.textContent='Busy handler entered';while(true){} panel.textContent='Loop escaped';});"
          "document.getElementById('stay').addEventListener('click',function(e){e.preventDefault();"
          "panel.textContent='Stayed here';});</script>", file);
    assert(fclose(file) == 0);
    int dpi = browser_get_dpi();
    assert(browser_set_dpi(96) == NSERROR_OK);
    nsoption_set_bool(enable_javascript, true);
    enter_address(path);
    await_document("Script interaction");
    script_expect_panel("panel", 140, 0xffd88224);
    assert(live_media_box(browser_window_get_content(window.browser), "fallback"));
    script_click("change");
    run_alarms();
    script_expect_text("Before any click", true);
    assert(new_tab("about:blank", true, NULL));
    await_document("about:blank");
    nsurl *url = NULL;
    assert(netsurf_path_to_nsurl(path, &url) == NSERROR_OK);
    assert(allow_page_scripts(url));
    nsurl_unref(url);
    enter_address(path);
    await_document("Script interaction");
    struct hlcache_handle *page = browser_window_get_content(window.browser);
    assert(!live_media_box(page, "fallback"));
    script_click("change");
    script_expect_panel("panel", 210, 0xff37865c);
    script_expect_text("Saved click 1", true);
    script_expect_text("Before any click", false);
    long before = zui_motion_clock();
    script_click("loop");
    assert(zui_motion_clock() - before < 5000);
    run_alarms();
    script_expect_text("Busy handler entered", true);
    script_expect_text("Loop escaped", false);
    script_click("change");
    script_expect_panel("panel", 140, 0xffd88224);
    script_expect_text("Saved click 2", true);
    script_click("stay");
    run_alarms();
    assert(browser_window_get_content(window.browser) == page);
    script_expect_text("Stayed here", true);
    enter_address("about:blank");
    await_document("about:blank");
    enter_address(path);
    await_document("Script interaction");
    script_click("change");
    script_expect_panel("panel", 210, 0xff37865c);
    script_expect_text("Saved click 1", true); /* Fresh document/global. */
    script_click("change"); /* Close before the queued redraw can run. */
    close_tab(W.active);
    run_alarms();
    script_expect_text("Before any click", true);
    assert(live_media_box(browser_window_get_content(window.browser), "fallback"));
    assert(unlink(path) == 0);
    nsoption_set_bool(enable_javascript, false);
    assert(browser_set_dpi(dpi) == NSERROR_OK);
    puts("PASS opt-in local JavaScript updates rendered text/class/layout on click, cancels link navigation, bounds a busy handler and survives navigation and tab close");
}
