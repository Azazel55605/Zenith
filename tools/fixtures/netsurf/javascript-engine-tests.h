static unsigned long_script_alarms(void)
{
    unsigned count = 0;
    long threshold = zui_motion_clock() + 110000;
    for (struct alarm *a = alarms; a; a = a->next)
        if (a->due > threshold && a->due <= threshold + 10000) count++;
    return count;
}

static void check_script_engine(void)
{
    char source[] = "/tmp/nebula-engine-XXXXXX.html";
    char target[] = "/tmp/nebula-target-XXXXXX.html";
    FILE *first = fdopen(mkstemps(source, 5), "w");
    FILE *second = fdopen(mkstemps(target, 5), "w");
    assert(first && second);
    fputs("<!doctype html><meta charset=utf-8><title>Engine target</title><p>Arrived</p>", second);
    assert(fclose(second) == 0);
    fputs("<!doctype html><meta charset=utf-8><title>Engine fixture</title>"
          "<button id=go>Next</button><p id='café🌙'>Déjà 🌙</p><p id=report>Waiting</p>"
          "<script>var r=document.getElementById('report'),p=document.getElementById('caf\\u00e9\\ud83c\\udf19');"
          "if(!p||p.textContent!=='D\\u00e9j\\u00e0 \\ud83c\\udf19'||p.textContent.length!==7) throw Error('DOM value');"
          "p.textContent='Cr\\u00e8me \\ud83c\\udf19';"
          "p.setAttribute('title','go \\ud83c\\udf19');p.classList.add('caf\\u00e9','moon\\ud83c\\udf19');"
          "if(p.textContent!=='Crème 🌙'||p.getAttribute('title')!=='go 🌙'||"
          "p.classList.item(1)!=='moon🌙')throw Error('DOM write');"
          "if(globalThis!==window||typeof Duktape!=='undefined')throw Error('realm');"
          "if(window.name!=='')throw Error('old window name');window.name='realm name';"
          "document.cookie='fixture=value';if(window.name!=='realm name'||document.cookie!==''||"
          "navigator.cookieEnabled||window.parent||window.opener||window.history||"
          "window.localStorage||window.sessionStorage)throw Error('private state');"
          "var rejected=0;try{window.location.assign('about:blank')}catch(e){rejected++}"
          "try{window.location.replace('resource:credits.html')}catch(e){rejected++}"
          "try{window.location.href='data:text/plain,fixture'}catch(e){rejected++}"
          "if(rejected!==3)throw Error('scheme');"
          "try{new Uint8Array(5*1024*1024)}catch(e){r.textContent='Limits OK'}"
          "if(new Uint8Array(16).length!==16) throw Error('small buffer');"
          "setTimeout(function(){r.textContent='Timer fired'},120000);"
          "document.getElementById('go').onclick=function(){window.location.assign('", first);
    fputs(strrchr(target, '/') + 1, first);
    fputs("')};</script>", first);
    assert(fclose(first) == 0);

    nsurl *url = NULL;
    assert(netsurf_path_to_nsurl(source, &url) == NSERROR_OK);
    nsoption_set_bool(enable_javascript, true);
    assert(new_tab("about:blank", true, NULL));
    await_document("about:blank");
    assert(allow_page_scripts(url));
    nsurl_unref(url);
    unsigned initial = long_script_alarms();
    enter_address(source);
    await_document("Engine fixture");
    script_expect_text("Limits OK", true);
    assert(long_script_alarms() == initial + 1);
    script_click("go");
    await_document("Engine target");
    assert(long_script_alarms() == initial);
    enter_address(source);
    await_document("Engine fixture");
    script_expect_text("Limits OK", true); /* The preceding document name is gone. */
    assert(long_script_alarms() == initial + 1);
    close_tab(W.active);
    run_alarms();
    assert(long_script_alarms() == initial);
    nsoption_set_bool(enable_javascript, false);
    assert(unlink(source) == 0 && unlink(target) == 0);
    puts("PASS UTF-16/UTF-8 DOM and attribute round trips, engine memory limit, script navigation and timer teardown");
}
