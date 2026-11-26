/* Evaluate math at layout time using the inner containing block, live viewport
 * and root font size. The local document is unlinked before resizing. */
static void check_responsive_math(void)
{
    const bool sidebar = W.sidebar_hidden;
    int dpi = browser_get_dpi();
    assert(browser_set_dpi(96) == NSERROR_OK);
    W.sidebar_hidden = false;
    relayout_all();
    char path[] = "/tmp/nebula-math-XXXXXX.html";
    int fd = mkstemps(path, 5);
    assert(fd >= 0);
    FILE *f = fdopen(fd, "w");
    assert(f);
    fputs("<!doctype html><title>Responsive math</title>"
          "<style>html{font-size:16px}body{margin:0}section{width:50%}section div{height:4px}"
          ".a{width:calc(100% - 2rem);background:#7a5391}"
          ".b{width:min(80%,max(1px,calc(25vw + 10px)));background:#187651}"
          ".c{width:clamp(100px,(75% - 10px),215px);background:#d43c50}"
          ".d{width:43px;width:calc(1px + 1);width:min(50%,6deg);width:calc(100% -6px);"
          "width:clamp(1px,2px);background:#70bc93}"
          ".e{width:1px;min-width:max(10px,50% - 4px);background:#60359a}"
          ".f{width:200px;max-width:calc(50% - 4px)!important;max-width:190px;background:#ec2571}"
          ".g{width:calc((2 * 20px + 7px) / 2);background:#ba5137}"
          ".h{width:calc(80% - 1rem);width:33px;background:#13ab84}"
          "table{width:calc(100% - 40px);border-spacing:0}td{padding:0;height:4px;background:#947cad}"
          ".i{width:calc(12pt + 1px);background:#b4597e}"
          "</style><section><div class=a></div><div class=b></div><div class=c></div>"
          "<div class=d></div><div class=e></div><div class=f></div><div class=g></div><div class=h></div><table><tr><td></td></tr></table><div class=i></div></section>",f);
    assert(fclose(f) == 0);
    enter_address(path);
    await_document("Responsive math");
    assert(unlink(path) == 0);
    struct hlcache_handle *page = browser_window_get_content(window.browser);
    for (int i = 0; i < 3; i++) {
        W.sidebar_hidden = i == 1;
        relayout_all();
        struct zui_rect view = page_rect();
        run_alarms();
        assert(browser_window_get_content(window.browser) == page && render_page(&window,view));
        int inner = view.w / 2;
        expect_layout_box(0xff7a5391,0,0,inner-32,4);
        expect_layout_box(0xff187651,0,4,(view.w+2)/4+10,4);
        int limited = (inner*3+2)/4-10;
        expect_layout_box(0xffd43c50,0,8,limited < 215 ? limited : 215,4);
        expect_layout_box(0xff70bc93,0,12,43,4);
        expect_layout_box(0xff60359a,0,16,(inner+1)/2-4,4);
        expect_layout_box(0xffec2571,0,20,(inner+1)/2-4,4);
        expect_layout_box(0xffba5137,0,24,24,4);
        expect_layout_box(0xff13ab84,0,28,33,4);
        expect_layout_box(0xff947cad,0,32,inner-40,4);
        expect_layout_box(0xffb4597e,0,36,17,4);
    }
    assert(browser_set_dpi(dpi) == NSERROR_OK);
    W.sidebar_hidden = sidebar;
    relayout_all();
    puts("PASS calc/min/max/clamp width and min/max-width use containing block, viewport and root font on live resize");
}

/* At fractional device scales, multiplying a rounded one-pixel conversion
 * distorts every authored distance. Equivalent declarations must stay aligned. */
static void check_layout_dpi(void)
{
    int original = browser_get_dpi();
    int scales[] = {90, 144};
    for (size_t i = 0; i < sizeof(scales) / sizeof(scales[0]); i++) {
        int dpi = scales[i];
        assert(browser_set_dpi(dpi) == NSERROR_OK);
        char name[] = "/tmp/nebula-layout-dpi-XXXXXX.html";
        int fd = mkstemps(name, 5);
        FILE *f = fd < 0 ? NULL : fdopen(fd, "w");
        assert(f);
        fputs("<!doctype html><title>Device pixel scale</title>"
              "<style>html{font-size:16px}body{margin:0}div,i{height:32px}section{width:240px}"
              ".a{width:240px;background:#287b91}.b{width:calc(120px + 120px);background:#5b3ba7}"
              ".c{width:180pt;background:#cd5937}.d{width:15rem;background:#697a4e}"
              ".e{display:grid;grid-template-columns:80px calc(80px + 80px)}"
              ".f{width:calc(25% + 5vw + 12pt - 2rem);background:#cb4671}"
              ".g{width:4px;background:#834d26}.h{width:calc(.4px * 10);background:#46819f}"
              "</style><section><div class=a></div><div class=b></div><div class=c></div>"
              "<div class=d></div><div class=e><i style='background:#2f6853'></i>"
              "<i style='background:#bc6632'></i></div><div class=f></div>"
              "<div class=g></div><div class=h></div></section>", f);
        assert(fclose(f) == 0);
        enter_address(name);
        await_document("Device pixel scale");
        assert(unlink(name) == 0);
        run_alarms();
        struct zui_rect page = page_rect();
        assert(render_page(&window, page));
        int full = dpi == 90 ? 225 : 360;
        int cell = dpi == 90 ? 30 : 48;
        int small = dpi == 90 ? 4 : 6;
        int mixed = (int)(full / 4.0 + page.w * .05 - 16.0 * dpi / 96.0 + .5);
        expect_layout_box(0xff287b91, 0, 0, full, cell);
        expect_layout_box(0xff5b3ba7, 0, cell, full, cell);
        expect_layout_box(0xffcd5937, 0, 2*cell, full, cell);
        expect_layout_box(0xff697a4e, 0, 3*cell, full, cell);
        expect_layout_box(0xff2f6853, 0, 4*cell, full/3, cell);
        expect_layout_box(0xffbc6632, full/3, 4*cell, full - full/3, cell);
        expect_layout_box(0xffcb4671, 0, 5*cell, mixed, cell);
        expect_layout_box(0xff834d26, 0, 6*cell, small, cell);
        expect_layout_box(0xff46819f, 0, 7*cell, small, cell);
    }
    assert(browser_set_dpi(original) == NSERROR_OK);
    puts("PASS literal, physical, font-relative and calculated CSS widths, mixed units and Grid tracks at 90 and 144 DPI");
}
