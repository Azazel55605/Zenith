#ifndef NEBULA_SOURCE_VIEW_H
#define NEBULA_SOURCE_VIEW_H

#include "frontend.h"
#include "../ui/ui.h"

/* The completed document owns the bytes. The view owns a sparse index for wrapped rows. */
struct source_view {
    const uint8_t *bytes;
    size_t length, *checkpoints, capacity;
    int columns, rows, first;
};

void source_close(struct source_view *v);
bool source_layout(struct source_view *v, int width);
int source_page_rows(int height);
void source_scroll(struct source_view *v, long target, int height);
void source_paint(struct source_view *v, struct page_raster *raster, const struct zui_theme *theme);

#endif
