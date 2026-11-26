static void check_color_syntax(void)
{
    int dpi = browser_get_dpi();
    assert(browser_set_dpi(96) == NSERROR_OK); /* fixed swatch sampling coordinates */
    char path[] = "/tmp/nebula-colors-XXXXXX.html";
    int fd = mkstemps(path, 5);
    assert(fd >= 0);
    FILE *file = fdopen(fd, "w");
    assert(file);
    const struct {
        const char *style;
        uint32_t painted;
    } cases[] = {
        {"rgb(207 232 220 / 20%)", 0xff43555f},
        {"rgba(207 232 220 / .2)", 0xff43555f},
        {"rgb(25% 64 50% / 100%)", 0xff404080},
        {"rgb(255.6 110.75 -2.5)", 0xffff6f00},
        {"#cf5a", 0xff93ba4e},
        {"#ccee55aa", 0xff93af4e},
        {"#000000fe", 0xff000000},
        {"rgb(none none 64 / none)", 0xff203040},
        {"rgb(0, 128, 0, 50%)", 0xff105820},
        {"rgba(100%, 0%, 100%, 1)", 0xffff00ff},
        {"hsl(120 30% 50%)", 0xff59a659},
        {"hsla(120deg 30 50% / 50%)", 0xff3d6b4d},
        {"hsl(-.5turn 100% 50)", 0xff00ffff},
        {"hsl(300GRAD 100 50)", 0xff8000ff},
        {"hsl(3.14159rad 100% 50%)", 0xff00ffff},
        {"hsl(120deg, 30%, 50%, 1)", 0xff59a659},
        {"hsl(none 100% 50%)", 0xffff0000},
        {"hsla(240 none 50)", 0xff808080},
        {"hsl(0 -50% 40%)", 0xff666666},
        {"hsl(30deg 100% 50% / none)", 0xff203040},
        {"hsl(10%, 30%, 50%)", 0xff733515},
        {"hsl(30px 100% 50%)", 0xff733515},
        {"hsl(0, 50%, 30% / 1)", 0xff733515},
        {"hsl(0, 50, 30%)", 0xff733515},
        {"hsl(none, 100%, 50%)", 0xff733515},
        {"hsl(0, 100%, 50%, none)", 0xff733515},
        {"rgb(0 255 0, 1)", 0xff733515},
        {"rgb(100%, 0, 0%)", 0xff733515},
        {"rgb(0, 64, 96 / 1)", 0xff733515},
        {"#abcd123", 0xff733515}
    };
    fputs("<!doctype html><title>Colour syntax</title><style>body{margin:0;background:#ff000080}.base{background:#203040}"
          ".base>div{height:10px;background:#733515}</style><div class=base>", file);
    for (size_t i = 0; i < sizeof(cases)/sizeof(cases[0]); i++)
        fprintf(file, "<div style='background-color:%s'></div>", cases[i].style);
    fputs("</div><table style='border-spacing:0'><tr style='background:#00c000'>"
          "<td style='padding:0;width:40px;height:20px;background:#0000ff80'></td>"
          "</tr></table>", file);
    assert(fclose(file) == 0);
    enter_address(path);
    await_document("Colour syntax");
    assert(unlink(path) == 0);
    struct zui_rect page = page_rect();
    assert(render_page(&window, page));
    for (size_t i = 0; i < sizeof(cases)/sizeof(cases[0]); i++) {
        uint32_t actual = W.page[(i * 10 + 4) * page.w + 5];
        if (actual != cases[i].painted) {
            fprintf(stderr, "%s: expected %08x, painted %08x\n", cases[i].style, cases[i].painted, actual);
            abort();
        }
    }
    int table_y = (int)(sizeof(cases)/sizeof(cases[0])) * 10;
    assert(W.page[(table_y + 4) * page.w + 5] == 0xff006080);
    assert(W.page[(table_y + 30) * page.w + 5] == 0xffff7f7f);
    assert(browser_set_dpi(dpi) == NSERROR_OK);
    puts("PASS modern RGB/HSL, all hue angle units, slash alpha, RGBA hex and legacy comma colours; mixed grammars rejected");
}
