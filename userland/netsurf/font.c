/* NetSurf measures and draws each run through the same scaled Zenith font. */
#include "../ui/ui.h"
#include "frontend.h"
#include "netsurf/layout.h"
#include "utils/utf8.h"
#include <limits.h>

struct face {
    const struct zui_font *atlas;
    int em, native_em;
};

static struct face choose(const plot_font_style_t *style)
{
    /* At 96 dpi, 1pt = 4/3 CSS px. Zenith's atlases use pixel sizes. */
    long px = ((long)style->size * 4 + 1536) / 3072;
    int em = px < 1 ? 1 : px > 512 ? 512 : (int)px;
    if (style->family == PLOT_FONT_FAMILY_MONOSPACE)
        return (struct face){&zui_mono, em, 14};
    if (style->weight >= 600 && em >= 20)
        return (struct face){&zui_heading, em, 24};
    return (struct face){style->weight >= 600 ? &zui_label : &zui_body, em, 14};
}

static unsigned glyph(const char *s, size_t n, size_t *at)
{
    unsigned c = (unsigned char)s[*at];
    if (c < 128)
        (*at)++;
    else {
        c = utf8_to_ucs4(s + *at, n - *at);
        *at = utf8_next(s, n, *at);
    }
    if (c == 0xa0)
        c = ' ';
    if (c >= 32 && c <= 126)
        return c - 32;
    if (c == 0xb7)
        return 95;
    if (c == 0x2192)
        return 96;
    if (c == 0x2014)
        return 97;
    if (c == 0x2026)
        return 98;
    return '?' - 32;
}

static int scaled(struct face f, int value)
{
    return (value * f.em + f.native_em / 2) / f.native_em;
}
static int width_add(int a, int b)
{
    return a > INT_MAX - b ? INT_MAX : a + b;
}

static nserror width(const plot_font_style_t *style, const char *s, size_t n, int *out)
{
    struct face f = choose(style);
    int x = 0;
    for (size_t i = 0; i < n;)
        x = width_add(x, scaled(f, f.atlas->advance[glyph(s, n, &i)]));
    *out = x;
    return NSERROR_OK;
}

static nserror position(const plot_font_style_t *style, const char *s, size_t n, int target, size_t *offset,
                        int *actual)
{
    struct face f = choose(style);
    size_t at = 0;
    int x = 0;
    while (at < n) {
        size_t next = at;
        int advance = scaled(f, f.atlas->advance[glyph(s, n, &next)]);
        if ((long)target < (long)x + (advance + 1) / 2)
            break;
        at = next;
        x = width_add(x, advance);
    }
    *offset = at;
    *actual = x;
    return NSERROR_OK;
}

static nserror split(const plot_font_style_t *style, const char *s, size_t n, int target, size_t *offset,
                     int *actual)
{
    struct face f = choose(style);
    size_t best = 0;
    int x = 0, best_width = 0;
    for (size_t i = 0; i < n;) {
        if (s[i] == ' ' && i) {
            if (x > target && best)
                break;
            best = i;
            best_width = x;
            if (x >= target)
                break;
        }
        x = width_add(x, scaled(f, f.atlas->advance[glyph(s, n, &i)]));
        if (i == n && (x <= target || !best)) {
            best = n;
            best_width = x;
        }
    }
    *offset = best;
    *actual = best_width;
    return NSERROR_OK;
}

nserror zenith_plot_text(const struct redraw_context *ctx, const plot_font_style_t *style, int x, int baseline,
                         const char *s, size_t n)
{
    struct page_raster *r = ctx->priv;
    struct face f = choose(style);
    uint32_t rgb = page_rgb(style->foreground);
    long pen = x, top = (long)baseline - scaled(f, f.atlas->ascent);
    for (size_t i = 0; i < n && pen < r->clip.x1;) {
        unsigned g = glyph(s, n, &i);
        const unsigned char *box = f.atlas->boxes + g * 4, *ink = f.atlas->pixels + f.atlas->offsets[g];
        long left = pen + scaled(f, box[0]), upper = top + scaled(f, box[1]);
        int w = scaled(f, box[2]), h = scaled(f, box[3]);
        for (long y = upper < r->clip.y0 ? r->clip.y0 : upper; y < upper + h && y < r->clip.y1; y++) {
            const unsigned char *row = ink + (y - upper) * box[3] / h * box[2];
            for (long px = left < r->clip.x0 ? r->clip.x0 : left; px < left + w && px < r->clip.x1; px++) {
                unsigned a = row[(px - left) * box[2] / w];
                if (a)
                    page_blend(r->pixels + y * r->stride + px, rgb, a);
            }
        }
        pen += scaled(f, f.atlas->advance[g]);
    }
    return NSERROR_OK;
}

struct gui_layout_table zenith_layout = {.width = width, .position = position, .split = split};
