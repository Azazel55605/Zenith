/* Grid track lists and placements are parsed once and interned in the
 * stylesheet. Computed styles share these immutable values across elements. */
#include <string.h>
#include <libcss/grid.h>
#include <libcss/math.h>
#include "grid_values.h"
#include "bytecode/bytecode.h"
#include "select/propset.h"
#include "select/propget.h"
#include "select/properties/properties.h"
#include "select/properties/helpers.h"

typedef css_error (*grid_setter)(css_computed_style *, uint8_t, lwc_string *);
typedef uint8_t (*grid_getter)(const css_computed_style *, lwc_string **);

static css_error cascade_grid(uint32_t opv, css_style *style,
		css_select_state *state, grid_setter set)
{
	lwc_string *data = NULL;
	uint8_t specified = !hasFlagValue(opv);
	if (specified && getValue(opv) != 0) {
		css_error err = css__stylesheet_string_get(style->sheet, *style->bytecode, &data);
		if (err != CSS_OK) return err;
		advance_bytecode(style, sizeof(css_code_t));
	}
	if (css__outranks_existing(getOpcode(opv), isImportant(opv), state, getFlagValue(opv)))
		return set(state->computed, specified, data);
	return CSS_OK;
}

static css_error copy_grid(const css_computed_style *from, css_computed_style *to,
		grid_getter get, grid_setter set)
{
	lwc_string *data = NULL;
	uint8_t type = get(from, &data);
	return from == to ? CSS_OK : set(to, type, data);
}

/* LibCSS dispatches inheritance, hinting and initialisation per longhand. */
#define GRID_PROPERTY(name) \
css_error css__cascade_##name(uint32_t opv, css_style *s, css_select_state *state) \
{ return cascade_grid(opv, s, state, set_##name); } \
css_error css__initial_##name(css_select_state *state) \
{ return set_##name(state->computed, 1, NULL); } \
css_error css__set_##name##_from_hint(const css_hint *hint, css_computed_style *s) \
{ (void)hint; return set_##name(s, 1, NULL); } \
css_error css__copy_##name(const css_computed_style *from, css_computed_style *to) \
{ return copy_grid(from, to, get_##name, set_##name); } \
css_error css__compose_##name(const css_computed_style *parent, const css_computed_style *child, css_computed_style *result) \
{ lwc_string *data; return css__copy_##name(get_##name(child, &data) ? child : parent, result); }

GRID_PROPERTY(grid_template_columns)
GRID_PROPERTY(grid_template_rows)
GRID_PROPERTY(grid_column_start)
GRID_PROPERTY(grid_column_end)
GRID_PROPERTY(grid_row_start)
GRID_PROPERTY(grid_row_end)

GRID_PROPERTY(grid_auto_columns)
GRID_PROPERTY(grid_auto_rows)
GRID_PROPERTY(grid_template_areas)
GRID_PROPERTY(grid_auto_flow)

size_t css_grid_template(const css_computed_style *style, bool rows, bool implicit,
		const css_unit_ctx *units, int available, css_grid_track *out, size_t capacity)
{
	grid_getter get = implicit ? (rows ? get_grid_auto_rows : get_grid_auto_columns) :
			(rows ? get_grid_template_rows : get_grid_template_columns);
	lwc_string *data = NULL;
	get(style, &data);
	if (!data) return 0;
	uint32_t n;
	const char *bytes = lwc_string_data(data);
	memcpy(&n, bytes, sizeof(n));
	size_t count = n > capacity ? capacity : n;
	if (count) memcpy(out, bytes + sizeof(n), count * sizeof(*out));
	for (size_t i = 0; i < count; i++) {
		css_grid_breadth *both[] = {&out[i].min, &out[i].max};
		for (size_t j = 0; j < 2; j++) {
			css_grid_breadth *b = both[j];
			if (!b->math_bytes) continue;
			int pixels;
			if (css_math_program_px(bytes + b->value, b->math_bytes, style, units, available, &pixels)) {
				b->value = pixels; /* CSS_UNIT_CALC now carries the used device-pixel value. */
			} else {
				/* An unresolved percentage participates in intrinsic sizing
				 * like any other indefinite percentage track. */
				b->unit = CSS_UNIT_PCT;
				b->value = 0;
			}
		}
	}
	return count;
}

unsigned css_grid_auto_flow(const css_computed_style *style)
{
	lwc_string *data = NULL;
	get_grid_auto_flow(style, &data);
	return data ? (unsigned char)lwc_string_data(data)[0] : 0;
}

int css_grid_area_extent(const css_computed_style *style, bool rows)
{
	lwc_string *data = NULL;
	struct grid_areas_header head;
	get_grid_template_areas(style, &data);
	if (!data) return 0;
	memcpy(&head, lwc_string_data(data), sizeof(head));
	return rows ? head.rows : head.columns;
}

const char *css_grid_line_names(const css_computed_style *style, bool rows)
{
	lwc_string *data = NULL;
	(rows ? get_grid_template_rows : get_grid_template_columns)(style, &data);
	return data ? lwc_string_data(data) : NULL;
}

bool css_grid_set_has(const char *names, uint32_t set, const char *name, size_t len, const char *suffix)
{
	if (!set || !names) return false;
	const char *p = names + set;
	uint32_t bytes;
	memcpy(&bytes, p, sizeof(bytes));
	p += sizeof(bytes);
	const char *end = p + bytes;
	size_t tail = strlen(suffix);
	while (p < end) {
		uint32_t n;
		memcpy(&n, p, sizeof(n));
		p += sizeof(n);
		if (n >= tail && n-tail == len && !memcmp(p, name, len) && !memcmp(p+len, suffix, tail)) return true;
		p += n;
	}
	return false;
}

css_grid_line css_grid_placement(const css_computed_style *item, bool rows, bool end)
{
	lwc_string *line = NULL;
	grid_getter get = rows ? (end ? get_grid_row_end : get_grid_row_start)
			       : (end ? get_grid_column_end : get_grid_column_start);
	get(item, &line);
	if (!line) return (css_grid_line){0};
	struct grid_line_value value;
	memcpy(&value, lwc_string_data(line), sizeof(value));
	size_t length = lwc_string_length(line) - sizeof(value);
	return (css_grid_line){.number = value.number, .span = value.span != 0, .length = length,
		.name = length ? lwc_string_data(line) + sizeof(value) : NULL};
}

int css_grid_area_edge(const css_computed_style *style, bool rows, bool end, const char *name, size_t name_len)
{
	lwc_string *areas = NULL;
	get_grid_template_areas(style, &areas);
	if (!areas) return 0;
	const char *p = lwc_string_data(areas) + sizeof(struct grid_areas_header);
	const char *limit = lwc_string_data(areas) + lwc_string_length(areas);
	while (p < limit) {
		struct grid_named_area area;
		memcpy(&area, p, sizeof(area));
		p += sizeof(area);
		if (area.length == name_len && memcmp(p, name, name_len) == 0)
			return rows ? (end ? area.row_end : area.row_start) :
				      (end ? area.col_end : area.col_start);
		p += area.length;
	}
	return 0;
}

uint8_t css_computed_row_gap(const css_computed_style *style, css_fixed *length, css_unit *unit)
{
	return get_row_gap(style, length, unit);
}
