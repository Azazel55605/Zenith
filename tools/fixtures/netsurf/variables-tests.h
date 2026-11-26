/* Real selector cascade and box construction, including rebuilding both
 * inherited custom property values and pseudo styles at a media breakpoint. */
static void check_css_variables(void)
{
    int original_dpi = browser_get_dpi();
    bool sidebar = W.sidebar_hidden;
    assert(browser_set_dpi(96) == NSERROR_OK);
    W.sidebar_hidden = true;
    relayout_all();
    char path[] = "/tmp/nebula-variables-XXXXXX.html";
    int fd = mkstemps(path, 5);
    assert(fd >= 0);
    FILE *file = fdopen(fd, "w");
    assert(file);
    fputs("<!doctype html><title>Variable cascade</title><style>"
          "html,body{margin:0;padding:0}:root{--a:30px;--inherited:var(--a);--A:17px}"
          ".group{--a:49px;--later:var(--forward);--color:#123456;--forward:42px}"
          "#computed{width:var(--inherited);height:var(--a)}"
          "#case{width:var(--A);height:var(--missing,var(--another,21px))}"
          "#forward{width:var(--later);padding:var(--pad);--pad:3px 8px}"
          ".important{--importance:72px!important;width:var(--importance)}"
          "#priority{--importance:120px}#inline{width:51px!important}"
          "#bg{--a:var(--b);--b:var(--a);--self:var(--ok,var(--self));--ok:3px;"
          "width:var(--a,19px);height:var(--self,22px)}"
          "#invalid{padding-left:74px;padding-left:var(--missing)}"
          "#order{--v:10px;padding:var(--v);padding-left:62px;width:39px;width:var(--v)}"
          "#token{width:var(--n)px;--n:12;padding-left:17px;padding-left:var(--n)px}"
          "#empty{--empty: ;width:var(--empty,50px) 29px;height:var(--unknown,) 31px}"
          "#fallback-origin{padding:44px;padding:var(--unknown,revert)}"
          "#inherited-origin{padding:41px;padding:var(--outer-origin)}"
          "#scc{--x:var(--a,var(--b));--a:var(--x);--b:var(--a,3px);width:var(--b,47px)}"
          "#math{width:calc(50px + var(--a))}"
          "@supports (--q: green) and (width:var(--q)){#feature{width:97px}}"
          "#pseudo:before{--A:13px;content:'p';display:block;width:var(--A)}"
          "#initial{--a:initial;width:var(--a,37px)}#inherit{--a:inherit;width:var(--a)}"
          "@media(max-width:579px){:root{--inherited:36px}.group{--a:57px}}"
          "</style><div class=group><div id=computed></div><div id=case></div><div id=forward></div>"
          "<div id=priority class=important style='--importance:85px'></div>"
          "<div id=inline style='--local:61px;width:var(--local)!important'></div>"
          "<div id=bg></div><div id=invalid></div><div id=order></div><div id=token></div>"
          "<div id=fallback-origin></div><div style='--outer-origin:var(--unknown,revert)'><div id=inherited-origin></div></div>"
          "<div id=empty></div><div id=scc></div><div id=feature></div><div id=math></div>"
          "<div id=initial></div><div id=inherit></div><div id=pseudo></div></div>", file);
    assert(fclose(file) == 0);
    enter_address(path);
    await_document("Variable cascade");
    assert(unlink(path) == 0);
    struct hlcache_handle *page = browser_window_get_content(window.browser);
    for (int i = 0; i < 3; i++) {
        if (i) toggle_sidebar();
        bool wide = W.sidebar_hidden;
        long deadline = zui_motion_clock() + 2000;
        struct box *b;
        do {
            run_alarms();
            b = live_media_box(page, "computed");
            if (b && b->height == (wide ? 49 : 57)) break;
            usleep(1000);
        } while (zui_motion_clock() < deadline);
        assert(b && b->width == (wide ? 30 : 36) && b->height == (wide ? 49 : 57));
        assert(live_media_box(page, "case")->width == 17 && live_media_box(page, "case")->height == 21);
        b = live_media_box(page, "forward");
        assert(b->width == 42 && b->padding[LEFT] == 8 && b->padding[TOP] == 3);
        assert(live_media_box(page, "priority")->width == 72);
        assert(live_media_box(page, "inline")->width == 61);
        b = live_media_box(page, "bg");
        assert(b->width == 19 && b->height == 22);
        assert(live_media_box(page, "invalid")->padding[LEFT] == 0);
        b = live_media_box(page, "order");
        assert(b->width == 10 && b->padding[LEFT] == 62 && b->padding[TOP] == 10);
        assert(live_media_box(page, "initial")->width == 37);
        assert(live_media_box(page, "inherit")->width == (wide ? 49 : 57));
        assert(live_media_box(page, "token")->padding[LEFT] == 0);
        assert(live_media_box(page, "fallback-origin")->padding[TOP] == 0);
        assert(live_media_box(page, "inherited-origin")->padding[LEFT] == 0);
        b = live_media_box(page, "empty");
        assert(b->width == 29 && b->height == 31);
        assert(live_media_box(page, "scc")->width == 47);
        assert(live_media_box(page, "feature")->width == 97);
        assert(live_media_box(page, "math")->width == (wide ? 99 : 107));
        b = live_media_box(page, "pseudo");
        assert(b->children && b->children->width == 13);
    }
    W.sidebar_hidden = sidebar;
    assert(browser_set_dpi(original_dpi) == NSERROR_OK);
    relayout_all();
    puts("PASS inherited and scoped CSS variables, inline importance, var shorthands, cycles, fallback, pseudo-elements and live media");
}
