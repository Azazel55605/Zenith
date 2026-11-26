static bool count_navigation(const struct browser_window *browser, int x0, int y0,
        int x1, int y1, const struct history_entry *entry, void *data)
{
    (void)browser; (void)x0; (void)y0; (void)x1; (void)y1; (void)entry;
    ++*(unsigned *)data;
    return true;
}

static void check_find_across_reflow(void)
{
    bool hidden = W.sidebar_hidden;
    int dpi = browser_get_dpi();
    assert(browser_set_dpi(96) == NSERROR_OK);
    W.sidebar_hidden = false;
    relayout_all();
    char path[] = "/tmp/nebula-find-XXXXXX.html";
    int fd = mkstemps(path, 5);
    assert(fd >= 0);
    FILE *file = fdopen(fd, "w");
    assert(file);
    fputs("<!doctype html><title>Find on resize</title><style>"
          "body{height:1800px}.extra{display:none}@media(min-width:580px){.extra{display:block}}"
          "</style><p>Always visible</p><p class=extra>Wide only needle</p>", file);
    assert(fclose(file) == 0);
    enter_address(path);
    await_document("Find on resize");
    assert(unlink(path) == 0);
    unsigned before = 0, after = 0;
    browser_window_history_enumerate(window.browser, count_navigation, &before);
    open_find(&window);
    strcpy(window.find_text, "needle");
    window.find.length = strlen(window.find_text);
    find(&window, false);
    assert(window.find_missing);
    scroll_to(&window, 0, 370);
    for (int i = 0; i < 4; i++) {
        toggle_sidebar();
        run_alarms();
        assert(render_page(&window, page_rect()));
        assert(window.find_missing == !W.sidebar_hidden);
        assert(window.sy == 370);
    }
    toggle_sidebar();
    run_alarms();
    assert(render_page(&window, page_rect()));
    find(&window, false); /* Uses match boxes from the current layout. */
    assert(!window.find_missing && window.sy < 370);
    browser_window_history_enumerate(window.browser, count_navigation, &after);
    assert(before && before == after);
    close_find(&window);
    W.sidebar_hidden = hidden;
    assert(browser_set_dpi(dpi) == NSERROR_OK);
    relayout_all();
    puts("PASS live Find highlights follow visible text, preserving scroll and navigation history on resize");
}
