#include "netsurf/browser.h"
/* Compare the entire painted rectangle. Sampling a single pixel also accepts a
 * solid block that accidentally covers both columns or overlaps the next row. */
static void expect_layout_box(uint32_t color, int x, int y, int width, int height)
{
    int left = W.page_w, top = W.page_h, right = 0, bottom = 0, pixels = 0;
    for (int row = 0; row < W.page_h; row++) {
        for (int col = 0; col < W.page_w; col++) {
            if (W.page[row * W.page_w + col] != color) continue;
            if (left > col) left = col;
            if (top > row) top = row;
            if (right <= col) right = col + 1;
            if (bottom <= row) bottom = row + 1;
            pixels++;
        }
    }
    if (left != x || top != y || right != x + width || bottom != y + height ||
        pixels != width * height) {
        fprintf(stderr, "Color %08x: expected (%d,%d) %dx%d; painted (%d,%d) %dx%d (%d px)\n",
                color, x, y, width, height, left, top, right-left, bottom-top, pixels);
        abort();
    }
}

static void check_grid_layout(void)
{
    const bool previous_sidebar = W.sidebar_hidden;
    const int previous_dpi = browser_get_dpi();
    assert(browser_set_dpi(96) == NSERROR_OK); /* integral CSS-pixel geometry */
    for (int wide = 0; wide <= 1; wide++) {
        W.sidebar_hidden = wide != 0;
        relayout_all();
        char path[] = "/tmp/nebula-grid-XXXXXX.html";
        int fd = mkstemps(path, 5);
        assert(fd >= 0);
        FILE *file = fdopen(fd, "w");
        assert(file);
        const char *title = wide ? "Grid wide" : "Grid narrow";
        fprintf(file, "<!doctype html><title>%s</title>", title);
        fputs("<style>html,body{margin:0;padding:0}"
              ".cards{display:flex;display:grid;grid-template-columns:repeat(auto-fit,minmax(143px,1fr));gap:8px}"
              ".cards div{height:20px} .a{background:#a11729}.b{background:#236e81}"
              ".c{background:#7356d0}.d{background:#cc8239}"
              ".lines{display:grid;width:200px;grid-template-columns:repeat(2,50px) minmax(0,1fr);"
              "grid-template-columns:bogus;grid-template-rows:12px 16px;gap:4px 8px}"
              ".span{background:#17515c;grid-row:2;grid-column:1/-1}"
              ".middle{background:#648310;grid-column:2;grid-row:1}"
              ".auto{background:#285ed4}.last{background:#fc7862}"
              ".two{display:grid;width:100%;grid-template-columns:repeat(auto-fit,minmax(100px,1fr));column-gap:10px}"
              ".two div{height:8px}.one{background:#21aa95}.twoEnd{background:#3759ca}"
              ".filled{display:grid;height:8px;width:500px;grid-template-columns:repeat(auto-fill,100px);gap:10px}"
              ".fill{background:#9f2151}"
              ".ratio{display:grid;width:210px;grid-template-columns:1fr 2fr;height:7px;column-gap:6px}"
              ".r1{background:#0142f7}.r2{background:#6ec44f}"
              ".colflow{display:grid;grid-template-rows:6px 8px;grid-auto-columns:13px 23px;"
              "grid-auto-flow:column;gap:2px 3px}"
              ".cf1{background:#9184ad}.cf2{background:#5741b7}.cf3{background:#112546}"
              ".dense{display:grid;grid-template-columns:repeat(3,20px);grid-auto-rows:5px 7px;"
              "grid-auto-flow:row dense;gap:1px 2px}.dense .pair{grid-column:span 2}"
              ".pair1{background:#d93322}.pair2{background:#184a21}.hole{background:#3b287e}"
              ".inline{font-size:0;line-height:0;height:8px}.ig{display:inline-grid;vertical-align:top;"
              "grid-template-columns:19px 31px;gap:3px;height:8px}.ig1{background:#710b95}"
              ".ig2{background:#470c29}.ig3{background:#203a9f}"
              ".freeze{display:grid;width:100px;height:4px;grid-template-columns:minmax(60px,1fr) "
              "minmax(17px,1fr) 1fr}.fz1{background:#791d6a}.fz2{background:#c15774}.fz3{background:#7bc343}"
              ".fit{display:grid;grid-template-columns:fit-content(36px) 1fr;"
              "grid-template-columns:fit-content(1fr) 1fr;width:140px;height:7px}"
              ".ft1,.ft2{height:7px;overflow:hidden}.ft1{color:#a3901b;background:#a3901b}.ft2{background:#ca68a4}"
              ".aligned{display:grid;grid-template-columns:20px 20px;grid-template-rows:10px;"
              "place-content:center space-between;place-items:center;width:100px;height:20px}"
              ".al1{width:8px;height:4px;background:#ea704a}.al2{place-self:end start;width:8px;"
              "height:4px;background:#685dca}"
              ".pct{display:grid;width:50px;height:30px;grid-template-columns:25px 25px;grid-template-rows:10px auto}"
              ".declared{grid-area:1 / 1;height:50%;background:#65ca47}.nest{display:grid;grid-area:2 / 1;"
              "grid-template-rows:1fr 1fr}.nest>i{grid-row:2;background:#bc345a}.force{grid-area:2 / 2;height:20px}"
              ".normal,.start{display:grid;width:40px;grid-template-columns:auto auto}.start{justify-content:start}"
              ".normal>i,.start>i{min-width:5px;height:3px}.normal>i+i{background:#0c356a}.start>i+i{background:#de7534}"
              ".rtl{display:grid;direction:rtl;width:60px;height:3px;grid-template-columns:10px 20px;"
              "column-gap:2px;justify-items:start}.rtl>i{width:5px;background:#30238d}.rtl>i+i{background:#482414}"
              ".named{display:grid;grid-template-columns:[outer header-start] repeat(3,[col] 12px [edge]) "
              "[stop] 18px [header-end edge];grid-template-columns:[span] 100px;column-gap:2px;grid-auto-rows:4px}"
              ".n1{grid-row:1;grid-column:col 2 / span 2 edge;background:#cc5204}"
              ".n2{grid-row:2;grid-column:span 2 col / -2 edge;background:#a52e10}"
              ".n3{grid-row:3;grid-column:header;background:#3e6b40}.n4{grid-row:4;grid-column:outer / stop;background:#2b4097}"
              ".namedAuto{display:grid;width:64px;grid-auto-rows:3px;grid-template-columns:[before] "
              "repeat(auto-fill,[tile] 10px [foot]) [stop] 14px [end];column-gap:2px}"
              ".na1{grid-row:1;grid-column:tile -2 / span 2 foot;background:#b22036}"
              ".na2{grid-row:2;grid-column:before / stop;background:#701345}"
              ".nstrings{display:grid;grid-template:[r-start] 'a b' 3px [r-mid] [r-second] "
              "'c d' 5px [r-end] / [left] 20px [split] 40px}.ns{grid-row:r-mid / r-end;grid-column:split;background:#b27620}"
              "</style>"
              "<div class=cards><div class=a></div><div class=b></div><div class=c></div><div class=d></div></div>"
              "<div class=lines><div class=span></div><div class=middle></div><div class=auto></div><div class=last></div></div>"
              "<div class=two><div class=one></div><div class=twoEnd></div></div>"
              "<div class=filled><div class=fill></div></div>"
              "<div class=ratio><div class=r1></div><div class=r2></div></div>"
              "<div class=colflow><div class=cf1></div><div class=cf2></div><div class=cf3></div></div>"
              "<div class=dense><div class='pair pair1'></div><div class='pair pair2'></div><div class=hole></div></div>"
              "<div class=inline><span class=ig><b class=ig1></b><b class=ig2></b></span>"
              "<span class=ig><b class=ig3></b></span></div>"
              "<div class=freeze><div class=fz1></div><div class=fz2></div><div class=fz3></div></div>"
              "<div class=fit><div class=ft1>a b c d e f g h i j k l m n o p q</div><div class=ft2></div></div>"
              "<div class=aligned><div class=al1></div><div class=al2></div></div>"
              "<div class=pct><div class=declared></div><div class=nest><i></i></div><div class=force></div></div>"
              "<div class=normal><i></i><i></i></div><div class=start><i></i><i></i></div>"
              "<div class=rtl><i></i><i></i></div>"
              "<div class=named><b class=n1></b><b class=n2></b><b class=n3></b><b class=n4></b></div>"
              "<div class=namedAuto><b class=na1></b><b class=na2></b></div><div class=nstrings><b class=ns></b></div>", file);
        assert(fclose(file) == 0);
        enter_address(path);
        await_document(title);
        assert(unlink(path) == 0);
        struct zui_rect page = page_rect();
        assert(render_page(&window, page));
        int columns = wide ? 4 : 3;
        int track_space = page.w - 8 * (columns-1);
        const uint32_t colors[] = {0xffa11729, 0xff236e81, 0xff7356d0, 0xffcc8239};
        for (int i = 0; i < 4; i++) {
            int col = i % columns;
            int start = track_space * col / columns;
            int width = track_space * (col+1) / columns - start;
            expect_layout_box(colors[i], start + 8*col, 28*(i/columns), width, 20);
        }
        int y = wide ? 20 : 48;
        expect_layout_box(0xff17515c, 0, y+16, 200, 16);
        expect_layout_box(0xff648310, 58, y, 50, 12);
        expect_layout_box(0xff285ed4, 0, y, 50, 12);
        expect_layout_box(0xfffc7862, 116, y, 84, 12);
        y += 32;
        int half = (page.w - 10) / 2;
        expect_layout_box(0xff21aa95, 0, y, half, 8);
        expect_layout_box(0xff3759ca, half + 10, y, page.w - 10 - half, 8);
        expect_layout_box(0xff9f2151, 0, y + 8, 100, 8);
        expect_layout_box(0xff0142f7, 0, y + 16, 68, 7);
        expect_layout_box(0xff6ec44f, 74, y + 16, 136, 7);
        y += 23;
        expect_layout_box(0xff9184ad, 0, y, 13, 6);
        expect_layout_box(0xff5741b7, 0, y + 8, 13, 8);
        expect_layout_box(0xff112546, 16, y, 23, 6);
        y += 16;
        expect_layout_box(0xffd93322, 0, y, 42, 5);
        expect_layout_box(0xff184a21, 0, y + 6, 42, 7);
        expect_layout_box(0xff3b287e, 44, y, 20, 5);
        y += 13;
        expect_layout_box(0xff710b95, 0, y, 19, 8);
        expect_layout_box(0xff470c29, 22, y, 31, 8);
        expect_layout_box(0xff203a9f, 53, y, 19, 8);
        y += 8;
        expect_layout_box(0xff791d6a, 0, y, 60, 4);
        expect_layout_box(0xffc15774, 60, y, 20, 4);
        expect_layout_box(0xff7bc343, 80, y, 20, 4);
        y += 4;
        expect_layout_box(0xffa3901b, 0, y, 36, 7);
        expect_layout_box(0xffca68a4, 36, y, 104, 7);
        y += 7;
        expect_layout_box(0xffea704a, 6, y+8, 8, 4);
        expect_layout_box(0xff685dca, 80, y+11, 8, 4);
        y += 20;
        expect_layout_box(0xff65ca47, 0, y, 25, 5);
        expect_layout_box(0xffbc345a, 0, y+20, 25, 10);
        y += 30;
        expect_layout_box(0xff0c356a, 20, y, 20, 3);
        expect_layout_box(0xffde7534, 5, y+3, 5, 3);
        expect_layout_box(0xff30238d, 55, y+6, 5, 3);
        expect_layout_box(0xff482414, 43, y+6, 5, 3);
        y += 9;
        expect_layout_box(0xffcc5204, 14, y, 26, 4);
        expect_layout_box(0xffa52e10, 14, y+4, 26, 4);
        expect_layout_box(0xff3e6b40, 0, y+8, 60, 4);
        expect_layout_box(0xff2b4097, 0, y+12, 40, 4);
        y += 16;
        expect_layout_box(0xffb22036, 24, y, 22, 3);
        expect_layout_box(0xff701345, 0, y+3, 46, 3);
        expect_layout_box(0xffb27620, 20, y+9, 40, 5);
    }
    assert(browser_set_dpi(previous_dpi) == NSERROR_OK);
    W.sidebar_hidden = previous_sidebar;
    relayout_all();
    puts("PASS narrow/wide CSS Grid card layout, minmax, fr, repeat/auto-fit/auto-fill, gaps, forward/backward named lines and spans, cyclic implicit tracks and row/column/dense placement");
}

/* Each formatting context takes its top margin between these siblings,
 * including when child content supplies its height. */
static void check_formatting_margins(void)
{
    int dpi = browser_get_dpi();
    assert(browser_set_dpi(96) == NSERROR_OK);
    char name[] = "/tmp/nebula-format-margins-XXXXXX.html";
    int fd = mkstemps(name, 5);
    assert(fd >= 0);
    FILE *f = fdopen(fd, "w");
    assert(f);
    fputs("<!doctype html><title>Formatting margins</title><style>body{margin:0}"
          "section{display:grid;width:30px}section+section{margin-top:12px}"
          ".flex{display:flex}i{width:30px;height:4px;display:block}"
          "</style><section><i style='background:#2f49da'></i></section>"
          "<section><i style='background:#ab265a'></i></section>"
          "<section class=flex style='margin-bottom:5px'><i style='background:#de7428'></i></section>"
          "<section class=flex><i style='background:#41c567'></i></section>", f);
    assert(fclose(f) == 0);
    enter_address(name);
    await_document("Formatting margins");
    assert(unlink(name) == 0);
    run_alarms();
    assert(render_page(&window, page_rect()));
    expect_layout_box(0xff2f49da, 0, 0, 30, 4);
    expect_layout_box(0xffab265a, 0, 16, 30, 4);
    expect_layout_box(0xffde7428, 0, 32, 30, 4);
    expect_layout_box(0xff41c567, 0, 48, 30, 4);
    assert(browser_set_dpi(dpi) == NSERROR_OK);
    puts("PASS adjacent Grid and Flex containers respect collapsed sibling margins");
}
