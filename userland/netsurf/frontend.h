#ifndef ZENITH_NETSURF_FRONTEND_H
#define ZENITH_NETSURF_FRONTEND_H

#include "utils/errors.h"
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "netsurf/types.h"
#include "netsurf/plot_style.h"
#include "netsurf/plotters.h"

/* Coordinates passed by the core are relative to the web viewport, including
 * for child frames. Chrome is outside this raster's addressable rectangle. */
struct page_raster {
    uint32_t *pixels;
    int width, height, stride;
    struct rect clip;
};

static inline uint32_t page_rgb(colour c)
{
    return 0xff000000u | ((c & 255) << 16) | (c & 0xff00) | ((c >> 16) & 255);
}

static inline void page_blend(uint32_t *dst, uint32_t rgb, unsigned alpha)
{
    if (alpha == 255) {
        *dst = rgb | 0xff000000u;
        return;
    }
    unsigned rest = 255 - alpha;
    unsigned r = (((rgb >> 16) & 255) * alpha + ((*dst >> 16) & 255) * rest + 127) / 255;
    unsigned g = (((rgb >> 8) & 255) * alpha + ((*dst >> 8) & 255) * rest + 127) / 255;
    unsigned b = ((rgb & 255) * alpha + (*dst & 255) * rest + 127) / 255;
    *dst = 0xff000000u | (r << 16) | (g << 8) | b;
}

extern const struct plotter_table zenith_plotters;
extern struct gui_layout_table zenith_layout;
extern struct gui_bitmap_table zenith_bitmaps;
nserror zenith_plot_text(const struct redraw_context *, const plot_font_style_t *, int, int, const char *,
                         size_t);
int zenith_browser_run(const char *url);
#endif
