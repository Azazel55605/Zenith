/* CSS Grid track sizing and item placement for NetSurf. SPDX-License-Identifier: GPL-2.0-only */
#include <limits.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <libcss/grid.h>

#include "utils/utils.h"
#include "html/box.h"
#include "html/html.h"
#include "html/private.h"
#include "html/box_inspect.h"
#include "html/layout_internal.h"

/* Explicit addresses are bounded independently of ordinary automatic rows. A long
 * page can continue downwards without allocating a dense two-dimensional array. */
enum { GRID_COLS = CSS_GRID_MAX_TRACKS * 2, GRID_PX_MAX = INT_MAX / 16 };

struct track {
	css_grid_breadth min_size, max_size;
	int size, target, start;
	uint32_t before, after, outer_before, outer_after;
	bool auto_fit, occupied, frozen;
};

struct axis {
	struct track *tracks;
	css_grid_track *implicit;
	const char *names;
	int implicit_count, template_start, template_end;
	int count, capacity, available, gap;
	enum css_align_content_e alignment;
};

struct area {
	int start, span;
};

struct item {
	struct box *box;
	int order, source_index;
	struct area col, row;
	int natural_height, min_height, max_height;
	bool height_auto, percent_height;
	css_fixed height_pct;
};

struct cell_row {
	int locked_cursor;
	uint64_t bits[(GRID_COLS + 63) / 64];
};

struct placement {
	struct cell_row *rows;
	int capacity;
};

static int px(int64_t size)
{
	return size <= 0 ? 0 : size > GRID_PX_MAX ? GRID_PX_MAX : (int)size;
}

static int clamped_size(int size, int min_size, int max_size)
{
	if (max_size >= 0 && size > max_size) size = max_size;
	return max(max(0, min_size), size);
}

static int pixels(const struct box *grid, const css_unit_ctx *units,
		css_fixed value, css_unit unit, int reference)
{
	if (unit == CSS_UNIT_CALC) return px(value); /* resolved by css_grid_template */
	if (unit == CSS_UNIT_PCT)
		return reference < 0 ? 0 : px(((int64_t)value * reference + F_100 / 2) / F_100);
	return px(FIXTOINT(FADD(css_unit_len2device_px(grid->style, units, value, unit), F_0_5)));
}

static struct track new_track(css_grid_breadth minimum, css_grid_breadth maximum, bool fit)
{
	return (struct track){ .min_size = minimum, .max_size = maximum, .auto_fit = fit };
}

static struct track implicit_track(const struct axis *axis, int index)
{
	if (axis->implicit_count) {
		int anchor = index < axis->template_start ? axis->template_start : axis->template_end;
		int offset = (index - anchor) % axis->implicit_count;
		if (offset < 0) offset += axis->implicit_count;
		const css_grid_track *t = &axis->implicit[offset];
		return new_track(t->min, t->max, false);
	}
	const css_grid_breadth automatic = { .kind = CSS_GRID_AUTO };
	return new_track(automatic, automatic, false);
}

static bool ensure_tracks(struct axis *axis, int count)
{
	if (count > axis->capacity) {
		int capacity = max(count, axis->capacity > INT_MAX / 2 ? count : axis->capacity * 2);
		struct track *tracks = realloc(axis->tracks, (size_t)capacity * sizeof(*tracks));
		if (!tracks) return false;
		axis->tracks = tracks;
		axis->capacity = capacity;
	}
	while (axis->count < count) {
		axis->tracks[axis->count] = implicit_track(axis, axis->count);
		axis->count++;
	}
	return true;
}

/* Definite maxima determine the auto-repeat count; otherwise use definite minima.
 * Floor repeated track sizes at one device pixel to handle repeat(auto-fill, 0). */
static int repeat_size(const struct box *grid, const css_unit_ctx *units,
		const css_grid_track *t, int reference)
{
	int minimum = t->min.kind == CSS_GRID_LENGTH ?
			pixels(grid, units, t->min.value, t->min.unit, reference) : 0;
	int maximum = t->max.kind == CSS_GRID_LENGTH ?
			pixels(grid, units, t->max.value, t->max.unit, reference) : 0;
	return max(minimum, maximum);
}

/* Use the same distribution calculation for either axis. */
static enum css_align_content_e content_alignment(const css_computed_style *style, bool rows)
{
	if (rows) return css_computed_align_content(style);
	switch (css_computed_justify_content(style)) {
	case CSS_JUSTIFY_CONTENT_FLEX_START: return CSS_ALIGN_CONTENT_FLEX_START;
	case CSS_JUSTIFY_CONTENT_CENTER: return CSS_ALIGN_CONTENT_CENTER;
	case CSS_JUSTIFY_CONTENT_FLEX_END: return CSS_ALIGN_CONTENT_FLEX_END;
	case CSS_JUSTIFY_CONTENT_SPACE_BETWEEN: return CSS_ALIGN_CONTENT_SPACE_BETWEEN;
	case CSS_JUSTIFY_CONTENT_SPACE_AROUND: return CSS_ALIGN_CONTENT_SPACE_AROUND;
	case CSS_JUSTIFY_CONTENT_SPACE_EVENLY: return CSS_ALIGN_CONTENT_SPACE_EVENLY;
	default: return CSS_ALIGN_CONTENT_STRETCH;
	}
}

static bool init_axis(struct axis *axis, const struct box *grid, const css_unit_ctx *units,
		bool rows, int available)
{
	css_grid_track specs[CSS_GRID_MAX_TRACKS];
	css_fixed gap;
	css_unit unit;
	uint8_t type = rows ? css_computed_row_gap(grid->style, &gap, &unit) :
			css_computed_column_gap(grid->style, &gap, &unit);
	axis->available = available;
	axis->names = css_grid_line_names(grid->style, rows);
	axis->alignment = content_alignment(grid->style, rows);
	axis->gap = type == CSS_COLUMN_GAP_SET ? pixels(grid, units, gap, unit, available) : 0;
	int count = (int)css_grid_template(grid->style, rows, false, units, available, specs, CSS_GRID_MAX_TRACKS);
	if (count < 0 || count > CSS_GRID_MAX_TRACKS) return false;
	int64_t once = 0;
	for (int i = 0; i < count;) {
		int group = specs[i].repeat < 0 ? max(1, (int)specs[i].group_size) : 1;
		if (group > count - i) return false;
		for (int j = 0; j < group; j++) {
			int size = repeat_size(grid, units, &specs[i+j], available);
			once += (specs[i].repeat < 0 ? max(size, 1) : size) + axis->gap;
		}
		i += group;
	}
	for (int i = 0; i < count;) {
		bool automatic = specs[i].repeat == CSS_GRID_AUTO_FIT || specs[i].repeat == CSS_GRID_AUTO_FILL;
		int group = automatic ? max(1, (int)specs[i].group_size) : 1;
		if (group > count - i) return false;
		int times = 1;
		if (automatic && available >= 0) {
			int64_t step = 0;
			for (int j = 0; j < group; j++) {
				int size = repeat_size(grid, units, &specs[i+j], available);
				step += max(1, size) + axis->gap;
			}
			int64_t spare = (int64_t)available + axis->gap - once;
			if (spare > 0)
				times += min(spare / step, (CSS_GRID_MAX_TRACKS - count) / group);
		}
		int first = axis->count;
		if (!ensure_tracks(axis, first + group * times)) return false;
		for (int j = 0; j < group * times; j++) {
			const css_grid_track *t = &specs[i + j % group];
			struct track *dest = &axis->tracks[first+j];
			*dest = new_track(t->min, t->max, specs[i].repeat == CSS_GRID_AUTO_FIT);
			dest->before = t->before;
			dest->after = t->after;
			if (j < group) dest->outer_before = t->outer_before;
			if (j >= group * (times - 1)) dest->outer_after = t->outer_after;
		}
		i += group;
	}
	axis->template_end = axis->count;
	int nimplicit = (int)css_grid_template(grid->style, rows, true, units, available, specs, CSS_GRID_MAX_TRACKS);
	if (nimplicit > 0) {
		axis->implicit = malloc((size_t)nimplicit * sizeof(*axis->implicit));
		if (!axis->implicit) return false;
		memcpy(axis->implicit, specs, (size_t)nimplicit * sizeof(*axis->implicit));
		axis->implicit_count = nimplicit;
	}
	return ensure_tracks(axis, css_grid_area_extent(grid->style, rows));
}

static int line_position(int line, int explicit_tracks)
{
	if (line > 0) return min(line - 1, CSS_GRID_MAX_TRACKS);
	return explicit_tracks + 1 + max(line, -CSS_GRID_MAX_TRACKS);
}

static bool named_line(const struct axis *axis, int line, const css_grid_line *spec, const char *suffix)
{
	if (line > 0) {
		const struct track *t = &axis->tracks[line-1];
		if (css_grid_set_has(axis->names, t->after, spec->name, spec->length, suffix) ||
		    css_grid_set_has(axis->names, t->outer_after, spec->name, spec->length, suffix)) return true;
	}
	if (line < axis->count) {
		const struct track *t = &axis->tracks[line];
		if (css_grid_set_has(axis->names, t->before, spec->name, spec->length, suffix) ||
		    css_grid_set_has(axis->names, t->outer_before, spec->name, spec->length, suffix)) return true;
	}
	return false;
}

/* Area declarations implicitly name each edge AREA-start or AREA-end. */
static int implicit_name(const css_computed_style *parent, bool rows, const css_grid_line *spec)
{
	size_t n = spec->length;
	if (n > 6 && !memcmp(spec->name+n-6, "-start", 6))
		return css_grid_area_edge(parent, rows, false, spec->name, n-6) - 1;
	if (n > 4 && !memcmp(spec->name+n-4, "-end", 4))
		return css_grid_area_edge(parent, rows, true, spec->name, n-4) - 1;
	return -1;
}

static int named_occurrence(const css_computed_style *parent, bool rows, const struct axis *axis,
		const css_grid_line *spec, int from, int step)
{
	int n = spec->number ? min(abs(spec->number), CSS_GRID_MAX_TRACKS) : 1;
	int area_edge = implicit_name(parent, rows, spec);
	for (int line = from;; line += step) {
		/* Implicit lines beyond the explicit grid count as matching. */
		if (line < 0 || line > axis->count || line == area_edge || named_line(axis, line, spec, ""))
			if (!--n) return line;
	}
}

static int explicit_line(const css_computed_style *parent, bool rows, bool end, const struct axis *axis,
		const css_grid_line *line)
{
	if (line->span || (!line->number && !line->name)) return AUTO;
	if (!line->name) return line_position(line->number, axis->count);
	if (!line->number) {
		const char *suffix = end ? "-end" : "-start";
		int area_edge = css_grid_area_edge(parent, rows, end, line->name, line->length) - 1;
		for (int i = 0; i <= axis->count; i++)
			if (i == area_edge || named_line(axis, i, line, suffix)) return i;
	}
	int step = line->number < 0 ? -1 : 1;
	return named_occurrence(parent, rows, axis, line, step < 0 ? axis->count : 0, step);
}

static struct area placement_for(const css_computed_style *parent, const css_computed_style *style,
		bool rows, const struct axis *axis)
{
	css_grid_line begin = css_grid_placement(style, rows, false);
	css_grid_line end = css_grid_placement(style, rows, true);
	int first = explicit_line(parent, rows, false, axis, &begin);
	int last = explicit_line(parent, rows, true, axis, &end);
	if (first != AUTO && last != AUTO)
		return (struct area){ min(first, last), max(1, abs(last - first)) };
	const css_grid_line *s = begin.span ? &begin : &end;
	int span = s->span && !s->name ? max(1, min(s->number, CSS_GRID_MAX_TRACKS)) : 1;
	if (s->span && s->name && (first != AUTO || last != AUTO)) {
		int dir = first != AUTO ? 1 : -1, anchor = first != AUTO ? first : last;
		int boundary = named_occurrence(parent, rows, axis, s, anchor+dir, dir);
		span = abs(boundary - anchor);
	}
	return (struct area){ first != AUTO ? first : last != AUTO ? last - span : AUTO, span };
}

static bool shift_axis(struct axis *axis, int before)
{
	if (!before) return true;
	int explicit_tracks = axis->count;
	if (!ensure_tracks(axis, before + explicit_tracks)) return false;
	memmove(axis->tracks + before, axis->tracks, (size_t)explicit_tracks * sizeof(*axis->tracks));
	axis->template_start += before;
	axis->template_end += before;
	for (int i = 0; i < before; i++)
		axis->tracks[i] = implicit_track(axis, i);
	return true;
}

static bool free_cells(const struct placement *p, int col, int row, int width, int height)
{
	for (int r = row; r < p->capacity && r < row + height; r++)
		for (int c = col; c < col + width; c++)
			if (p->rows[r].bits[c / 64] & (UINT64_C(1) << (c % 64)))
				return false;
	return true;
}

static bool place_item(struct placement *p, struct item *item, struct axis *cols,
		struct axis *rows, int col, int row)
{
	int last_col = col + item->col.span;
	int last_row = row + item->row.span;
	if (!ensure_tracks(cols, last_col) || !ensure_tracks(rows, last_row)) return false;
	if (last_row > p->capacity) {
		int capacity = max(last_row, p->capacity > INT_MAX / 2 ? last_row : 2 * p->capacity);
		struct cell_row *more = realloc(p->rows, (size_t)capacity * sizeof(*more));
		if (!more) return false;
		memset(more + p->capacity, 0, (size_t)(capacity - p->capacity) * sizeof(*more));
		p->rows = more;
		p->capacity = capacity;
	}
	for (int r = row; r < last_row; r++) {
		rows->tracks[r].occupied = true;
		for (int c = col; c < last_col; c++) {
			cols->tracks[c].occupied = true;
			p->rows[r].bits[c / 64] |= UINT64_C(1) << (c % 64);
		}
	}
	item->col.start = col;
	item->row.start = row;
	return true;
}

static void transpose(struct item *item)
{
	struct area tmp = item->col;
	item->col = item->row;
	item->row = tmp;
}

/* Reserve definite areas, place items locked to a row, then scan for the rest.
 * Column flow transposes both axes. Fully explicit items may overlap. */
static bool position_items(struct placement *p, struct axis *cols, struct axis *rows,
		struct item *items, int count, const css_computed_style *style)
{
	unsigned flow = css_grid_auto_flow(style);
	bool column_flow = (flow & CSS_GRID_FLOW_COLUMN) != 0;
	bool dense = (flow & CSS_GRID_FLOW_DENSE) != 0;
	for (int i = 0; i < count; i++) {
		struct item *item = &items[i];
		item->col = placement_for(style, item->box->style, false, cols);
		item->row = placement_for(style, item->box->style, true, rows);
		if (column_flow) transpose(item);
	}
	if (column_flow) {
		struct axis *tmp = cols;
		cols = rows;
		rows = tmp;
	}
	int before_col = 0, before_row = 0;
	for (int i = 0; i < count; i++) {
		struct item *item = &items[i];
		if (item->col.start != AUTO) before_col = max(before_col, -item->col.start);
		if (item->row.start != AUTO) before_row = max(before_row, -item->row.start);
	}
	before_col = min(before_col, GRID_COLS - cols->count);
	if (!shift_axis(cols, before_col) || !shift_axis(rows, before_row)) return false;
	int needed_cols = max(1, cols->count);
	for (int i = 0; i < count; i++) {
		struct item *item = &items[i];
		item->col.span = min(item->col.span, GRID_COLS);
		if (item->col.start != AUTO) {
			item->col.start = max(0, min(item->col.start + before_col, GRID_COLS - item->col.span));
			needed_cols = max(needed_cols, item->col.start + item->col.span);
		}
		needed_cols = max(needed_cols, item->col.span);
		if (item->row.start != AUTO) {
			item->row.start += before_row;
			if (item->col.start != AUTO &&
			    !place_item(p, item, cols, rows, item->col.start, item->row.start)) return false;
		}
	}
	if (!ensure_tracks(cols, needed_cols)) return false;
	for (int i = 0; i < count; i++) {
		struct item *item = &items[i];
		if (item->row.start == AUTO || item->col.start != AUTO) continue;
		int col = !dense && item->row.start < p->capacity ? p->rows[item->row.start].locked_cursor : 0;
		col = min(col, GRID_COLS - item->col.span);
		while (col < GRID_COLS - item->col.span &&
		       !free_cells(p, col, item->row.start, item->col.span, item->row.span)) col++;
		if (!place_item(p, item, cols, rows, col, item->row.start)) return false;
		p->rows[item->row.start].locked_cursor = col + item->col.span;
	}
	int x = 0, y = 0, single_x = 0, single_y = 0;
	for (int i = 0; i < count; i++) {
		struct item *item = &items[i];
		if (item->row.start != AUTO) continue;
		bool single = item->col.start == AUTO && item->col.span == 1 && item->row.span == 1;
		if (dense) {
			/* Cells before the first one-wide hole stay occupied for the rest of placement. */
			x = single ? single_x : 0;
			y = single ? single_y : 0;
		}
		if (item->col.start != AUTO) {
			if (item->col.start < x) y++;
			x = item->col.start;
			while (!free_cells(p, x, y, item->col.span, item->row.span)) y++;
		} else {
			while (true) {
				if (x + item->col.span > cols->count) { x = 0; y++; }
				if (free_cells(p, x, y, item->col.span, item->row.span)) break;
				x++;
			}
		}
		if (!place_item(p, item, cols, rows, x, y)) return false;
		x += item->col.span;
		if (dense && single) { single_x = x; single_y = y; }
	}
	if (column_flow) {
		for (int i = 0; i < count; i++) transpose(&items[i]);
	}
	return true;
}

static bool collapsed(const struct track *t)
{
	return t->auto_fit && !t->occupied;
}

static bool intrinsic(const css_grid_breadth *b, int available)
{
	return b->kind == CSS_GRID_AUTO || b->kind == CSS_GRID_MIN_CONTENT ||
	       b->kind == CSS_GRID_MAX_CONTENT || b->kind == CSS_GRID_FIT_CONTENT ||
	       (b->kind == CSS_GRID_LENGTH && b->unit == CSS_UNIT_PCT && available < 0);
}

/* Grow intrinsic tracks to fit the item's contribution. Fixed tracks and
 * minmax(0, 1fr) retain their authored minimum even for overflowing content. */
static void contribute(struct axis *axis, struct area area, int min_content, int max_content, bool preferred)
{
	int64_t used = (int64_t)(area.span - 1) * axis->gap;
	int eligible = 0, contribution = 0;
	for (int i = area.start; i < area.start + area.span; i++) {
		struct track *t = &axis->tracks[i];
		const css_grid_breadth *breadth = preferred ? &t->max_size : &t->min_size;
		used += preferred ? t->target : t->size;
		if (intrinsic(breadth, axis->available) || (preferred && breadth->kind == CSS_GRID_FR)) {
			int need = breadth->kind == CSS_GRID_MAX_CONTENT ||
					(preferred && breadth->kind != CSS_GRID_MIN_CONTENT) ? max_content : min_content;
			contribution = max(contribution, need);
			eligible++;
		}
	}
	int remaining = px((int64_t)contribution - used);
	for (int i = area.start; i < area.start + area.span && remaining > 0; i++) {
		struct track *t = &axis->tracks[i];
		const css_grid_breadth *breadth = preferred ? &t->max_size : &t->min_size;
		if (!intrinsic(breadth, axis->available) && !(preferred && breadth->kind == CSS_GRID_FR)) continue;
		int share = remaining / eligible--;
		*(preferred ? &t->target : &t->size) += share;
		remaining -= share;
	}
}

static int64_t sizes_and_gutters(const struct axis *axis)
{
	int64_t sum = 0;
	int visible = 0;
	for (int i = 0; i < axis->count; i++) {
		const struct track *t = &axis->tracks[i];
		if (collapsed(t)) continue;
		sum += t->size;
		visible++;
	}
	return sum + (int64_t)max(0, visible - 1) * axis->gap;
}

static void allocate_tracks(struct axis *axis)
{
	int room = axis->available < 0 ? GRID_PX_MAX : px((int64_t)axis->available - sizes_and_gutters(axis));
	/* Maximize non-flex tracks equally, stopping at individual growth limits. */
	while (room > 0) {
		int eligible = 0;
		for (int i = 0; i < axis->count; i++) {
			struct track *t = &axis->tracks[i];
			if (!collapsed(t) && t->max_size.kind != CSS_GRID_FR && t->target > t->size) eligible++;
		}
		if (!eligible) break;
		int round_room = room;
		for (int i = 0; i < axis->count && round_room; i++) {
			struct track *t = &axis->tracks[i];
			if (collapsed(t) || t->max_size.kind == CSS_GRID_FR || t->target <= t->size) continue;
			int share = round_room / eligible--;
			int growth = min(share, t->target - t->size);
			t->size += growth;
			room -= growth;
			round_room -= share;
		}
	}
	int64_t sum_fr = 0;
	int64_t wanted_fr = 0;
	int64_t non_flex = sizes_and_gutters(axis);
	int stretch = 0;
	for (int i = 0; i < axis->count; i++) {
		struct track *t = &axis->tracks[i];
		if (collapsed(t)) continue;
		if (axis->alignment == CSS_ALIGN_CONTENT_STRETCH && t->max_size.kind == CSS_GRID_AUTO) stretch++;
		if (t->max_size.kind == CSS_GRID_FR && t->max_size.value > 0) {
			sum_fr += t->max_size.value;
			non_flex -= t->size;
			int64_t factor = max(F_1, t->max_size.value);
			int64_t needed = ((int64_t)max(t->target, t->size) * F_1 + factor - 1) / factor;
			wanted_fr = max(wanted_fr, needed);
		}
	}
	if (axis->available < 0) {
		for (int i = 0; i < axis->count; i++) {
			struct track *t = &axis->tracks[i];
			if (!collapsed(t) && t->max_size.kind == CSS_GRID_FR)
				t->size = max(t->size, px(wanted_fr * t->max_size.value / F_1));
		}
		return;
	}
	/* A track whose minimum exceeds its proposed fraction freezes at that minimum. */
	int remaining = px((int64_t)axis->available - non_flex);
	bool changed;
	do {
		changed = false;
		int64_t divisor = max((int64_t)F_1, sum_fr);
		for (int i = 0; i < axis->count; i++) {
			struct track *t = &axis->tracks[i];
			if (collapsed(t) || t->frozen || t->max_size.kind != CSS_GRID_FR || !t->max_size.value) continue;
			if ((int64_t)remaining * t->max_size.value / divisor < t->size) {
				t->frozen = true;
				remaining = max(0, remaining - t->size);
				sum_fr -= t->max_size.value;
				changed = true;
				break; /* Recompute the fraction using the new denominator. */
			}
		}
	} while (changed);
	int64_t divisor = max((int64_t)F_1, sum_fr), carry = 0;
	for (int i = 0; i < axis->count; i++) {
		struct track *t = &axis->tracks[i];
		if (collapsed(t) || t->frozen || t->max_size.kind != CSS_GRID_FR || !t->max_size.value) continue;
		int64_t share = (int64_t)remaining * t->max_size.value + carry;
		t->size = px(share / divisor);
		carry = share % divisor;
	}
	remaining = px((int64_t)axis->available - sizes_and_gutters(axis));
	for (int i = 0; i < axis->count && stretch; i++) {
		struct track *t = &axis->tracks[i];
		if (collapsed(t) || t->max_size.kind != CSS_GRID_AUTO) continue;
		int share = remaining / stretch--;
		t->size += share;
		remaining -= share;
	}
}

static int align_offset(enum css_align_content_e alignment, int extra, int index, int count)
{
	if (!extra || !count) return 0;
	switch (alignment) {
	case CSS_ALIGN_CONTENT_FLEX_END: return extra;
	case CSS_ALIGN_CONTENT_CENTER: return extra / 2;
	case CSS_ALIGN_CONTENT_SPACE_BETWEEN: return count < 2 ? 0 : (int64_t)extra * index / (count - 1);
	case CSS_ALIGN_CONTENT_SPACE_AROUND: return (int64_t)extra * (2LL * index + 1) / (2LL * count);
	case CSS_ALIGN_CONTENT_SPACE_EVENLY: return (int64_t)extra * (index + 1LL) / (count + 1LL);
	default: return 0;
	}
}

static int size_axis(struct axis *axis, const struct box *grid, const css_unit_ctx *units,
		struct item *items, int count, bool rows, int *minimum)
{
	for (int i = 0; i < axis->count; i++) {
		struct track *t = &axis->tracks[i];
		if (!collapsed(t) && t->min_size.kind == CSS_GRID_LENGTH)
			t->size = pixels(grid, units, t->min_size.value, t->min_size.unit, axis->available);
	}
	for (int preferred = 0; preferred < 2; preferred++) {
		if (preferred) {
			for (int i = 0; i < axis->count; i++) {
				struct track *t = &axis->tracks[i];
				t->target = t->size;
				if (!collapsed(t) && t->max_size.kind == CSS_GRID_LENGTH)
					t->target = max(t->size, pixels(grid, units, t->max_size.value, t->max_size.unit, axis->available));
			}
		}
		for (int spanning = 0; spanning < 2; spanning++) {
			for (int i = 0; i < count; i++) {
				struct item *item = &items[i];
				struct area area = rows ? item->row : item->col;
				if ((area.span > 1) != (spanning != 0)) continue;
				contribute(axis, area, rows ? item->natural_height : item->box->min_width,
						rows ? item->natural_height : item->box->max_width, preferred);
			}
		}
		if (!preferred && minimum) *minimum = px(sizes_and_gutters(axis));
	}
	int visible = 0;
	for (int i = 0; i < axis->count; i++) {
		struct track *t = &axis->tracks[i];
		if (collapsed(t)) continue;
		visible++;
		if (t->max_size.kind == CSS_GRID_FIT_CONTENT &&
		    !(t->max_size.unit == CSS_UNIT_PCT && axis->available < 0)) {
			int limit = pixels(grid, units, t->max_size.value, t->max_size.unit, axis->available);
			t->target = max(t->size, min(t->target, limit));
		}
	}
	allocate_tracks(axis);
	int extra = axis->available < 0 ? 0 : px((int64_t)axis->available - sizes_and_gutters(axis));
	int pos = 0, index = 0;
	for (int i = 0; i < axis->count; i++) {
		struct track *t = &axis->tracks[i];
		if (!collapsed(t) && index) pos = px((int64_t)pos + axis->gap);
		t->start = px((int64_t)pos + align_offset(axis->alignment, extra, index, visible));
		if (collapsed(t)) continue;
		pos = px((int64_t)pos + t->size);
		index++;
	}
	return pos;
}

static int area_size(const struct axis *axis, struct area area)
{
	const struct track *last = &axis->tracks[area.start + area.span - 1];
	return last->start + last->size - axis->tracks[area.start].start;
}

static int margin_offset(struct box *box, enum box_side start, enum box_side end, int free)
{
	if (box->margin[start] == AUTO)
		return (box->margin[end] == AUTO ? max(0, free) / 2 : max(0, free));
	return box->margin[start];
}

static int horizontal_alignment(const struct box *b)
{
	int align = css_computed_justify_self(b->style);
	return align == CSS_ALIGN_SELF_AUTO ? css_computed_justify_items(b->parent->style) : align;
}

static bool layout_contents(struct box *b, html_content *content)
{
	b->float_container = b->parent;
	bool ok;
	switch (b->type) {
	case BOX_BLOCK: ok = layout_block_context(b, -1, content); break;
	case BOX_TABLE: ok = layout_table(b, b->width, content); break;
	case BOX_FLEX: ok = layout_flex(b, b->width, content); break; /* includes nested grids */
	default: ok = false; break;
	}
	b->float_container = NULL;
	return ok;
}

static int percent_height(const struct item *item, html_content *content, int width, int height)
{
	int result = pixels(item->box, &content->unit_len_ctx, item->height_pct, CSS_UNIT_PCT, height);
	layout_handle_box_sizing(&content->unit_len_ctx, item->box, width, false, &result);
	return clamped_size(result, item->min_height, item->max_height);
}

/* Lay out each child at its actual column width before sizing intrinsic rows. */
static bool layout_item(struct item *item, int width, int known_height, html_content *content)
{
	struct box *b = item->box;
	int min_width, max_width;
	b->float_container = b->parent;
	layout_find_dimensions(&content->unit_len_ctx, width, -1, b, b->style, &b->width, &b->height,
			&max_width, &min_width, &item->max_height, &item->min_height, b->margin, b->padding, b->border);
	css_unit height_unit = CSS_UNIT_PX;
	item->height_auto = css_computed_height(b->style, &item->height_pct, &height_unit) == CSS_HEIGHT_AUTO;
	item->percent_height = !item->height_auto && height_unit == CSS_UNIT_PCT;
	if (item->percent_height)
		b->height = known_height < 0 ? AUTO : percent_height(item, content, width, known_height);
	if (known_height >= 0 && item->height_auto && b->margin[TOP] != AUTO && b->margin[BOTTOM] != AUTO &&
	    !lh__box_is_object(b) && lh__box_align_self(b->parent, b) == CSS_ALIGN_SELF_STRETCH)
		b->height = clamped_size(known_height - lh__delta_outer_height(b), item->min_height, item->max_height);
	int delta = lh__delta_outer_width(b);
	if (b->width == AUTO) {
		if (horizontal_alignment(b) != CSS_ALIGN_SELF_STRETCH ||
		    b->margin[LEFT] == AUTO || b->margin[RIGHT] == AUTO || lh__box_is_object(b))
			b->width = max(b->min_width, min(width, b->max_width)) - delta;
		else b->width = width - delta;
	}
	b->width = clamped_size(b->width, min_width, max_width);
	if (!layout_contents(b, content)) return false;
	b->height = clamped_size(b->height == AUTO ? 0 : b->height, item->min_height, item->max_height);
	item->natural_height = px((int64_t)b->height + lh__delta_outer_height(b));
	return true;
}

struct grid_layout {
	struct axis cols, rows;
	struct placement placement;
	struct item *items;
	int count;
};

static void free_layout(struct grid_layout *layout)
{
	free(layout->items);
	free(layout->placement.rows);
	free(layout->cols.tracks);
	free(layout->cols.implicit);
	free(layout->rows.tracks);
	free(layout->rows.implicit);
}

static int item_order(const void *left, const void *right)
{
	const struct item *a = left, *b = right;
	if (a->order != b->order) return a->order < b->order ? -1 : 1;
	return (a->source_index > b->source_index) - (a->source_index < b->source_index);
}

static bool prepare_layout(struct grid_layout *layout, const struct box *grid,
		const css_unit_ctx *units, int width, int height)
{
	for (struct box *b = grid->children; b; b = b->next)
		if (!lh__box_is_absolute(b)) layout->count++;
	if (layout->count) {
		layout->items = calloc((size_t)layout->count, sizeof(*layout->items));
		if (!layout->items) return false;
		bool sort = false;
		int i = 0, previous = INT_MIN;
		for (struct box *b = grid->children; b; b = b->next) {
			if (lh__box_is_absolute(b)) continue;
			struct item *item = &layout->items[i];
			item->box = b;
			item->source_index = i++;
			int32_t order = 0;
			css_computed_order(b->style, &order);
			item->order = order;
			if (item->order < previous) sort = true;
			previous = item->order;
		}
		if (sort) qsort(layout->items, layout->count, sizeof(*layout->items), item_order);
	}
	return init_axis(&layout->cols, grid, units, false, width) &&
	       init_axis(&layout->rows, grid, units, true, height) &&
	       position_items(&layout->placement, &layout->cols, &layout->rows, layout->items, layout->count, grid->style);
}

/* Inline grids and grids nested inside other formatting contexts must report
 * the width of their tracks, including gaps, even when their items are empty. */
void layout_grid_intrinsic(const struct box *grid, const html_content *content,
		int *minimum, int *maximum)
{
	struct grid_layout layout = {0};
	if (prepare_layout(&layout, grid, &content->unit_len_ctx, -1, -1))
		*maximum = size_axis(&layout.cols, grid, &content->unit_len_ctx,
				layout.items, layout.count, false, minimum);
	free_layout(&layout);
}

/* Resolve a single definite row before laying out its items. Automatic or
 * spanning rows need the item's intrinsic contribution before this is known. */
static int definite_height(const struct item *item, const struct axis *rows,
		const struct box *grid, const css_unit_ctx *units)
{
	if (item->row.span != 1) return -1;
	const struct track *t = &rows->tracks[item->row.start];
	if (t->min_size.kind != CSS_GRID_LENGTH || t->max_size.kind != CSS_GRID_LENGTH ||
	    t->min_size.value != t->max_size.value || t->min_size.unit != t->max_size.unit ||
	    (rows->available < 0 && t->min_size.unit == CSS_UNIT_PCT)) return -1;
	return pixels(grid, units, t->min_size.value, t->min_size.unit, rows->available);
}

bool layout_grid(struct box *grid, int available_width, html_content *content)
{
	struct grid_layout layout = {0};
	const css_unit_ctx *units = &content->unit_len_ctx;
	int min_height, max_height;
	bool success = false;
	/* The parent layout has already resolved the container's margins and edges. */
	layout_find_dimensions(units, available_width, -1, grid, grid->style, NULL, NULL,
			NULL, NULL, &max_height, &min_height, NULL, NULL, NULL);
	for (struct box *b = grid->children; b; b = b->next)
		if (lh__box_is_absolute(b)) { b->x = grid->padding[LEFT]; b->y = grid->padding[TOP]; }
	if (!prepare_layout(&layout, grid, units, grid->width, grid->height)) goto done;
	size_axis(&layout.cols, grid, units, layout.items, layout.count, false, NULL);
	for (int i = 0; i < layout.count; i++) {
		struct item *item = &layout.items[i];
		int known_height = definite_height(item, &layout.rows, grid, units);
		if (!layout_item(item, area_size(&layout.cols, item->col), known_height, content)) goto done;
	}
	int height = size_axis(&layout.rows, grid, units, layout.items, layout.count, true, NULL);
	bool rtl = css_computed_direction(grid->style) == CSS_DIRECTION_RTL;
	grid->height = clamped_size(grid->height == AUTO ? height : grid->height, min_height, max_height);
	for (int i = 0; i < layout.count; i++) {
		struct item *item = &layout.items[i];
		struct box *b = item->box;
		int width = area_size(&layout.cols, item->col);
		int height = area_size(&layout.rows, item->row);
		int width_free = width - b->width - lh__delta_outer_width(b);
		int height_free = height - item->natural_height;
		int align = lh__box_align_self(grid, b);
		int resolved_height = b->height;
		if (item->percent_height) resolved_height = percent_height(item, content, width, height);
		else if (align == CSS_ALIGN_SELF_STRETCH && item->height_auto && !lh__box_is_object(b) &&
			 b->margin[TOP] != AUTO && b->margin[BOTTOM] != AUTO)
			resolved_height = clamped_size(b->height + height_free, item->min_height, item->max_height);
		if (resolved_height != b->height) {
			b->height = resolved_height;
			if (b->children && !layout_contents(b, content)) goto done;
		}
		height_free = height - b->height - lh__delta_outer_height(b);
		int y = margin_offset(b, TOP, BOTTOM, height_free);
		if (b->margin[TOP] != AUTO && b->margin[BOTTOM] != AUTO) {
			if (align == CSS_ALIGN_SELF_CENTER) y += height_free / 2;
			else if (align == CSS_ALIGN_SELF_FLEX_END) y += height_free;
		}
		int x = margin_offset(b, LEFT, RIGHT, width_free);
		if (b->margin[LEFT] != AUTO && b->margin[RIGHT] != AUTO) {
			int justify = horizontal_alignment(b);
			if (justify == CSS_ALIGN_SELF_CENTER) x += width_free / 2;
			else if ((justify == CSS_ALIGN_SELF_FLEX_END) != rtl) x += width_free;
		}
		int col_start = layout.cols.tracks[item->col.start].start;
		if (rtl) col_start = grid->width - col_start - width;
		b->x = grid->padding[LEFT] + col_start + x + b->border[LEFT].width;
		b->y = grid->padding[TOP] + layout.rows.tracks[item->row.start].start + y + b->border[TOP].width;
	}
	success = true;
done:
	free_layout(&layout);
	return success;
}
