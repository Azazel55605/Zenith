/* CSS Variables Level 1: declarations keep CSS tokens until their names have
 * cascaded on the element. Inheritance copies computed values; an inherited
 * token stream never resolves again against a child's local definitions. */
#include <limits.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include "select/variables.h"
#include "parse/important.h"
#include "utils/parserutilserror.h"

/* Cap both the substitution depth and the expanded value of any one property.
 * In particular nested fallbacks and repeated references cannot expand without
 * bound. The caller distinguishes invalid computed values from allocation errors. */
enum { VAR_DEPTH = 128, VAR_TOKENS = 32768 };

struct winner {
	const struct css_var_decl *declaration;
	uint32_t specificity;
};
enum resolution { UNRESOLVED, RESOLVING, RESOLVED, INVALID };
struct variable {
	lwc_string *name;
	struct winner origin[3];
	struct css_var_tokens value;
	enum resolution state;
	unsigned index, low;
	bool on_stack;
	struct variable *stack_next, *next, *bucket_next;
};
struct css_var_map {
	unsigned refs;
	bool may_revert;
	struct css_var_map *parent;
	struct variable *first;
	struct variable **buckets;
	size_t count, bucket_count;
};

static bool word(const css_token *t, css_token_type type, const char *literal)
{
	size_t length = strlen(literal);
	return t && t->type == type && t->idata && lwc_string_length(t->idata) == length &&
			strncasecmp(lwc_string_data(t->idata), literal, length) == 0;
}

bool css_vars_name(lwc_string *name)
{
	return name && lwc_string_length(name) > 2 && !memcmp(lwc_string_data(name), "--", 2);
}

bool css_vars_used(const parserutils_vector *v, int32_t pos)
{
	const css_token *t;
	while ((t = parserutils_vector_iterate(v, &pos)))
		if (word(t, CSS_TOKEN_FUNCTION, "var")) return true;
	return false;
}

static void tokens_free(struct css_var_tokens *list)
{
	for (size_t i = 0; i < list->count; ++i)
		if (list->items[i].idata) lwc_string_unref(list->items[i].idata);
	free(list->items);
	*list = (struct css_var_tokens){0};
}

static css_error append(struct css_var_tokens *to, const css_token *token)
{
	if (to->count == VAR_TOKENS) return CSS_INVALID;
	if (to->count == to->capacity) {
		size_t size = to->capacity ? to->capacity * 2 : 8;
		css_token *p = realloc(to->items, size * sizeof(*p));
		if (!p) return CSS_NOMEM;
		to->items = p;
		to->capacity = size;
	}
	css_token t = *token;
	/* Lexer backing storage is temporary; property parsers use the interned
	 * token value. Separators and whitespace need just their token type. */
	if (t.idata) {
		lwc_string_ref(t.idata);
		t.data.data = (uint8_t *)lwc_string_data(t.idata);
		t.data.len = lwc_string_length(t.idata);
	} else {
		t.data.data = NULL;
		t.data.len = 0;
	}
	to->items[to->count++] = t;
	return CSS_OK;
}

static css_error copy_tokens(struct css_var_tokens *to, const struct css_var_tokens *from)
{
	for (size_t i = 0; i < from->count; ++i) {
		css_error err = append(to, &from->items[i]);
		if (err != CSS_OK) return err;
	}
	return CSS_OK;
}

void css_vars_free_decls(struct css_var_decl *d)
{
	while (d) {
		struct css_var_decl *next = d->next;
		lwc_string_unref(d->property);
		tokens_free(&d->value);
		free(d);
		d = next;
	}
}

void css_vars_merge(css_style *target, css_style *from)
{
	if (!from->variables) return;
	for (struct css_var_decl *d = from->variables; d; d = d->next)
		d->offset += target->used;
	if (target->variables_tail) target->variables_tail->next = from->variables;
	else target->variables = from->variables;
	target->variables_tail = from->variables_tail;
	from->variables = from->variables_tail = NULL;
}

/* Validate var() names and balanced closing parentheses even for a declaration
 * whose value only becomes known on a particular element. */
static bool valid_call(const css_token *tokens, size_t n, size_t i)
{
	while (i < n && tokens[i].type == CSS_TOKEN_S) i++;
	if (i == n || tokens[i].type != CSS_TOKEN_IDENT || !css_vars_name(tokens[i].idata)) return false;
	i++;
	while (i < n && tokens[i].type == CSS_TOKEN_S) i++;
	return i < n && (tokenIsChar(&tokens[i], ',') || tokenIsChar(&tokens[i], ')'));
}

css_error css_vars_defer(css_language *c, const css_token *property,
		const parserutils_vector *v, int32_t *ctx, css_rule *rule, bool custom)
{
	struct css_var_decl *d = calloc(1, sizeof(*d));
	if (!d) return CSS_NOMEM;
	d->property = lwc_string_ref(property->idata);
	d->custom = custom;
	css_error err = CSS_OK;
	const css_token *t;
	int32_t pos = *ctx, important_at = -1;
	unsigned nested = 0;
	while ((t = parserutils_vector_iterate(v, &pos))) {
		if (t->type == CSS_TOKEN_INVALID_STRING) { err = CSS_INVALID; break; }
		if (tokenIsChar(t, '!') && !nested) {
			important_at = pos - 1;
			break;
		}
		if (t->type == CSS_TOKEN_FUNCTION || tokenIsChar(t, '(') ||
		    tokenIsChar(t, '[') || tokenIsChar(t, '{')) nested++;
		else if (tokenIsChar(t, ')') || tokenIsChar(t, ']') || tokenIsChar(t, '}')) {
			if (!nested) { err = CSS_INVALID; break; }
			nested--;
		}
		err = append(&d->value, t);
		if (err != CSS_OK) break;
	}
	if (err == CSS_OK && nested) err = CSS_INVALID;
	if (err == CSS_OK && important_at >= 0) {
		uint8_t flags = 0;
		err = css__parse_important(c, v, &important_at, &flags);
		consumeWhitespace(v, &important_at);
		if (!flags || parserutils_vector_peek(v, important_at)) err = CSS_INVALID;
		d->important = flags != 0;
	}
	for (size_t i = 0; err == CSS_OK && i < d->value.count; i++)
		if (word(&d->value.items[i], CSS_TOKEN_FUNCTION, "var") &&
		    !valid_call(d->value.items, d->value.count, i+1)) err = CSS_INVALID;
	if (err == CSS_OK && rule) {
		/* A fallback can introduce the CSS-wide keyword at computed-value
		 * time. Its origin snapshots must already exist by then. */
		for (size_t i = 0; i < d->value.count; i++)
			if (word(&d->value.items[i], CSS_TOKEN_IDENT, "revert"))
				c->sheet->uses_revert = true;
		css_style *style;
		err = css__stylesheet_style_create(c->sheet, &style);
		if (err == CSS_OK) {
			style->variables = style->variables_tail = d;
			d = NULL;
			err = css__stylesheet_rule_append_style(c->sheet, rule, style);
			if (err != CSS_OK) css__stylesheet_style_destroy(style);
			else c->sheet->uses_variables = true;
		}
	}
	if (err == CSS_OK) *ctx = important_at >= 0 ? important_at : pos;
	css_vars_free_decls(d);
	return err;
}

bool css_vars_may_revert(const struct css_var_map *map)
{
	return map && map->may_revert;
}

static struct css_var_map *map_ref(struct css_var_map *map)
{
	if (map) map->refs++;
	return map;
}

void css_vars_unref(struct css_var_map *map)
{
	while (map && --map->refs == 0) {
		struct css_var_map *parent = map->parent;
		while (map->first) {
			struct variable *v = map->first;
			map->first = v->next;
			tokens_free(&v->value);
			lwc_string_unref(v->name);
			free(v);
		}
		free(map->buckets);
		free(map);
		map = parent;
	}
}

static struct variable *local(const struct css_var_map *map, lwc_string *name)
{
	if (!map) return NULL;
	if (map->buckets) {
		size_t slot = lwc_string_hash_value(name) & (map->bucket_count - 1);
		for (struct variable *v = map->buckets[slot]; v; v = v->bucket_next)
			if (v->name == name) return v;
	} else {
		for (struct variable *v = map->first; v; v = v->next)
			if (v->name == name) return v;
	}
	return NULL;
}

/* Most local scopes have only a few overrides. A framework's root scope may
 * have thousands; descendants should not walk that list for each var() use. */
static struct variable *insert_variable(struct css_var_map *map, lwc_string *name)
{
	if (map->count >= 16 && map->count >= map->bucket_count) {
		size_t count = map->count * 2;
		struct variable **buckets = calloc(count, sizeof(*buckets));
		if (!buckets) return NULL;
		for (struct variable *v = map->first; v; v = v->next) {
			size_t slot = lwc_string_hash_value(v->name) & (count - 1);
			v->bucket_next = buckets[slot];
			buckets[slot] = v;
		}
		free(map->buckets);
		map->buckets = buckets;
		map->bucket_count = count;
	}
	struct variable *v = calloc(1, sizeof(*v));
	if (!v) return NULL;
	v->name = lwc_string_ref(name);
	v->next = map->first;
	map->first = v;
	if (map->buckets) {
		size_t slot = lwc_string_hash_value(name) & (map->bucket_count - 1);
		v->bucket_next = map->buckets[slot];
		map->buckets[slot] = v;
	}
	map->count++;
	return v;
}

static struct variable *lookup(const struct css_var_map *map, lwc_string *name)
{
	for (; map; map = map->parent) {
		struct variable *v = local(map, name);
		if (v) return v;
	}
	return NULL;
}

css_error css_vars_collect(css_select_state *state, const css_style *style)
{
	struct css_var_map **slot = &state->node_data->variables[state->current_pseudo];
	for (const struct css_var_decl *d = style->variables; d; d = d->next) {
		if (!d->custom) continue;
		if (!*slot) {
			*slot = calloc(1, sizeof(**slot));
			if (!*slot) return CSS_NOMEM;
			(*slot)->refs = 1;
		}
		struct variable *v = local(*slot, d->property);
		if (!v) {
			v = insert_variable(*slot, d->property);
			if (!v) return CSS_NOMEM;
		}
		struct winner *win = &v->origin[state->current_origin];
		if (!win->declaration || (d->important && !win->declaration->important) ||
		    (win->declaration->important == d->important && state->current_specificity >= win->specificity))
			*win = (struct winner){d, state->current_specificity};
	}
	return CSS_OK;
}

static const css_token *only_word(const struct css_var_tokens *values)
{
	const css_token *keyword = NULL;
	for (size_t i = 0; i < values->count; i++) {
		const css_token *t = &values->items[i];
		if (t->type == CSS_TOKEN_S) continue;
		if (keyword || t->type != CSS_TOKEN_IDENT) return NULL;
		keyword = t;
	}
	return keyword;
}

static const struct css_var_decl *winner(const struct variable *v)
{
	int limit = CSS_ORIGIN_AUTHOR;
	while (limit >= CSS_ORIGIN_UA) {
		const struct css_var_decl *win = NULL;
		int origin = -1;
		for (int i = CSS_ORIGIN_UA; i <= limit; i++) {
			const struct css_var_decl *d = v->origin[i].declaration;
			if (d && (!win || !win->important)) { win = d; origin = i; }
		}
		if (!win) break;
		const css_token *key = only_word(&win->value);
		if (!word(key, CSS_TOKEN_IDENT, "revert") && !word(key, CSS_TOKEN_IDENT, "revert-layer"))
			return win;
		limit = origin - 1;
	}
	return NULL;
}

static css_error expand(const struct css_var_tokens *src, size_t from, size_t end,
		struct css_var_map *map, struct css_var_tokens *out, unsigned depth)
{
	if (depth > VAR_DEPTH) return CSS_INVALID;
	for (size_t i = from; i < end; ++i) {
		const css_token *t = &src->items[i];
		if (!word(t, CSS_TOKEN_FUNCTION, "var")) {
			css_error err = append(out, t);
			if (err != CSS_OK) return err;
			continue;
		}
		size_t j = i+1;
		while (j < end && src->items[j].type == CSS_TOKEN_S) j++;
		if (j >= end || !valid_call(src->items, end, j)) return CSS_INVALID;
		struct variable *v = lookup(map, src->items[j++].idata);
		while (j < end && src->items[j].type == CSS_TOKEN_S) j++;
		size_t close = j;
		unsigned nesting = 0;
		for (; close < end; ++close) {
			const css_token *s = &src->items[close];
			if (s->type == CSS_TOKEN_FUNCTION || tokenIsChar(s, '(')) nesting++;
			else if (tokenIsChar(s, ')')) {
				if (!nesting) break;
				nesting--;
			}
		}
		if (close == end) return CSS_INVALID;
		css_error err;
		if (v && v->state == RESOLVED)
			err = copy_tokens(out, &v->value);
		else if (j < close && tokenIsChar(&src->items[j], ','))
			err = expand(src, j+1, close, map, out, depth+1);
		else return CSS_INVALID;
		if (err != CSS_OK) return err;
		i = close;
	}
	return CSS_OK;
}

/* A dependency in an unused fallback also creates a graph edge. Mark strongly
 * connected components before substitution so a cross-edge into a component
 * cannot accidentally rescue one of its members through a fallback. */
static void mark_cycles(struct css_var_map *map, struct variable *v, unsigned depth,
		unsigned *order, struct variable **stack)
{
	v->index = v->low = ++*order;
	v->stack_next = *stack;
	*stack = v;
	v->on_stack = true;
	const struct css_var_decl *d = winner(v);
	bool self = false;
	if (d) for (size_t i = 0; i < d->value.count; ++i) {
		if (!word(&d->value.items[i], CSS_TOKEN_FUNCTION, "var")) continue;
		size_t j = i+1;
		while (d->value.items[j].type == CSS_TOKEN_S) j++;
		struct variable *w = local(map, d->value.items[j].idata);
		if (!w) continue;
		if (w == v) self = true;
		if (!w->index) {
			if (depth == VAR_DEPTH) { v->state = INVALID; continue; }
			mark_cycles(map, w, depth+1, order, stack);
			if (w->low < v->low) v->low = w->low;
		} else if (w->on_stack && w->index < v->low) {
			v->low = w->index;
		}
	}
	if (v->low == v->index) {
		bool cycle = self || *stack != v;
		struct variable *w;
		do {
			w = *stack;
			*stack = w->stack_next;
			w->on_stack = false;
			if (cycle) w->state = INVALID;
		} while (w != v);
	}
}

static css_error resolve(struct css_var_map *map, struct variable *v, unsigned depth)
{
	if (v->state == RESOLVED || v->state == INVALID) return CSS_OK;
	if (depth == VAR_DEPTH || v->state == RESOLVING) { v->state = INVALID; return CSS_OK; }
	v->state = RESOLVING;
	const struct css_var_decl *d = winner(v);
	const css_token *key = d ? only_word(&d->value) : NULL;
	css_error err = CSS_OK;
	if (!d || word(key, CSS_TOKEN_IDENT, "inherit") || word(key, CSS_TOKEN_IDENT, "unset")) {
		struct variable *inherited = lookup(map->parent, v->name);
		if (inherited && inherited->state == RESOLVED)
			err = copy_tokens(&v->value, &inherited->value);
		else v->state = INVALID;
	} else if (word(key, CSS_TOKEN_IDENT, "initial")) {
		v->state = INVALID;
	} else {
		/* Dependency edges include var() references in unused fallbacks. */
		for (size_t i = 0; i < d->value.count; i++) {
			if (!word(&d->value.items[i], CSS_TOKEN_FUNCTION, "var")) continue;
			size_t j = i+1;
			while (d->value.items[j].type == CSS_TOKEN_S) j++;
			struct variable *other = local(map, d->value.items[j].idata);
			if (other) {
				err = resolve(map, other, depth+1);
				if (err != CSS_OK) return err;
			}
		}
		if (v->state != INVALID) err = expand(&d->value, 0, d->value.count, map, &v->value, 0);
	}
	if (err == CSS_NOMEM) return err;
	if (v->state != INVALID) v->state = err == CSS_OK ? RESOLVED : INVALID;
	return CSS_OK;
}

css_error css_vars_finish(css_select_state *state, struct css_var_map *parent)
{
	for (unsigned i = 0; i < CSS_PSEUDO_ELEMENT_COUNT; ++i) {
		struct css_var_map *base = i ? state->node_data->variables[0] : parent;
		struct css_var_map **slot = &state->node_data->variables[i];
		if (!*slot) {
			if (!i || state->results->styles[i]) *slot = map_ref(base);
			continue;
		}
		(*slot)->parent = map_ref(base);
		(*slot)->may_revert = css_vars_may_revert(base);
		unsigned order = 0;
		struct variable *stack = NULL;
		for (struct variable *v = (*slot)->first; v; v = v->next)
			if (!v->index) mark_cycles(*slot, v, 0, &order, &stack);
		for (struct variable *v = (*slot)->first; v; v = v->next) {
			css_error err = resolve(*slot, v, 0);
			if (v->state == RESOLVED && word(only_word(&v->value), CSS_TOKEN_IDENT, "revert"))
				(*slot)->may_revert = true;
			memset(v->origin, 0, sizeof(v->origin));
			if (err != CSS_OK) return err;
		}
	}
	return CSS_OK;
}

css_error css_vars_compile(css_stylesheet *sheet, const struct css_var_decl *decl,
		struct css_var_map *map, css_style **result)
{
	struct css_var_tokens value = {0};
	parserutils_vector *arg;
	*result = NULL;
	css_error err = expand(&decl->value, 0, decl->value.count, map, &value, 0);
	if (err == CSS_NOMEM) { tokens_free(&value); return err; }
	parserutils_error pe = parserutils_vector_create(sizeof(css_token), 8, &arg);
	if (pe != PARSERUTILS_OK) { tokens_free(&value); return css_error_from_parserutils_error(pe); }
	if (err == CSS_OK) {
		for (size_t i=0; i<value.count && pe == PARSERUTILS_OK; ++i)
			pe = parserutils_vector_append(arg, &value.items[i]);
		if (pe != PARSERUTILS_OK) err = css_error_from_parserutils_error(pe);
	}
	if (err == CSS_OK) err = css__parse_expanded(sheet, decl->property, arg, result);
	if (err == CSS_INVALID) {
		parserutils_vector_clear(arg);
		lwc_string *unset;
		if (lwc_intern_string("unset", 5, &unset) != lwc_error_ok) err = CSS_NOMEM;
		else {
			css_token token = {.type=CSS_TOKEN_IDENT, .idata=unset};
			pe = parserutils_vector_append(arg, &token);
			err = pe == PARSERUTILS_OK ? css__parse_expanded(sheet, decl->property, arg, result)
					: css_error_from_parserutils_error(pe);
			lwc_string_unref(unset);
		}
	}
	if (err == CSS_OK && decl->important)
		css__make_style_important(*result);
	parserutils_vector_destroy(arg);
	tokens_free(&value);
	return err;
}
