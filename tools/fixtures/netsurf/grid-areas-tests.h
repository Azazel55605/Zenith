/* Use source order main/header/nav so ordinary auto-placement cannot pass. */
static void check_named_grid_areas(void)
{
    int dpi = browser_get_dpi();
    assert(browser_set_dpi(96) == NSERROR_OK);
    char path[] = "/tmp/nebula-areas-XXXXXX.html";
    int fd = mkstemps(path, 5);
    assert(fd >= 0);
    FILE *file = fdopen(fd, "w");
    assert(file);
    fputs("<!doctype html><title>News grid</title><style>body{margin:0}"
          ".board{display:grid;width:180px;gap:4px;"
          "grid-template:'mast mast' 12px 'nav main-story' 24px / 50px 1fr;"
          "grid-template-areas:'mast mast' 'nav mast';"
          "grid:30px / dense 50px;grid-template:30px / inherit}"
          "header{grid-area:mast!important;grid-area:nav;background:#c45912}"
          "nav{grid-area:nav;background:#b48406}"
          "main{grid-area:main-story;background:#4266ac}"
          ".listing{display:grid;width:62px;grid:auto-flow 5px / 20px 40px;gap:2px}"
          ".listing div{background:#5a6d32}.listing div+div{background:#803e66}"
          ".flag{height:7px;width:9px;background:#aaaaaa}"
          "@supports (display:grid) and (grid-template-columns:repeat(2, 1fr)){"
          "@media screen and (min-width:200px){.flag{background:#d41b6a}}}"
          "@media screen{@supports not (display:submarine){.flag{width:17px}}}"
          "@supports (grid-template-columns:broken) or selector(.a, .b){.flag{height:48px}}"
          "@supports not selector(.flag){.flag{width:45px}}"
          "@supports ((display:unknown) or (display:flex)){.flag{height:9px}}"
          "@supports (42){.flag{width:90px}}@supports not (42){.flag{width:18px}}"
          "@supports not (display:grid) and (color:blue){.flag{width:90px}}"
          "html{font-size:16px}.measure{display:grid;width:480px;height:7px;"
          "grid-template-columns:1fr min(45rem,90%) 1fr}"
          ".measure div{grid-column:2;background:#fa7113}.measure.big{width:900px}.big div{background:#976314}"
          ".mixed{display:grid;grid-auto-columns:calc(50px + 1rem);grid-auto-flow:column;height:4px;"
          "grid-template-columns:repeat(2,clamp(10px,7%,41px));width:500px}"
          ".mixed div{background:#eb3213}.mixed div+div{background:#214516}.mixed div+div+div{background:#cabc22}"
          "</style><div class=board><main></main><header></header><nav></nav></div>"
          "<section class=listing><div></div><div></div></section><div class=flag></div>"
          "<div class=measure><div></div></div><div class='measure big'><div></div></div>"
          "<div class=mixed><div></div><div></div><div></div></div>", file);
    assert(fclose(file) == 0);
    enter_address(path);
    await_document("News grid");
    assert(unlink(path) == 0);
    assert(render_page(&window, page_rect()));
    expect_layout_box(0xffc45912, 0, 0, 180, 12);
    expect_layout_box(0xffb48406, 0, 16, 50, 24);
    expect_layout_box(0xff4266ac, 54, 16, 126, 24);
    expect_layout_box(0xff5a6d32, 0, 40, 20, 5);
    expect_layout_box(0xff803e66, 22, 40, 40, 5);
    expect_layout_box(0xffd41b6a, 0, 45, 18, 9);
    expect_layout_box(0xfffa7113, 24, 54, 432, 7);
    /* The test viewport clips the right side of the desktop-width layout. */
    expect_layout_box(0xff976314, 90, 61, W.page_w - 90, 7);
    expect_layout_box(0xffeb3213, 0, 68, 35, 4);
    expect_layout_box(0xff214516, 35, 68, 35, 4);
    expect_layout_box(0xffcabc22, 70, 68, 66, 4);
    assert(browser_set_dpi(dpi) == NSERROR_OK);
    puts("PASS named areas, template and automatic grid shorthands, math on explicit/repeated/implicit tracks, nested @supports/@media, cascade and invalid declarations");
}
