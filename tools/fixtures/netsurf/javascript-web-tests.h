/* Responses go through the production GET parser and the real HTML engine. */
static const char *web_page_fixture(const char *path, const char **mime,
                                    const char **headers, int *status)
{
    static const char ordinary[] =
        "<!doctype html><title>Web script fixture</title><p id=s>Waiting for scripts</p>"
        "<script>var panel=document.getElementById('s');var chain='inline';</script>"
        "<script src='https://assets.test/cdn.js'></script>"
        "<script>panel.textContent=chain+' finished';</script>";
    if (!strcmp(path, "/cdn.js")) {
        *mime = "text/javascript";
        return "chain+=' external';";
    }
    if (!strcmp(path, "/redirect")) {
        *status = 302;
        *headers = "Location: https://other.test/start\r\n";
        return "";
    }
    if (!strcmp(path, "/policy"))
        *headers = "Content-Security-Policy: default-src 'self'\r\n";
    else if (!strcmp(path, "/report-only"))
        *headers = "Content-Security-Policy-Report-Only: script-src 'none'\r\n";
    else if (!strcmp(path, "/meta"))
        return "<!doctype html><title>Web script fixture</title>"
               "<meta http-equiv=Content-Security-Policy content=\"script-src 'none'\">"
               "<p id=s>Waiting for scripts</p><script>document.getElementById('s').textContent='ran';</script>";
    else if (!strcmp(path, "/resources"))
        return "<!doctype html><title>Web script fixture</title><p id=s>Waiting for scripts</p>"
               "<script>var chain='web';</script>"
               "<script src='http://assets.test/cdn.js'></script>"
               "<script integrity='sha256-unverified' src='https://assets.test/integrity.js'></script>"
               "<script crossorigin src='https://assets.test/cors.js'></script>"
               "<script src='file:///missing-local-script.js'></script>"
               "<script src='https://fixture.invalid/untrusted.js'></script>"
               "<script>try{location.assign('file:///usr/share/zenith/netsurf/guide.html');}"
               "catch(e){if(e.name==='TypeError')chain+=' restricted';}document.getElementById('s').textContent=chain+' finished';</script>";
    else if (!strcmp(path, "/integrity.js") || !strcmp(path, "/cors.js"))
        abort(); /* Both attributes require checks the transport cannot yet perform. */
    else if (strcmp(path, "/start") && strcmp(path, "/next")) {
        *status = 404;
        return "";
    }
    return ordinary;
}

static void expect_scope(const char *address, bool allowed)
{
    nsurl *url = NULL;
    assert(nsurl_create(address, &url) == NSERROR_OK);
    assert(tab_allows_scripts(&window, url) == allowed);
    nsurl_unref(url);
}

static void visit_script_page(const char *url, const char *text)
{
    enter_address(url);
    await_document("Web script fixture");
    script_expect_text(text, true);
}

static void check_web_javascript(void)
{
    nsoption_set_bool(enable_javascript, true);
    unsigned scripts_before = web_script_requests;
    visit_script_page("https://scripts.test/start", "Waiting for scripts");
    assert(web_script_requests == scripts_before);
    ui_action(&window, MENU_SCRIPTS);
    await_document("Web script fixture");
    script_expect_text("inline external finished", true);
    assert(web_script_requests > scripts_before);
    expect_scope("https://scripts.test:0443/another?query#fragment", true);
    expect_scope("https://scripts.test:8443/start", false);
    expect_scope("http://scripts.test/start", false);
    expect_scope("https://other.test/start", false);
    expect_scope("https://user@scripts.test/start", false);
    expect_scope("nebula:start", false);
    visit_script_page("https://scripts.test/next", "inline external finished");
    visit_script_page("https://scripts.test/policy", "Waiting for scripts");
    assert(html_page_restricts_scripts(browser_window_get_content(window.browser)));
    visit_script_page("https://scripts.test/meta", "Waiting for scripts");
    assert(html_page_restricts_scripts(browser_window_get_content(window.browser)));
    visit_script_page("https://scripts.test/report-only", "inline external finished");
    unsigned before_insecure = insecure_script_requests, refused = connections;
    visit_script_page("https://scripts.test/resources", "web restricted finished");
    assert(connections > refused && insecure_script_requests == before_insecure);
    visit_script_page("https://scripts.test/redirect", "Waiting for scripts");
    assert(strstr(nsurl_access(hlcache_handle_get_url(browser_window_get_content(window.browser))), "other.test"));
    visit_script_page("https://scripts.test/start", "inline external finished");
    assert(new_tab("https://scripts.test/start", true, NULL));
    await_document("Web script fixture");
    script_expect_text("Waiting for scripts", true);
    close_tab(W.active);
    script_expect_text("inline external finished", true);
    ui_action(&window, MENU_SCRIPTS);
    await_document("Web script fixture");
    script_expect_text("Waiting for scripts", true);
    visit_script_page("http://scripts.test/start", "Waiting for scripts");
    ui_action(&window, MENU_SCRIPTS);
    await_document("Web script fixture");
    script_expect_text("inline external finished", true);
    expect_scope("http://scripts.test:0080/other", true);
    expect_scope("https://scripts.test/", false);
    puts("PASS HTTP and HTTPS origin opt-in, ordered classic scripts, policy, mixed content, TLS rejection, redirect and tab isolation");
}
