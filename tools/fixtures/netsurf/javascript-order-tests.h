/* Real HTML parser, fetch queue, script engine and rendered page. */
static const char *order_response(const char *path, const char **mime)
{
    if (!strcmp(path, "/order/index.html")) {
        *mime = "text/html";
        return "<!doctype html><title>Classic script order</title>"
               "<p id=sequence>Waiting</p><p id=async>Waiting</p><p id=bad>Clean</p>"
               "<script>var seq='',asyncRuns=0;function mark(c){seq+=c;}"
               "document.addEventListener('DOMContentLoaded',function(){"
               "mark(document.readyState==='interactive'?'R':'!');"
               "document.getElementById('sequence').textContent=seq;});mark('I');</script>"
               "<script src='block.js'></script>"
               "<script>mark('P');</script>"
               "<script defer src='first.js'></script>"
               "<script defer src='json-response.js'></script>"
               "<script defer src='second.js'></script>"
               "<script async defer src='async.js'></script>"
               "<script type='module' src='should-not-load.js'></script>"
               "<script type='application/ld+json'>document.getElementById('bad').textContent='Wrong'</script>"
               "<p id=tail>Tail</p>"
               "<script type='  TEXT/JAVASCRIPT '>mark('T');</script>";
    }
    *mime = "application/javascript";
    if (!strcmp(path, "/order/block.js"))
        return "mark(document.readyState==='loading' && !document.getElementById('tail')?'B':'!');";
    if (!strcmp(path, "/order/first.js"))
        return "mark(document.readyState==='interactive' && document.getElementById('tail')?'1':'!');";
    if (!strcmp(path, "/order/second.js")) return "mark('2');";
    if (!strcmp(path, "/order/async.js"))
        return "document.getElementById('async').textContent='Async runs '+(++asyncRuns);";
    if (!strcmp(path, "/order/json-response.js")) {
        *mime = "application/json";
        return "document.getElementById('bad').textContent='Wrong';";
    }
    if (!strcmp(path, "/order/should-not-load.js")) abort();
    return NULL;
}

static void check_script_order(void)
{
    const char *address = "https://scripts.test/order/index.html";
    nsurl *url = NULL;
    nsoption_set_bool(enable_javascript, true);
    assert(new_tab("about:blank", true, NULL));
    await_document("about:blank");
    assert(nsurl_create(address, &url) == NSERROR_OK);
    assert(allow_page_scripts(url));
    nsurl_unref(url);
    enter_address(address);
    await_document("Classic script order");
    script_expect_text("IBPT12R", true);
    /* Async download and its queued repaint may finish after the first frame. */
    long deadline = zui_motion_clock() + 5000;
    open_find(&window);
    snprintf(window.find_text, sizeof(window.find_text), "Async runs 1");
    window.find.length = strlen(window.find_text);
    do {
        run_alarms();
        find(&window, false);
        if (!window.find_missing) break;
        usleep(1000);
    } while (zui_motion_clock() < deadline);
    assert(!window.find_missing);
    close_find(&window);
    script_expect_text("Clean", true);
    script_expect_text("Wrong", false);
    close_tab(W.active);
    run_alarms();
    puts("PASS classic parser-blocking script, ordered deferred scripts before DOMContentLoaded, async once, MIME and module filtering");
}
