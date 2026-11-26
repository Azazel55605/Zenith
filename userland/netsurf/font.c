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

/* Generated alongside the bitmap atlases from the same list of codepoints. */
unsigned zenith_page_glyph(unsigned codepoint);

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
    return zenith_page_glyph(c);
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

/* Map destination pixel centres to ink samples. Use fixed-point steps per glyph so
 * scaling does not divide inside the pixel loop. The atlas has tight ink boxes. */
static int clamp_sample(int coordinate, int size)
{
    int last = (size - 1) << 16;
    return coordinate < 0 ? 0 : coordinate > last ? last : coordinate;
}

static void paint_glyph(struct page_raster *r, const unsigned char *ink, int src_w, int src_h,
                        long left, long top, int w, int h, uint32_t rgb)
{
    long x0 = left < r->clip.x0 ? r->clip.x0 : left;
    long y0 = top < r->clip.y0 ? r->clip.y0 : top;
    long x1 = left + w > r->clip.x1 ? r->clip.x1 : left + w;
    long y1 = top + h > r->clip.y1 ? r->clip.y1 : top + h;
    if (x0 >= x1 || y0 >= y1) return;

    if (w == src_w && h == src_h) {
        for (long y = y0; y < y1; y++) {
            const unsigned char *row = ink + (y - top) * src_w;
            for (long x = x0; x < x1; x++) {
                unsigned alpha = row[x - left];
                if (alpha) page_blend(r->pixels + y * r->stride + x, rgb, alpha);
            }
        }
        return;
    }

    int dx = (src_w << 16) / w, dy = (src_h << 16) / h;
    /* Anchor the sampling grid to the glyph, even when only part of it is redrawn. */
    int first_x = dx / 2 - 32768 + (int)(x0 - left) * dx;
    int sy = dy / 2 - 32768 + (int)(y0 - top) * dy;
    for (long y = y0; y < y1; y++, sy += dy) {
        int v = clamp_sample(sy, src_h), iy = v >> 16;
        unsigned fy = (unsigned)(v >> 8) & 255;
        const unsigned char *row = ink + iy * src_w;
        const unsigned char *next = iy + 1 < src_h ? row + src_w : row;
        int sx = first_x;
        for (long x = x0; x < x1; x++, sx += dx) {
            int u = clamp_sample(sx, src_w), ix = u >> 16;
            int jx = ix + 1 < src_w ? ix + 1 : ix;
            unsigned fx = (unsigned)(u >> 8) & 255;
            unsigned upper = row[ix] * (256 - fx) + row[jx] * fx;
            unsigned lower = next[ix] * (256 - fx) + next[jx] * fx;
            unsigned alpha = (upper * (256 - fy) + lower * fy + 32768) >> 16;
            if (alpha) page_blend(r->pixels + y * r->stride + x, rgb, alpha);
        }
    }
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
        paint_glyph(r, ink, box[2], box[3], left, upper, w, h, rgb);
        pen += scaled(f, f.atlas->advance[g]);
    }
    return NSERROR_OK;
}

struct gui_layout_table zenith_layout = {.width = width, .position = position, .split = split};
