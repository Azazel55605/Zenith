/* Display the cached response as inert, monospaced text. Only visible rows are rasterized. */
#include "source-view.h"
#include <parserutils/charset/utf8.h>
#include <stdlib.h>
#include <string.h>

enum { MARGIN = 12, BODY_TOP = 40, MAX_COLUMNS = 1024, CHECKPOINT_ROWS = 64 };

void source_close(struct source_view *v)
{
    free(v->checkpoints);
    *v = (struct source_view){0};
}

int source_page_rows(int height)
{
    int rows = (height - BODY_TOP - MARGIN) / (zui_mono.height + 2);
    return rows > 0 ? rows : 1;
}

void source_scroll(struct source_view *v, long target, int height)
{
    int last = v->rows - source_page_rows(height);
    v->first = last <= 0 || target <= 0 ? 0 : target > last ? last : (int)target;
}

/* Indexing and drawing share exactly the same CR/LF, tab and wrap boundaries.
 * Never pass malformed input to the font decoder. Embedded NULs are visible replacements. */
static size_t read_row(const struct source_view *v, size_t pos, char *line, size_t *length)
{
    size_t out = 0;
    int column = 0;
    while (pos < v->length) {
        unsigned c = v->bytes[pos];
        if (c == '\n' || c == '\r') {
            pos++;
            if (c == '\r' && pos < v->length && v->bytes[pos] == '\n') pos++;
            break;
        }
        if (column == v->columns) break;
        if (c == '\t') {
            int spaces = 4 - column % 4;
            if (spaces > v->columns - column) spaces = v->columns - column;
            if (line) memset(line + out, ' ', spaces);
            out += spaces;
            column += spaces;
            pos++;
            continue;
        }
        size_t n = 1;
        bool valid = c >= 32 && c != 127;
        if (c >= 128) {
            uint32_t cp;
            valid = parserutils_charset_utf8_to_ucs4(v->bytes + pos, v->length - pos, &cp, &n) == PARSERUTILS_OK
                    && n <= 4 && cp <= 0x10ffff && !(cp >= 0xd800 && cp <= 0xdfff);
            if (!valid) n = 1;
        }
        if (line) {
            if (valid) memcpy(line + out, v->bytes + pos, n);
            else line[out] = '?';
        }
        pos += n;
        out += n;
        column++;
    }
    if (length) *length = out;
    return pos;
}

bool source_layout(struct source_view *v, int width)
{
    int columns = (width - 2 * MARGIN) / zui_mono.advance[0];
    if (columns < 1) columns = 1;
    if (columns > MAX_COLUMNS) columns = MAX_COLUMNS;
    if (v->columns == columns) return true;
    v->columns = columns;
    v->rows = 0;
    size_t pos = 0;
    do {
        if (v->rows % CHECKPOINT_ROWS == 0) {
            size_t slot = (size_t)v->rows / CHECKPOINT_ROWS;
            if (slot >= v->capacity) {
                size_t capacity = v->capacity ? 2 * v->capacity : 8;
                size_t *grown = realloc(v->checkpoints, capacity * sizeof(*grown));
                if (!grown) return false;
                v->checkpoints = grown;
                v->capacity = capacity;
            }
            v->checkpoints[slot] = pos;
        }
        v->rows++;
        pos = read_row(v, pos, NULL, NULL);
    } while (pos < v->length);
    return true;
}

void source_paint(struct source_view *v, struct page_raster *r, const struct zui_theme *theme)
{
    for (int y = 0; y < r->height; y++)
        for (int x = 0; x < r->width; x++)
            r->pixels[(size_t)y * r->stride + x] = 0xff000000u | theme->surface;
    struct redraw_context ctx = {.priv = r};
    plot_font_style_t style = {.family = PLOT_FONT_FAMILY_SANS_SERIF, .weight = 600,
                               .size = 21 * PLOT_STYLE_SCALE / 2, .foreground = nscss_color_to_ns(theme->text) & 0xffffff};
    zenith_plot_text(&ctx, &style, MARGIN, 8 + zui_label.ascent, "Page source", 11);
    const char hint[] = "Esc to return";
    int hint_x = r->width - MARGIN - zui_measure(hint, sizeof(hint) - 1, &zui_body);
    if (hint_x > 120) {
        style.weight = 400;
        style.foreground = nscss_color_to_ns(theme->secondary) & 0xffffff;
        zenith_plot_text(&ctx, &style, hint_x, 8 + zui_body.ascent, hint, sizeof(hint) - 1);
    }
    source_scroll(v, v->first, r->height);
    int base = v->first / CHECKPOINT_ROWS;
    size_t pos = v->checkpoints[base];
    for (int row = base * CHECKPOINT_ROWS; row < v->first; row++)
        pos = read_row(v, pos, NULL, NULL);
    style.family = PLOT_FONT_FAMILY_MONOSPACE;
    style.weight = 400;
    style.foreground = nscss_color_to_ns(theme->text) & 0xffffff;
    char line[MAX_COLUMNS * 4];
    for (int y = BODY_TOP; y < r->height && pos < v->length; y += zui_mono.height + 2) {
        size_t bytes;
        pos = read_row(v, pos, line, &bytes);
        zenith_plot_text(&ctx, &style, MARGIN, y + zui_mono.ascent, line, bytes);
    }
}
