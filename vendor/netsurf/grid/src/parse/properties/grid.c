/* Parse grid templates and numeric line placement into immutable values.
 * The CSS engine owns the cascade, media filtering and lifetime of these values. */
#include <stdlib.h>
#include <strings.h>
#include <string.h>
#include <libcss/grid.h>
#include "grid_values.h"
#include "parse/properties/math.h"
#include "bytecode/bytecode.h"
#include "bytecode/opcodes.h"
#include "parse/properties/properties.h"
#include "parse/properties/utils.h"
#include "select/helpers.h"
#include "utils/utils.h"

static bool word(const css_token *t, const char *name)
{
	return t && t->idata && lwc_string_length(t->idata) == strlen(name) &&
		strncasecmp(lwc_string_data(t->idata), name, strlen(name)) == 0;
}

static const css_token *next_token(const parserutils_vector *v, int32_t *pos)
{
	consumeWhitespace(v, pos);
	return parserutils_vector_peek(v, *pos);
}

static bool take_char(const parserutils_vector *v, int32_t *pos, char c)
{
	const css_token *t = next_token(v, pos);
	if (t == NULL || !tokenIsChar(t, c)) return false;
	parserutils_vector_iterate(v, pos);
	return true;
}

static bool integer(const css_token *t, int *result)
{
	if (t == NULL || t->type != CSS_TOKEN_NUMBER) return false;
	size_t used;
	css_fixed n = css__number_from_lwc_string(t->idata, true, &used);
	if (used != lwc_string_length(t->idata) || n == 0) return false;
	*result = FIXTOINT(n);
	return true;
}

static bool breadth(css_language *c, const parserutils_vector *v, int32_t *pos, css_grid_breadth *out)
{
	const css_token *t = next_token(v, pos);
	if (t == NULL) return false;
	if (t->type == CSS_TOKEN_FUNCTION) {
		struct css_math_token program[CSS_MATH_LIMIT];
		size_t count;
		int32_t first = *pos;
		if (css__parse_math_program(c, v, pos, program, CSS_MATH_LIMIT, &count) != CSS_OK)
			return false;
		out->kind = CSS_GRID_LENGTH;
		out->unit = CSS_UNIT_CALC;
		out->value = first; /* Token cursor until store_tracks packs the program. */
		out->math_bytes = count * sizeof(*program);
		return true;
	}
	if (t->type == CSS_TOKEN_IDENT) {
		if (word(t, "auto")) out->kind = CSS_GRID_AUTO;
		else if (word(t, "min-content")) out->kind = CSS_GRID_MIN_CONTENT;
		else if (word(t, "max-content")) out->kind = CSS_GRID_MAX_CONTENT;
		else return false;
		parserutils_vector_iterate(v, pos);
		return true;
	}
	if (t->type == CSS_TOKEN_DIMENSION) {
		const char *s = lwc_string_data(t->idata);
		size_t size = lwc_string_length(t->idata), n;
		css_fixed factor = css__number_from_lwc_string(t->idata, false, &n);
		if (size - n == 2 && strncasecmp(s + n, "fr", 2) == 0) {
			if (factor < 0) return false;
			out->kind = CSS_GRID_FR;
			out->value = factor;
			parserutils_vector_iterate(v, pos);
			return true;
		}
	}
	uint32_t unit;
	if (css__parse_unit_specifier(c, v, pos, UNIT_PX, &out->value, &unit) != CSS_OK ||
	    out->value < 0 || !(unit & (UNIT_LENGTH | UNIT_PCT))) return false;
	out->kind = CSS_GRID_LENGTH;
	out->unit = css__to_css_unit(unit);
	return true;
}

static bool track(css_language *c, const parserutils_vector *v, int32_t *pos, css_grid_track *out)
{
	const css_token *t = next_token(v, pos);
	out->repeat = 1;
	out->group_size = 1;
	if (t && t->type == CSS_TOKEN_FUNCTION && word(t, "fit-content")) {
		parserutils_vector_iterate(v, pos);
		if (!breadth(c, v, pos, &out->max) || out->max.kind != CSS_GRID_LENGTH ||
		    !take_char(v, pos, ')')) return false;
		out->min.kind = CSS_GRID_AUTO;
		out->max.kind = CSS_GRID_FIT_CONTENT;
		return true;
	}
	if (t && t->type == CSS_TOKEN_FUNCTION && word(t, "minmax")) {
		parserutils_vector_iterate(v, pos);
		return breadth(c, v, pos, &out->min) && out->min.kind != CSS_GRID_FR &&
			take_char(v, pos, ',') && breadth(c, v, pos, &out->max) && take_char(v, pos, ')');
	}
	if (!breadth(c, v, pos, &out->max)) return false;
	out->min = out->max;
	if (out->max.kind == CSS_GRID_FR) out->min = (css_grid_breadth){.kind = CSS_GRID_AUTO};
	return true;
}

static css_error store(css_language *c, css_style *style, opcode_t prop, const void *bytes, size_t size)
{
	if (!size) return css__stylesheet_style_appendOPV(style, prop, 0, 0);
	lwc_string *data;
	if (lwc_intern_string(bytes, size, &data) != lwc_error_ok) return CSS_NOMEM;
	uint32_t id;
	css_error err = css__stylesheet_string_add(c->sheet, data, &id);
	if (err == CSS_OK) err = css__stylesheet_style_appendOPV(style, prop, 0, 1);
	if (err == CSS_OK) err = css__stylesheet_style_append(style, id);
	return err;
}

/* Keep token ranges while parsing. The computed style owns a single interned
 * value containing tracks and length-prefixed names, with no borrowed pointers. */
struct name_set {
	int32_t first;
	uint32_t bytes;
};
struct name_sets {
	struct name_set sets[2 * CSS_GRID_MAX_TRACKS + 2];
	size_t count, bytes;
};

static bool custom_ident(const css_token *t)
{
	return t && t->type == CSS_TOKEN_IDENT && !word(t, "auto") && !word(t, "span") &&
		!word(t, "initial") && !word(t, "inherit") && !word(t, "unset") &&
		!word(t, "revert") && !word(t, "revert-layer") && !word(t, "default");
}

static bool read_names(const parserutils_vector *v, int32_t *pos, struct name_sets *pool, uint32_t *set)
{
	*set = 0;
	if (!take_char(v, pos, '[')) return true;
	struct name_set list = {.first = *pos};
	while (!take_char(v, pos, ']')) {
		const css_token *t = next_token(v, pos);
		if (!custom_ident(t)) return false;
		size_t length = lwc_string_length(t->idata);
		if (length > UINT32_MAX || (uint64_t)length + sizeof(uint32_t) + list.bytes > UINT32_MAX) return false;
		list.bytes += sizeof(uint32_t) + length;
		parserutils_vector_iterate(v, pos);
	}
	if (!list.bytes) return true;
	if (pool->count >= sizeof(pool->sets)/sizeof(*pool->sets) ||
	    (uint64_t)pool->bytes + list.bytes + sizeof(uint32_t) > UINT32_MAX) return false;
	pool->sets[pool->count++] = list;
	*set = pool->count;
	pool->bytes += sizeof(uint32_t) + list.bytes;
	return true;
}

static css_error store_tracks(css_language *c, css_style *result, opcode_t prop,
		const parserutils_vector *v, css_grid_track *tracks, size_t count, const struct name_sets *pool)
{
	size_t prefix = sizeof(uint32_t) + count * sizeof(*tracks);
	if (pool->bytes > UINT32_MAX - prefix) return CSS_NOMEM;
	/* Collect distinct token ranges before encoding. The minimum and maximum
	 * of a simple track, and every fixed repeat, share one copy. */
	struct expression_ref { int32_t cursor; uint32_t bytes, offset; };
	struct expression_ref expr[2 * CSS_GRID_MAX_TRACKS];
	size_t expressions = 0, total = prefix + pool->bytes;
	for (size_t i = 0; i < count; i++) {
		css_grid_breadth *breadths[] = {&tracks[i].min, &tracks[i].max};
		for (size_t j = 0; j < 2; j++) {
			css_grid_breadth *b = breadths[j];
			if (!b->math_bytes) continue;
			size_t k = 0;
			while (k < expressions && expr[k].cursor != b->value) k++;
			if (k == expressions) {
				if (total > INT32_MAX - b->math_bytes) return CSS_NOMEM;
				expr[expressions++] = (struct expression_ref){b->value, b->math_bytes, total};
				total += b->math_bytes;
			}
			b->value = expr[k].offset;
		}
	}
	char *data = malloc(total);
	if (!data) return CSS_NOMEM;
	uint32_t n = count, offsets[2 * CSS_GRID_MAX_TRACKS + 3] = {0};
	memcpy(data, &n, sizeof(n));
	char *p = data + prefix;
	for (size_t i = 0; i < pool->count; i++) {
		struct name_set set = pool->sets[i];
		offsets[i+1] = p - data;
		memcpy(p, &set.bytes, sizeof(set.bytes));
		p += sizeof(set.bytes);
		while (!take_char(v, &set.first, ']')) {
			const css_token *t = next_token(v, &set.first);
			uint32_t len = lwc_string_length(t->idata);
			memcpy(p, &len, sizeof(len));
			memcpy(p + sizeof(len), lwc_string_data(t->idata), len);
			p += sizeof(len) + len;
			parserutils_vector_iterate(v, &set.first);
		}
	}
	for (size_t i = 0; i < count; i++) {
		css_grid_track t = tracks[i];
		t.before = offsets[t.before];
		t.after = offsets[t.after];
		t.outer_before = offsets[t.outer_before];
		t.outer_after = offsets[t.outer_after];
		memcpy(data + sizeof(n) + i * sizeof(t), &t, sizeof(t));
	}
	for (size_t i = 0; i < expressions; i++) {
		struct css_math_token program[CSS_MATH_LIMIT];
		size_t length;
		if (css__parse_math_program(c, v, &expr[i].cursor, program, CSS_MATH_LIMIT, &length) != CSS_OK ||
		    length * sizeof(*program) != expr[i].bytes) {
			free(data);
			return CSS_INVALID;
		}
		memcpy(data + expr[i].offset, program, expr[i].bytes);
	}
	css_error err = store(c, result, prop, data, total);
	free(data);
	return err;
}

static bool fixed_track(const css_grid_track *t)
{
	return t->min.kind == CSS_GRID_LENGTH || t->max.kind == CSS_GRID_LENGTH;
}

static bool repeated(css_language *c, const parserutils_vector *v, int32_t *pos,
		css_grid_track *tracks, size_t *count, bool *has_auto, struct name_sets *names)
{
	int n;
	const css_token *t = next_token(v, pos);
	if (t && t->type == CSS_TOKEN_IDENT && word(t, "auto-fill")) n = CSS_GRID_AUTO_FILL;
	else if (t && t->type == CSS_TOKEN_IDENT && word(t, "auto-fit")) n = CSS_GRID_AUTO_FIT;
	else if (!integer(t, &n) || n < 1) return false;
	parserutils_vector_iterate(v, pos);
	if (!take_char(v, pos, ',')) return false;
	size_t first = *count;
	uint32_t leading = 0;
	if (!read_names(v, pos, names, &leading)) return false;
	do {
		if (*count == CSS_GRID_MAX_TRACKS || !track(c, v, pos, &tracks[*count])) return false;
		if (*count == first) tracks[*count].before = leading;
		if (!read_names(v, pos, names, &tracks[*count].after)) return false;
		(*count)++;
	} while (!take_char(v, pos, ')'));
	size_t group = *count - first;
	if (n < 0) {
		if (*has_auto) return false;
		*has_auto = true;
		tracks[first].repeat = n;
		tracks[first].group_size = group;
	} else {
		if ((size_t)n > (CSS_GRID_MAX_TRACKS - first) / group) return false;
		for (int i = 1; i < n; i++, *count += group)
			memcpy(tracks + *count, tracks + first, group * sizeof(*tracks));
	}
	return true;
}

static css_error parse_template(css_language *c, const parserutils_vector *v, int32_t *ctx,
		css_style *result, opcode_t prop)
{
	int32_t pos = *ctx;
	const css_token *t = next_token(v, &pos);
	if (t == NULL) return CSS_INVALID;
	bool implicit = prop == CSS_PROP_GRID_AUTO_ROWS || prop == CSS_PROP_GRID_AUTO_COLUMNS;
	enum flag_value flag = get_css_flag_value(c, t);
	if (flag != FLAG_VALUE__NONE || (!implicit && t->type == CSS_TOKEN_IDENT && word(t, "none"))) {
		parserutils_vector_iterate(v, &pos);
		*ctx = pos;
		return flag == FLAG_VALUE__NONE ? store(c, result, prop, NULL, 0) :
				css_stylesheet_style_flag_value(result, flag, prop);
	}
	css_grid_track tracks[CSS_GRID_MAX_TRACKS] = {0};
	size_t count = 0;
	bool has_auto = false;
	struct name_sets names = {0};
	uint32_t leading = 0;
	if (!implicit && !read_names(v, &pos, &names, &leading)) return CSS_INVALID;
	while ((t = next_token(v, &pos)) != NULL && !tokenIsChar(t, '!') && !tokenIsChar(t, '/')) {
		if (t->type == CSS_TOKEN_FUNCTION && word(t, "repeat")) {
			parserutils_vector_iterate(v, &pos);
			if (implicit || !repeated(c, v, &pos, tracks, &count, &has_auto, &names)) return CSS_INVALID;
		} else {
			if (count == CSS_GRID_MAX_TRACKS || !track(c, v, &pos, &tracks[count])) return CSS_INVALID;
			count++;
		}
		if (!implicit && !read_names(v, &pos, &names, &tracks[count-1].outer_after)) return CSS_INVALID;
	}
	if (!count) return CSS_INVALID;
	tracks[0].outer_before = leading;
	if (has_auto)
		for (size_t i = 0; i < count; i++)
			if (!fixed_track(&tracks[i])) return CSS_INVALID;
	*ctx = pos;
	return store_tracks(c, result, prop, v, tracks, count, &names);
}

struct line_spec {
	struct grid_line_value value;
	lwc_string *name;
};

static bool read_line(const parserutils_vector *v, int32_t *pos, struct line_spec *line)
{
	const css_token *t = next_token(v, pos);
	if (!t) return false;
	if (t->type == CSS_TOKEN_IDENT && word(t, "auto")) {
		parserutils_vector_iterate(v, pos);
		return true;
	}
	bool span = false;
	int number = 0;
	while ((t = next_token(v, pos)) && !tokenIsChar(t, '/') && !tokenIsChar(t, '!')) {
		if (!span && t->type == CSS_TOKEN_IDENT && word(t, "span")) span = true;
		else if (!number && integer(t, &number)) { /* Numeric occurrence. */ }
		else if (!line->name && custom_ident(t)) line->name = t->idata;
		else return false;
		parserutils_vector_iterate(v, pos);
	}
	if ((!number && !line->name) || (span && number < 0)) return false;
	line->value.number = number;
	line->value.span = span;
	return true;
}

static css_error store_line(css_language *c, css_style *result, opcode_t prop, const struct line_spec *line)
{
	if (!line->name)
		return store(c, result, prop, &line->value, line->value.number ? sizeof(line->value) : 0);
	size_t length = lwc_string_length(line->name);
	if (length > SIZE_MAX - sizeof(line->value)) return CSS_NOMEM;
	char *buf = malloc(sizeof(line->value) + length);
	if (!buf) return CSS_NOMEM;
	memcpy(buf, &line->value, sizeof(line->value));
	memcpy(buf + sizeof(line->value), lwc_string_data(line->name), length);
	css_error err = store(c, result, prop, buf, sizeof(line->value) + length);
	free(buf);
	return err;
}

/* Missing longhands default to auto, except that a bare area name supplies
 * the corresponding missing end/column name according to CSS Grid §8.4. */
static css_error parse_lines(css_language *c, const parserutils_vector *v, int32_t *ctx,
		css_style *result, const opcode_t *props, size_t count)
{
	int32_t pos = *ctx;
	const css_token *t = next_token(v, &pos);
	if (!t) return CSS_INVALID;
	enum flag_value flag = get_css_flag_value(c, t);
	struct line_spec lines[4] = {0};
	if (flag != FLAG_VALUE__NONE) {
		parserutils_vector_iterate(v, &pos);
	} else {
		size_t i = 0;
		do {
			if (!read_line(v, &pos, &lines[i++])) return CSS_INVALID;
		} while (i < count && take_char(v, &pos, '/'));
		for (; i < count; i++) {
			size_t source = count == 4 && i == 3 ? 1 : 0;
			if (!lines[source].value.span && !lines[source].value.number) lines[i].name = lines[source].name;
		}
	}
	for (size_t i = 0; i < count; i++) {
		css_error err = flag == FLAG_VALUE__NONE ? store_line(c, result, props[i], &lines[i]) :
			css_stylesheet_style_flag_value(result, flag, props[i]);
		if (err != CSS_OK) return err;
	}
	*ctx = pos;
	return CSS_OK;
}

#define PARSE_ARGS css_language *c, const parserutils_vector *v, int32_t *ctx, css_style *result
#define PLACEMENT(name, ...) css_error css__parse_##name(PARSE_ARGS) \
	{ const opcode_t props[] = {__VA_ARGS__}; return parse_lines(c, v, ctx, result, props, sizeof(props)/sizeof(props[0])); }

PLACEMENT(grid_row, CSS_PROP_GRID_ROW_START, CSS_PROP_GRID_ROW_END)
PLACEMENT(grid_column, CSS_PROP_GRID_COLUMN_START, CSS_PROP_GRID_COLUMN_END)
PLACEMENT(grid_row_start, CSS_PROP_GRID_ROW_START)
PLACEMENT(grid_column_start, CSS_PROP_GRID_COLUMN_START)
PLACEMENT(grid_row_end, CSS_PROP_GRID_ROW_END)
PLACEMENT(grid_column_end, CSS_PROP_GRID_COLUMN_END)
PLACEMENT(grid_area, CSS_PROP_GRID_ROW_START, CSS_PROP_GRID_COLUMN_START, CSS_PROP_GRID_ROW_END, CSS_PROP_GRID_COLUMN_END)

css_error css__parse_grid_template_rows(PARSE_ARGS)
{ return parse_template(c, v, ctx, result, CSS_PROP_GRID_TEMPLATE_ROWS); }

css_error css__parse_grid_template_columns(PARSE_ARGS)
{ return parse_template(c, v, ctx, result, CSS_PROP_GRID_TEMPLATE_COLUMNS); }

css_error css__parse_gap(PARSE_ARGS)
{
	int32_t pos = *ctx;
	const css_token *t = next_token(v, &pos);
	if (t == NULL) return CSS_INVALID;
	enum flag_value flag = get_css_flag_value(c, t);
	if (flag != FLAG_VALUE__NONE) {
		parserutils_vector_iterate(v, &pos);
		css_error err = css_stylesheet_style_flag_value(result, flag, CSS_PROP_ROW_GAP);
		if (err == CSS_OK) err = css_stylesheet_style_flag_value(result, flag, CSS_PROP_COLUMN_GAP);
		if (err == CSS_OK) *ctx = pos;
		return err;
	}
	int32_t first = pos;
	css_error err = css__parse_row_gap(c, v, &pos, result);
	if (err != CSS_OK) return err;
	t = next_token(v, &pos);
	bool both = t == NULL || tokenIsChar(t, '!');
	if (!both && get_css_flag_value(c, t) != FLAG_VALUE__NONE) return CSS_INVALID;
	err = css__parse_column_gap(c, v, both ? &first : &pos, result);
	if (err == CSS_OK) *ctx = pos;
	return err;
}


css_error css__parse_grid_auto_rows(PARSE_ARGS)
{ return parse_template(c, v, ctx, result, CSS_PROP_GRID_AUTO_ROWS); }

css_error css__parse_grid_auto_columns(PARSE_ARGS)
{ return parse_template(c, v, ctx, result, CSS_PROP_GRID_AUTO_COLUMNS); }

css_error css__parse_grid_auto_flow(PARSE_ARGS)
{
	int32_t pos = *ctx;
	const css_token *t = next_token(v, &pos);
	if (!t) return CSS_INVALID;
	enum flag_value flag = get_css_flag_value(c, t);
	if (flag != FLAG_VALUE__NONE) {
		parserutils_vector_iterate(v, &pos);
		*ctx = pos;
		return css_stylesheet_style_flag_value(result, flag, CSS_PROP_GRID_AUTO_FLOW);
	}
	unsigned char flow = 0;
	bool axis = false;
	while (t && t->type == CSS_TOKEN_IDENT) {
		if (!axis && (word(t, "row") || word(t, "column"))) {
			if (word(t, "column")) flow |= CSS_GRID_FLOW_COLUMN;
			axis = true;
		} else if (!(flow & CSS_GRID_FLOW_DENSE) && word(t, "dense")) {
			flow |= CSS_GRID_FLOW_DENSE;
		} else return CSS_INVALID;
		parserutils_vector_iterate(v, &pos);
		t = next_token(v, &pos);
	}
	if (!axis && !flow) return CSS_INVALID;
	*ctx = pos;
	return store(c, result, CSS_PROP_GRID_AUTO_FLOW, &flow, sizeof(flow));
}

struct named_area {
	struct grid_named_area area;
	const char *name;
	unsigned cells;
};

struct template_areas {
	struct grid_areas_header size;
	struct named_area names[CSS_GRID_MAX_TRACKS];
	size_t count;
};

static bool name_code(unsigned char ch)
{
	return ch >= 128 || ch == '_' || ch == '-' || (ch >= 'A' && ch <= 'Z') ||
		(ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9');
}

static bool add_area_cell(struct template_areas *a, const char *s, size_t length, int x, int y)
{
	size_t i;
	for (i = 0; i < a->count; i++) {
		if (length == a->names[i].area.length && memcmp(s, a->names[i].name, length) == 0) break;
	}
	if (i == a->count) {
		if (i == CSS_GRID_MAX_TRACKS || length > UINT32_MAX) return false;
		a->names[i] = (struct named_area){.name = s, .area = {
			.col_start = x, .row_start = y, .col_end = x + 1, .row_end = y + 1, .length = length}};
		a->count++;
	}
	struct named_area *n = &a->names[i];
	if (x < n->area.col_start) n->area.col_start = x;
	if (x + 1 > n->area.col_end) n->area.col_end = x + 1;
	n->area.row_end = y + 1;
	n->cells++;
	return true;
}

static bool area_row(struct template_areas *a, const lwc_string *row)
{
	const char *p = lwc_string_data(row), *end = p + lwc_string_length(row);
	unsigned cols = 0;
	if (a->size.rows == CSS_GRID_MAX_TRACKS) return false;
	while (p < end) {
		if (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n' || *p == '\f') { p++; continue; }
		if (++cols > CSS_GRID_MAX_TRACKS) return false;
		if (*p == '.') {
			do p++; while (p < end && *p == '.');
		} else {
			const char *start = p;
			while (p < end && name_code((unsigned char)*p)) p++;
			if (start == p || !add_area_cell(a, start, p - start, cols, a->size.rows + 1)) return false;
		}
	}
	if (cols == 0 || (a->size.rows && a->size.columns != cols)) return false;
	a->size.columns = cols;
	a->size.rows++;
	return true;
}

static css_error store_areas(css_language *c, css_style *result, const struct template_areas *a)
{
	size_t size = sizeof(a->size);
	for (size_t i = 0; i < a->count; i++) {
		const struct grid_named_area *n = &a->names[i].area;
		unsigned cells = (n->col_end - n->col_start) * (n->row_end - n->row_start);
		if (a->names[i].cells != cells) return CSS_INVALID;
		if (size > SIZE_MAX - sizeof(*n) || SIZE_MAX - size - sizeof(*n) < n->length) return CSS_NOMEM;
		size += sizeof(*n) + n->length;
	}
	char *buf = malloc(size);
	if (!buf) return CSS_NOMEM;
	memcpy(buf, &a->size, sizeof(a->size));
	char *pos = buf + sizeof(a->size);
	for (size_t i = 0; i < a->count; i++) {
		const struct grid_named_area *n = &a->names[i].area;
		memcpy(pos, n, sizeof(*n));
		memcpy(pos + sizeof(*n), a->names[i].name, n->length);
		pos += sizeof(*n) + n->length;
	}
	css_error err = store(c, result, CSS_PROP_GRID_TEMPLATE_AREAS, buf, size);
	free(buf);
	return err;
}

css_error css__parse_grid_template_areas(PARSE_ARGS)
{
	int32_t pos = *ctx;
	const css_token *t = next_token(v, &pos);
	if (!t) return CSS_INVALID;
	enum flag_value flag = get_css_flag_value(c, t);
	if (flag != FLAG_VALUE__NONE || (t->type == CSS_TOKEN_IDENT && word(t, "none"))) {
		parserutils_vector_iterate(v, &pos);
		*ctx = pos;
		return flag == FLAG_VALUE__NONE ? store(c, result, CSS_PROP_GRID_TEMPLATE_AREAS, NULL, 0) :
			css_stylesheet_style_flag_value(result, flag, CSS_PROP_GRID_TEMPLATE_AREAS);
	}
	struct template_areas areas = {0};
	while (t && t->type == CSS_TOKEN_STRING) {
		if (!area_row(&areas, t->idata)) return CSS_INVALID;
		parserutils_vector_iterate(v, &pos);
		t = next_token(v, &pos);
	}
	if (!areas.size.rows) return CSS_INVALID;
	*ctx = pos;
	return store_areas(c, result, &areas);
}

static bool finished(const css_token *t)
{
	return t == NULL || tokenIsChar(t, '!');
}

static bool automatic_flow(const parserutils_vector *v, int32_t *pos, unsigned char *flow)
{
	bool automatic = false, dense = false;
	int32_t candidate = *pos;
	const css_token *t;
	while ((t = next_token(v, &candidate)) && t->type == CSS_TOKEN_IDENT) {
		if (!automatic && word(t, "auto-flow")) automatic = true;
		else if (!dense && word(t, "dense")) dense = true;
		else break;
		parserutils_vector_iterate(v, &candidate);
	}
	if (!automatic) return false;
	*pos = candidate;
	if (dense) *flow |= CSS_GRID_FLOW_DENSE;
	return true;
}

/* CSS-wide keywords apply to the entire shorthand, never one side of '/'. */
static css_error track_operand(css_language *c, const parserutils_vector *v,
        int32_t *pos, css_style *result, opcode_t prop)
{
	const css_token *t = next_token(v, pos);
	if (!t || get_css_flag_value(c, t) != FLAG_VALUE__NONE) return CSS_INVALID;
	return parse_template(c, v, pos, result, prop);
}

static css_error track_shorthand(PARSE_ARGS, bool reset_implicit)
{
	const opcode_t props[] = {CSS_PROP_GRID_TEMPLATE_AREAS, CSS_PROP_GRID_TEMPLATE_ROWS,
		CSS_PROP_GRID_TEMPLATE_COLUMNS, CSS_PROP_GRID_AUTO_FLOW, CSS_PROP_GRID_AUTO_ROWS, CSS_PROP_GRID_AUTO_COLUMNS};
	int32_t pos = *ctx;
	const css_token *t = next_token(v, &pos);
	if (!t) return CSS_INVALID;
	enum flag_value flag = get_css_flag_value(c, t);
	if (flag != FLAG_VALUE__NONE) {
		parserutils_vector_iterate(v, &pos);
		if (!finished(next_token(v, &pos))) return CSS_INVALID;
		for (size_t i = 0; i < (reset_implicit ? 6u : 3u); i++) {
			css_error err = css_stylesheet_style_flag_value(result, flag, props[i]);
			if (err != CSS_OK) return err;
		}
		*ctx = pos;
		return CSS_OK;
	}
	css_error err;
	unsigned char flow = 0;
	opcode_t sized_implicit = CSS_N_PROPERTIES;
	if (reset_implicit && automatic_flow(v, &pos, &flow)) {
		if (!take_char(v, &pos, '/')) {
			sized_implicit = CSS_PROP_GRID_AUTO_ROWS;
			err = track_operand(c, v, &pos, result, sized_implicit);
			if (err != CSS_OK) return err;
			if (!take_char(v, &pos, '/')) return CSS_INVALID;
		}
		err = store(c, result, CSS_PROP_GRID_TEMPLATE_ROWS, NULL, 0);
		if (err == CSS_OK) err = store(c, result, CSS_PROP_GRID_TEMPLATE_AREAS, NULL, 0);
		if (err == CSS_OK) err = track_operand(c, v, &pos, result, CSS_PROP_GRID_TEMPLATE_COLUMNS);
	} else {
		pos = *ctx;
		int32_t candidate = pos;
		struct name_sets names = {0};
		uint32_t leading = 0;
		if (!read_names(v, &candidate, &names, &leading)) return CSS_INVALID;
		const css_token *after_names = next_token(v, &candidate);
		bool strings = after_names && after_names->type == CSS_TOKEN_STRING;
		if (strings) {
			pos = candidate;
			t = after_names;
			struct template_areas areas = {0};
			css_grid_track rows[CSS_GRID_MAX_TRACKS] = {0};
			do {
				if (!area_row(&areas, t->idata)) return CSS_INVALID;
				parserutils_vector_iterate(v, &pos);
				t = next_token(v, &pos);
				css_grid_track *r = &rows[areas.size.rows - 1];
				r->outer_before = leading;
				if (!finished(t) && !tokenIsChar(t, '/') && !tokenIsChar(t, '[') && t->type != CSS_TOKEN_STRING) {
					if (!track(c, v, &pos, r)) return CSS_INVALID;
				} else {
					r->repeat = 1;
					r->group_size = 1;
				}
				if (!read_names(v, &pos, &names, &r->outer_after) ||
				    !read_names(v, &pos, &names, &leading)) return CSS_INVALID;
				t = next_token(v, &pos);
				if (leading && (!t || t->type != CSS_TOKEN_STRING)) return CSS_INVALID;
			} while (t && t->type == CSS_TOKEN_STRING);
			err = store_areas(c, result, &areas);
			if (err == CSS_OK)
				err = store_tracks(c, result, CSS_PROP_GRID_TEMPLATE_ROWS, v, rows, areas.size.rows, &names);
		} else {
			err = store(c, result, CSS_PROP_GRID_TEMPLATE_AREAS, NULL, 0);
			if (err == CSS_OK) err = track_operand(c, v, &pos, result, CSS_PROP_GRID_TEMPLATE_ROWS);
		}
		if (err != CSS_OK) return err;
		if (take_char(v, &pos, '/')) {
			if (!strings && reset_implicit && automatic_flow(v, &pos, &flow)) {
				flow |= CSS_GRID_FLOW_COLUMN;
				err = store(c, result, CSS_PROP_GRID_TEMPLATE_COLUMNS, NULL, 0);
				if (err == CSS_OK && !finished(next_token(v, &pos))) {
					sized_implicit = CSS_PROP_GRID_AUTO_COLUMNS;
					err = track_operand(c, v, &pos, result, sized_implicit);
				}
			} else {
				err = track_operand(c, v, &pos, result, CSS_PROP_GRID_TEMPLATE_COLUMNS);
			}
		} else if (strings || word(t, "none")) {
			err = store(c, result, CSS_PROP_GRID_TEMPLATE_COLUMNS, NULL, 0);
		} else return CSS_INVALID;
	}
	if (err != CSS_OK || !finished(next_token(v, &pos))) return err == CSS_OK ? CSS_INVALID : err;
	if (reset_implicit) {
		err = store(c, result, CSS_PROP_GRID_AUTO_FLOW, &flow, sizeof(flow));
		for (size_t i = 4; i < 6 && err == CSS_OK; i++)
			if (props[i] != sized_implicit) err = store(c, result, props[i], NULL, 0);
	}
	if (err == CSS_OK) *ctx = pos;
	return err;
}

css_error css__parse_grid_template(PARSE_ARGS)
{ return track_shorthand(c, v, ctx, result, false); }

css_error css__parse_grid(PARSE_ARGS)
{ return track_shorthand(c, v, ctx, result, true); }
