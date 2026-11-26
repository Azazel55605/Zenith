/* NetSurf screen plotters. Intersect before rasterizing: a long page or a
 * negative CSS position must not turn into an off-screen painting loop. */
#include "frontend.h"
#include "netsurf/bitmap.h"
#include "netsurf/content.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

static long imin(long a, long b)
{
    return a < b ? a : b;
}
static long imax(long a, long b)
{
    return a > b ? a : b;
}

static nserror clip(const struct redraw_context *ctx, const struct rect *area)
{
    struct page_raster *r = ctx->priv;
    r->clip = (struct rect){imax(0, area->x0), imax(0, area->y0), imin(r->width, area->x1),
                            imin(r->height, area->y1)};
    return NSERROR_OK;
}

static void fill(struct page_raster *r, long x0, long y0, long x1, long y1, uint32_t rgb)
{
    x0 = imax(x0, r->clip.x0);
    x1 = imin(x1, r->clip.x1);
    y0 = imax(y0, r->clip.y0);
    y1 = imin(y1, r->clip.y1);
    if (x0 >= x1 || y0 >= y1)
        return;
    for (long y = y0; y < y1; y++) {
        uint32_t *p = r->pixels + y * r->stride + x0;
        for (long x = x0; x < x1; x++)
            *p++ = rgb;
    }
}

static void segment(struct page_raster *r, const plot_style_t *style, int x0, int y0, int x1, int y1)
{
    if (style->stroke_type == PLOT_OP_TYPE_NONE || style->stroke_colour == NS_TRANSPARENT)
        return;
    long thick = imax(1, plot_style_fixed_to_int(style->stroke_width)), half = thick / 2;
    uint32_t rgb = page_rgb(style->stroke_colour);
    bool horizontal = labs((long)x1 - x0) >= labs((long)y1 - y0);
    long a = horizontal ? x0 : y0, b = horizontal ? x1 : y1;
    long c = horizontal ? y0 : x0, d = horizontal ? y1 : x1;
    if (a > b) {
        long tmp = a;
        a = b;
        b = tmp;
        tmp = c;
        c = d;
        d = tmp;
    }
    if (a == b) {
        fill(r, x0 - half, y0 - half, x0 - half + thick, y0 - half + thick, rgb);
        return;
    }
    long start = imax(a, horizontal ? r->clip.x0 : r->clip.y0);
    long end = imin(b, (horizontal ? r->clip.x1 : r->clip.y1) - 1);
    long dash = style->stroke_type == PLOT_OP_TYPE_DOT    ? thick
                : style->stroke_type == PLOT_OP_TYPE_DASH ? 3 * thick
                                                          : 0;
    if (c == d && !dash) {
        if (horizontal)
            fill(r, start, c - half, end + 1, c - half + thick, rgb);
        else
            fill(r, c - half, start, c - half + thick, end + 1, rgb);
        return;
    }
    for (long i = start; i <= end; i++) {
        if (dash && ((i - a) / dash) % 2)
            continue;
        long coord = c + (long)((double)(i - a) * (d - c) / (b - a)) - half;
        if (horizontal)
            fill(r, i, coord, i + 1, coord + thick, rgb);
        else
            fill(r, coord, i, coord + thick, i + 1, rgb);
    }
}

static nserror line(const struct redraw_context *ctx, const plot_style_t *style, const struct rect *s)
{
    segment(ctx->priv, style, s->x0, s->y0, s->x1, s->y1);
    return NSERROR_OK;
}

static nserror rectangle(const struct redraw_context *ctx, const plot_style_t *style, const struct rect *s)
{
    if (style->fill_type != PLOT_OP_TYPE_NONE && style->fill_colour != NS_TRANSPARENT)
        fill(ctx->priv, s->x0, s->y0, s->x1, s->y1, page_rgb(style->fill_colour));
    if (style->stroke_type != PLOT_OP_TYPE_NONE) {
        segment(ctx->priv, style, s->x0, s->y0, s->x1, s->y0);
        segment(ctx->priv, style, s->x1, s->y0, s->x1, s->y1);
        segment(ctx->priv, style, s->x1, s->y1, s->x0, s->y1);
        segment(ctx->priv, style, s->x0, s->y1, s->x0, s->y0);
    }
    return NSERROR_OK;
}

static nserror disc(const struct redraw_context *ctx, const plot_style_t *s, int cx, int cy, int radius)
{
    struct page_raster *r = ctx->priv;
    if (radius < 0)
        return NSERROR_OK;
    long inner = imax(0, (long)radius - imax(1, plot_style_fixed_to_int(s->stroke_width)));
    long end = imin(r->clip.y1 - 1, (long)cy + radius);
    for (long y = imax(r->clip.y0, (long)cy - radius); y <= end; y++) {
        long dy = y - cy;
        long outer = (long)sqrt((double)radius * radius - (double)dy * dy);
        if (s->fill_type != PLOT_OP_TYPE_NONE && s->fill_colour != NS_TRANSPARENT)
            fill(r, cx - outer, y, cx + outer + 1, y + 1, page_rgb(s->fill_colour));
        if (s->stroke_type != PLOT_OP_TYPE_NONE && s->stroke_colour != NS_TRANSPARENT) {
            long cut = labs(dy) >= inner ? 0 : (long)sqrt((double)inner * inner - (double)dy * dy);
            fill(r, cx - outer, y, cx - cut + 1, y + 1, page_rgb(s->stroke_colour));
            fill(r, cx + cut, y, cx + outer + 1, y + 1, page_rgb(s->stroke_colour));
        }
    }
    return NSERROR_OK;
}

static int coord(double p)
{
    return p < -2147483647.0 ? -2147483647 : p > 2147483647.0 ? 2147483647 : (int)p;
}
static nserror arc(const struct redraw_context *ctx, const plot_style_t *s, int x, int y, int radius, int a,
                   int b)
{
    if (radius <= 0)
        return NSERROR_OK;
    double start = a * (3.141592653589793 / 180.0);
    long sweep = ((long)b - a) % 360;
    if (sweep <= 0)
        sweep += 360;
    int steps = (int)imin(512, imax(1, (long)(sweep * sqrt(radius) / 18)));
    int px = coord(x + radius * cos(start)), py = coord(y - radius * sin(start));
    for (int i = 1; i <= steps; i++) {
        double angle = start + sweep * (3.141592653589793 / 180.0) * i / steps;
        int nx = coord(x + radius * cos(angle)), ny = coord(y - radius * sin(angle));
        segment(ctx->priv, s, px, py, nx, ny);
        px = nx;
        py = ny;
    }
    return NSERROR_OK;
}

static int compare_double(const void *left, const void *right)
{
    double a = *(const double *)left, b = *(const double *)right;
    return (a > b) - (a < b);
}

static nserror polygon(const struct redraw_context *ctx, const plot_style_t *s, const int *points, unsigned n)
{
    struct page_raster *r = ctx->priv;
    if (n < 3 || s->fill_type == PLOT_OP_TYPE_NONE || s->fill_colour == NS_TRANSPARENT)
        return NSERROR_OK;
    double local[16], *hits = n <= 16 ? local : malloc((size_t)n * sizeof(*hits));
    if (!hits)
        return NSERROR_NOMEM;
    int low = points[1], high = low;
    for (size_t i = 1; i < n; i++) {
        low = imin(low, points[2 * i + 1]);
        high = imax(high, points[2 * i + 1]);
    }
    for (long y = imax(low, r->clip.y0); y < imin(high, r->clip.y1); y++) {
        unsigned count = 0;
        for (size_t i = 0, j = n - 1; i < n; j = i++) {
            long y1 = points[j * 2 + 1], y2 = points[i * 2 + 1], x1 = points[j * 2], x2 = points[i * 2];
            if ((y1 > y) == (y2 > y))
                continue;
            hits[count++] = x1 + (y + 0.5 - y1) * (x2 - x1) / (y2 - y1);
        }
        qsort(hits, count, sizeof(*hits), compare_double);
        for (unsigned k = 1; k < count; k += 2)
            fill(r, (long)ceil(hits[k - 1]), y, (long)ceil(hits[k]), y + 1, page_rgb(s->fill_colour));
    }
    if (hits != local)
        free(hits);
    return NSERROR_OK;
}

/* No SVG content handler is enabled in this frontend. */
static nserror path(const struct redraw_context *ctx, const plot_style_t *s, const float *p, unsigned n,
                    const float tr[6])
{
    (void)ctx;
    (void)s;
    (void)p;
    (void)n;
    (void)tr;
    return NSERROR_NOT_IMPLEMENTED;
}

struct bitmap {
    int w, h;
    bool opaque;
    uint32_t pixels[];
};

static void *bitmap_create(int w, int h, enum gui_bitmap_flags flags)
{
    if (w <= 0 || h <= 0 || (uint64_t)w * h > 8u * 1024u * 1024u)
        return NULL;
    struct bitmap *b = calloc(1, sizeof(*b) + (size_t)w * h * 4);
    if (b) {
        b->w = w;
        b->h = h;
        b->opaque = (flags & BITMAP_OPAQUE) != 0;
    }
    return b;
}
static int bitmap_width(void *b)
{
    return ((struct bitmap *)b)->w;
}
static int bitmap_height(void *b)
{
    return ((struct bitmap *)b)->h;
}
static size_t stride(void *b)
{
    return (size_t)bitmap_width(b) * 4;
}
static unsigned char *buffer(void *b)
{
    return (unsigned char *)((struct bitmap *)b)->pixels;
}
static bool opaque(void *b)
{
    return ((struct bitmap *)b)->opaque;
}
static void set_opaque(void *b, bool op)
{
    ((struct bitmap *)b)->opaque = op;
}
static void modified(void *b)
{
    (void)b;
}

static nserror bitmap_plot(const struct redraw_context *ctx, struct bitmap *b, int x, int y, int w, int h,
                           colour bg, bitmap_flags_t flags)
{
    (void)bg;
    if (!b || w <= 0 || h <= 0)
        return NSERROR_OK;
    struct page_raster *r = ctx->priv;
    int left = r->clip.x0, right = r->clip.x1, top = r->clip.y0, bottom = r->clip.y1;
    if (!(flags & BITMAPF_REPEAT_X)) {
        left = imax(left, x);
        right = imin(right, (long)x + w);
    }
    if (!(flags & BITMAPF_REPEAT_Y)) {
        top = imax(top, y);
        bottom = imin(bottom, (long)y + h);
    }
    for (int py = top; py < bottom; py++) {
        long dy = (((long)py - y) % h + h) % h;
        const uint32_t *row = b->pixels + (dy * b->h / h) * b->w;
        for (int px = left; px < right; px++) {
            long dx = (((long)px - x) % w + w) % w;
            uint32_t c = row[dx * b->w / w];
            page_blend(r->pixels + (long)py * r->stride + px, c, b->opaque ? 255 : c >> 24);
        }
    }
    return NSERROR_OK;
}

static nserror bitmap_render(struct bitmap *b, struct hlcache_handle *content)
{
    struct page_raster r = {b->pixels, b->w, b->h, b->w, {0, 0, b->w, b->h}};
    struct redraw_context ctx = {.background_images = true, .plot = &zenith_plotters, .priv = &r};
    return content_scaled_redraw(content, b->w, b->h, &ctx) ? NSERROR_OK : NSERROR_INVALID;
}

struct gui_bitmap_table zenith_bitmaps = {.create = bitmap_create,
                                          .destroy = free,
                                          .set_opaque = set_opaque,
                                          .get_opaque = opaque,
                                          .get_buffer = buffer,
                                          .get_rowstride = stride,
                                          .get_width = bitmap_width,
                                          .get_height = bitmap_height,
                                          .modified = modified,
                                          .render = bitmap_render};
const struct plotter_table zenith_plotters = {.clip = clip,
                                              .arc = arc,
                                              .disc = disc,
                                              .line = line,
                                              .rectangle = rectangle,
                                              .polygon = polygon,
                                              .path = path,
                                              .bitmap = bitmap_plot,
                                              .text = zenith_plot_text};
