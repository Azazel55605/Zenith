/* Check the pixels and byte offsets produced by the browser's real layout/plot callbacks. */
#include "../../../userland/netsurf/frontend.h"
#include "../../../userland/ui/ui.h"
#include "netsurf/layout.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { width = 40, height = 36 };

static void paint(const plot_font_style_t *style, const char *glyph, uint32_t pixels[height][width])
{
    struct page_raster raster = {pixels[0], width, height, width, {0, 0, width, height}};
    struct redraw_context context = {.priv = &raster};
    memset(pixels, 0, height * width * sizeof(uint32_t));
    assert(zenith_plot_text(&context, style, 2, 27, glyph, strlen(glyph)) == NSERROR_OK);
}

static void check_glyphs(plot_font_style_t style)
{
    const char *const glyphs[] = {"Ü", "ü", "Ö", "ö", "ä", "ß", "©", "œ", "“", "”", "←", "→"};
    const uint32_t blank[height][width] = {0};
    uint32_t question[height][width], sample[height][width];
    paint(&style, "?", question);
    for (unsigned i = 0; i < sizeof(glyphs) / sizeof(glyphs[0]); ++i) {
        const char *glyph = glyphs[i];
        paint(&style, glyph, sample);
        assert(memcmp(sample, blank, sizeof(sample)) != 0);
        if (strcmp(glyph, "œ") == 0) { /* Outside the shipped font subset. */
            assert(memcmp(sample, question, sizeof(sample)) == 0);
            continue;
        }
        assert(memcmp(sample, question, sizeof(sample)) != 0);
        char word[12];
        snprintf(word, sizeof(word), "A%sX", glyph);
        size_t prefix = 1 + strlen(glyph), offset;
        int measured, actual;
        assert(zenith_layout.width(&style, word, prefix, &measured) == NSERROR_OK);
        assert(zenith_layout.position(&style, word, strlen(word), measured, &offset, &actual) == NSERROR_OK);
        assert(offset == prefix && actual == measured);
    }
    int with_soft_hyphen, without_soft_hyphen;
    zenith_layout.width(&style, "do\u00admain", strlen("do\u00admain"), &with_soft_hyphen);
    zenith_layout.width(&style, "domain", strlen("domain"), &without_soft_hyphen);
    assert(with_soft_hyphen == without_soft_hyphen);
}

static double source_position(int pixel, int source, int destination)
{
    double v = (pixel + 0.5) * source / destination - 0.5;
    return fmax(0, fmin(source - 1, v));
}

static void check_coverage(int em)
{
    plot_font_style_t style = {.size = em * 3 * PLOT_STYLE_SCALE / 4, .weight = 400, .foreground = 0xffffff};
    uint32_t image[height][width];
    paint(&style, "W", image);
    const unsigned g = 'W' - 32;
    const unsigned char *box = zui_body.boxes + g * 4;
    const unsigned char *ink = zui_body.pixels + zui_body.offsets[g];
    const int left = 2 + (box[0] * em + 7) / 14;
    const int top = 27 - (zui_body.ascent * em + 7) / 14 + (box[1] * em + 7) / 14;
    const int w = (box[2] * em + 7) / 14, h = (box[3] * em + 7) / 14;
    assert(top >= 0 && left >= 0 && top + h <= height && left + w <= width);
    unsigned interpolated = 0;
    for (int y = 0; y < height; y++) {
        for (int x = 0; x < width; x++) {
            int a = image[y][x] & 255;
            if (x < left || x >= left + w || y < top || y >= top + h) {
                assert(image[y][x] == 0);
                continue;
            }
            if (em == 14) {
                assert(a == ink[(y - top) * box[2] + (x - left)]);
                continue;
            }
            double u = source_position(x - left, box[2], w);
            double v = source_position(y - top, box[3], h);
            int ix = (int)u, iy = (int)v;
            int jx = ix + 1 < box[2] ? ix + 1 : ix;
            int jy = iy + 1 < box[3] ? iy + 1 : iy;
            u -= ix; v -= iy;
            double upper = ink[iy * box[2] + ix] * (1 - u) + ink[iy * box[2] + jx] * u;
            double lower = ink[jy * box[2] + ix] * (1 - u) + ink[jy * box[2] + jx] * u;
            assert(abs(a - (int)lround(upper * (1 - v) + lower * v)) <= 2);
            int nearest = ink[((y - top) * box[3] / h) * box[2] + (x - left) * box[2] / w];
            if (abs(a - nearest) > 4) interpolated++;
        }
    }
    if (em != 14) {
        assert(interpolated > 5);
        uint32_t clipped[height][width] = {0};
        struct rect clip = {left + w / 3, top + h / 3, left + w, top + h};
        struct page_raster raster = {clipped[0], width, height, width, clip};
        struct redraw_context context = {.priv = &raster};
        zenith_plot_text(&context, &style, 2, 27, "W", 1);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) {
                int inside = x >= clip.x0 && x < clip.x1 && y >= clip.y0 && y < clip.y1;
                assert(clipped[y][x] == (inside ? image[y][x] : 0));
            }
    }
}

int main(void)
{
    check_glyphs((plot_font_style_t){.size = 12 * PLOT_STYLE_SCALE, .weight = 400});
    check_glyphs((plot_font_style_t){.size = 18 * PLOT_STYLE_SCALE, .weight = 700});
    check_glyphs((plot_font_style_t){.size = 12 * PLOT_STYLE_SCALE, .family = PLOT_FONT_FAMILY_MONOSPACE});
    check_coverage(14);
    check_coverage(18);
    check_coverage(11);
    puts("PASS Latin-1 accents, copyright, curly quotes and arrows have visible page glyphs and UTF-8 hit-test offsets");
    puts("PASS native glyph coverage is exact and resized glyph edges use centre-sampled antialiasing");
    return 0;
}
