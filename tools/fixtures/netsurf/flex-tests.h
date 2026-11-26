static void check_flex_layout(void)
{
    const bool old_sidebar = W.sidebar_hidden;
    const int old_dpi = browser_get_dpi();
    assert(browser_set_dpi(96) == NSERROR_OK);
    for (int wide = 0; wide < 2; ++wide) {
        W.sidebar_hidden = wide != 0;
        relayout_all();
        char path[] = "/tmp/nebula-flex-XXXXXX.html";
        int fd = mkstemps(path, 5);
        assert(fd >= 0);
        FILE *file = fdopen(fd, "w");
        assert(file);
        const char *title = wide ? "Wide flex" : "Narrow flex";
        fprintf(file, "<!doctype html><title>%s</title>", title);
        fputs("<style>body{margin:0}.cards{display:flex;flex-wrap:wrap;justify-content:space-between;"
              "align-content:center;gap:8px 16px;height:88px}.cards>div{flex:0 0 180px;height:20px}"
              ".column{display:flex;flex-direction:column-reverse;height:100px;width:60px;gap:6px;"
              "align-items:center;justify-content:flex-end}.column>div{width:14px;height:20px}"
              ".grow{display:flex;width:210px;gap:10px}.grow>div{height:8px;flex:1 1 0}"
              ".auto{display:flex;width:200px;gap:12px;justify-content:center}"
              ".auto>div{width:20px;height:10px}.auto>div+div{margin-left:auto}"
              ".loose{display:flex;width:106px;justify-content:space-evenly}.loose>div{width:10px;height:8px}"
              ".reverse{display:flex;flex-direction:row-reverse;justify-content:space-around;width:80px}"
              ".reverse>div{width:10px;height:6px}.wrap{display:flex;flex-wrap:wrap-reverse;align-content:flex-start;"
              "width:36px;height:24px;gap:4px 2px}.wrap>div{width:20px;height:8px}"
              ".percent{display:flex;flex-direction:column;height:80px;width:24px;gap:10%}"
              ".percent>div{flex:0 0 25%}"
              "</style><div class=cards><div style='background:#781b21'></div>"
              "<div style='background:#185576'></div><div style='background:#71a068'></div></div>"
              "<div class=column><div style='background:#774419'></div><div style='background:#124b62'></div></div>"
              "<div class=grow><div style='background:#d47a15'></div><div style='background:#95bc34'></div></div>"
              "<div class=auto><div style='background:#68b489'></div><div style='background:#8243ad'></div></div>"
              "<div class=loose><div style='background:#2678a3'></div><div style='background:#951c77'></div>"
              "<div style='background:#3a681d'></div></div>"
              "<div class=reverse><div style='background:#897659'></div><div style='background:#694b82'></div></div>"
              "<div class=wrap><div style='background:#573f4a'></div><div style='background:#762d5a'></div></div>"
              "<div class=percent><div style='background:#783d68'></div><div style='background:#744730'></div></div>", file);
        assert(fclose(file) == 0);
        enter_address(path);
        await_document(title);
        assert(unlink(path) == 0);
        struct zui_rect page = page_rect();
        assert(render_page(&window, page));
        int first_y = wide ? 34 : 20;
        expect_layout_box(0xff781b21, 0, first_y, 180, 20);
        expect_layout_box(0xff185576, wide ? (page.w - 180) / 2 : page.w - 180, first_y, 180, 20);
        expect_layout_box(0xff71a068, wide ? page.w - 180 : 0, wide ? first_y : 48, 180, 20);
        expect_layout_box(0xff774419, 23, 114, 14, 20);
        expect_layout_box(0xff124b62, 23, 88, 14, 20);
        expect_layout_box(0xffd47a15, 0, 188, 100, 8);
        expect_layout_box(0xff95bc34, 110, 188, 100, 8);
        expect_layout_box(0xff68b489, 0, 196, 20, 10);
        expect_layout_box(0xff8243ad, 180, 196, 20, 10);
        expect_layout_box(0xff2678a3, 19, 206, 10, 8);
        expect_layout_box(0xff951c77, 48, 206, 10, 8);
        expect_layout_box(0xff3a681d, 77, 206, 10, 8);
        expect_layout_box(0xff897659, 55, 214, 10, 6);
        expect_layout_box(0xff694b82, 15, 214, 10, 6);
        expect_layout_box(0xff573f4a, 0, 236, 20, 8);
        expect_layout_box(0xff762d5a, 0, 224, 20, 8);
        expect_layout_box(0xff783d68, 0, 244, 24, 20);
        expect_layout_box(0xff744730, 0, 272, 24, 20);
    }
    W.sidebar_hidden = old_sidebar;
    relayout_all();
    assert(browser_set_dpi(old_dpi) == NSERROR_OK);
    puts("PASS responsive flex wrapping, space-between, align-content, row/column gap, grow, reverse column and auto margins");
}
