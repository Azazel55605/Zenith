/* Grid values shared by the CSS cascade and the NetSurf layout engine. */
#ifndef libcss_grid_h_
#define libcss_grid_h_
#include <libcss/computed.h>

enum { CSS_GRID_MAX_TRACKS = 128, CSS_GRID_AUTO_FIT = -1, CSS_GRID_AUTO_FILL = -2 };

enum css_grid_kind {
	CSS_GRID_AUTO, CSS_GRID_LENGTH, CSS_GRID_FR, CSS_GRID_MIN_CONTENT, CSS_GRID_MAX_CONTENT, CSS_GRID_FIT_CONTENT
};

typedef struct css_grid_breadth {
	enum css_grid_kind kind;
	css_fixed value;
	css_unit unit;
	uint32_t math_bytes; /* bytes of compiled math, zero for a simple breadth */
} css_grid_breadth;

typedef struct css_grid_track {
	css_grid_breadth min, max;
	int repeat;
	unsigned group_size;
	/* Sets inside a repeat occur on every copy. Outer sets occur only once. */
	uint32_t before, after, outer_before, outer_after;
} css_grid_track;

/* Fixed repeats have been expanded. For an auto-repeat the first track of
 * the pattern records AUTO_FIT/FILL and group_size. */
size_t css_grid_template(const css_computed_style *style, bool rows, bool implicit,
		const css_unit_ctx *units, int available, css_grid_track *out, size_t capacity);
enum { CSS_GRID_FLOW_COLUMN = 1, CSS_GRID_FLOW_DENSE = 2 };
unsigned css_grid_auto_flow(const css_computed_style *style);
int css_grid_area_extent(const css_computed_style *style, bool rows);
typedef struct css_grid_line {
	int number;
	bool span;
	const char *name;
	size_t length;
} css_grid_line;
css_grid_line css_grid_placement(const css_computed_style *item, bool rows, bool end);
int css_grid_area_edge(const css_computed_style *style, bool rows, bool end, const char *name, size_t len);
const char *css_grid_line_names(const css_computed_style *style, bool rows);
bool css_grid_set_has(const char *names, uint32_t set, const char *name, size_t len, const char *suffix);
uint8_t css_computed_justify_items(const css_computed_style *style);
uint8_t css_computed_justify_self(const css_computed_style *style);
uint8_t css_computed_row_gap(const css_computed_style *style, css_fixed *length, css_unit *unit);
#endif
