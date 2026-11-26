/* Typed length-percentage expressions, stored as immutable postfix bytecode. */
#include <string.h>
#include <strings.h>
#include "parse/properties/math.h"
#include "parse/properties/utils.h"
#include "bytecode/bytecode.h"
#include "bytecode/opcodes.h"
#include "select/helpers.h"
#include "math_values.h"

struct expression {
	css_language *css;
	const parserutils_vector *tokens;
	int32_t pos;
	unsigned count, depth;
	struct css_math_token code[CSS_MATH_LIMIT];
};
enum dim { INVALID, SCALAR, LENGTH };
enum function { UNKNOWN, CALC, MINIMUM, MAXIMUM, CLAMP };

static enum function math_function(const css_token *t)
{
	if (!t || t->type != CSS_TOKEN_FUNCTION) return UNKNOWN;
	const char *s = lwc_string_data(t->idata);
	size_t n = lwc_string_length(t->idata);
	if (n == 4 && !strncasecmp(s, "calc", n)) return CALC;
	if (n == 3 && !strncasecmp(s, "min", n)) return MINIMUM;
	if (n == 3 && !strncasecmp(s, "max", n)) return MAXIMUM;
	if (n == 5 && !strncasecmp(s, "clamp", n)) return CLAMP;
	return UNKNOWN;
}

static const css_token *peek(struct expression *e)
{
	consumeWhitespace(e->tokens, &e->pos);
	return parserutils_vector_peek(e->tokens, e->pos);
}

static bool take(struct expression *e, char c)
{
	const css_token *t = peek(e);
	if (!t || !tokenIsChar(t, c)) return false;
	parserutils_vector_iterate(e->tokens, &e->pos);
	return true;
}

static bool emit(struct expression *e, enum css_math_op op, css_unit unit, css_fixed value)
{
	if (e->count == CSS_MATH_LIMIT) return false;
	e->code[e->count++] = (struct css_math_token){op, unit, value};
	return true;
}

static enum dim math_sum(struct expression *e);

static enum dim arguments(struct expression *e, enum function fn)
{
	if (++e->depth > CSS_MATH_DEPTH) return INVALID;
	enum dim d = math_sum(e);
	if (!d) return INVALID;
	unsigned args = 1;
	while (take(e, ',')) {
		if (fn == CALC || (fn == CLAMP && args == 3) || math_sum(e) != d) return INVALID;
		if ((fn == MINIMUM || fn == MAXIMUM) && !emit(e, fn == MINIMUM ? M_MIN : M_MAX, 0, 0)) return INVALID;
		args++;
	}
	if (!take(e, ')') || (fn == CLAMP && (args != 3 || !emit(e, M_CLAMP, 0, 0)))) return INVALID;
	e->depth--;
	return d;
}

static enum dim atom(struct expression *e)
{
	const css_token *t = peek(e);
	if (!t) return INVALID;
	if (take(e, '(')) return arguments(e, CALC);
	enum function fn = math_function(t);
	if (fn != UNKNOWN) {
		parserutils_vector_iterate(e->tokens, &e->pos);
		return arguments(e, fn);
	}
	if (t->type == CSS_TOKEN_NUMBER) {
		size_t used = 0;
		css_fixed value = css__number_from_lwc_string(t->idata, false, &used);
		if (used != lwc_string_length(t->idata) || !emit(e, M_NUMBER, 0, value)) return INVALID;
		parserutils_vector_iterate(e->tokens, &e->pos);
		return SCALAR;
	}
	uint32_t unit;
	css_fixed value;
	if ((t->type != CSS_TOKEN_DIMENSION && t->type != CSS_TOKEN_PERCENTAGE) ||
	    css__parse_unit_specifier(e->css, e->tokens, &e->pos, UNIT_PX, &value, &unit) != CSS_OK ||
	    !(unit & (UNIT_LENGTH | UNIT_PCT)) || !emit(e, M_LENGTH, css__to_css_unit(unit), value))
		return INVALID;
	return LENGTH;
}

static enum dim product(struct expression *e)
{
	enum dim d = atom(e);
	while (d) {
		int32_t pos = e->pos;
		const css_token *op = peek(e);
		bool div = op && tokenIsChar(op, '/');
		if (!op || (!div && !tokenIsChar(op, '*'))) { e->pos = pos; break; }
		parserutils_vector_iterate(e->tokens, &e->pos);
		enum dim r = atom(e);
		if (!r || (div && r != SCALAR) || (!div && d == LENGTH && r == LENGTH) ||
		    !emit(e, div ? M_DIV : M_MUL, 0, 0)) return INVALID;
		if (r == LENGTH) d = LENGTH;
	}
	return d;
}

static enum dim math_sum(struct expression *e)
{
	enum dim d = product(e);
	while (d) {
		const css_token *before = parserutils_vector_peek(e->tokens, e->pos);
		const css_token *op = peek(e);
		bool minus = op && tokenIsChar(op, '-');
		if (!op || (!minus && !tokenIsChar(op, '+'))) break;
		parserutils_vector_iterate(e->tokens, &e->pos);
		const css_token *after = parserutils_vector_peek(e->tokens, e->pos);
		if (!before || before->type != CSS_TOKEN_S || !after || after->type != CSS_TOKEN_S ||
		    product(e) != d || !emit(e, minus ? M_SUB : M_ADD, 0, 0)) return INVALID;
	}
	return d;
}

css_error css__parse_math_program(css_language *css, const parserutils_vector *v, int32_t *ctx,
		struct css_math_token *out, size_t capacity, size_t *count)
{
	struct expression e = {.css=css, .tokens=v, .pos=*ctx};
	enum function fn = math_function(peek(&e));
	if (fn == UNKNOWN) return CSS_INVALID;
	parserutils_vector_iterate(v, &e.pos);
	if (arguments(&e, fn) != LENGTH || e.count > capacity) return CSS_INVALID;
	memcpy(out, e.code, e.count * sizeof(*out));
	*count = e.count;
	*ctx = e.pos;
	return CSS_OK;
}

static css_error property(css_language *css, const parserutils_vector *v, int32_t *ctx,
		css_style *result, opcode_t prop, css_prop_handler simple)
{
	int32_t pos = *ctx;
	consumeWhitespace(v, &pos);
	if (math_function(parserutils_vector_peek(v, pos)) == UNKNOWN) return simple(css,v,ctx,result);
	struct css_math_token code[CSS_MATH_LIMIT];
	size_t count;
	if (css__parse_math_program(css, v, &pos, code, CSS_MATH_LIMIT, &count) != CSS_OK) return CSS_INVALID;
	lwc_string *value = NULL;
	if (lwc_intern_string((const char *)code, count * sizeof(*code), &value) != lwc_error_ok)
		return CSS_NOMEM;
	uint32_t id;
	css_error err = css__stylesheet_string_add(css->sheet, value, &id);
	if (err == CSS_OK) err = css__stylesheet_style_appendOPV(result, prop, 0, CSS_MATH_OPV);
	if (err == CSS_OK) err = css__stylesheet_style_append(result, id);
	if (err == CSS_OK) *ctx = pos;
	return err;
}

#define MATH_PROPERTY(n, p) \
css_error css__parse_math_##n(css_language *c, const parserutils_vector *v, int32_t *i, css_style *s) \
{ return property(c, v, i, s, p, css__parse_##n); }
MATH_PROPERTY(width, CSS_PROP_WIDTH)
MATH_PROPERTY(min_width, CSS_PROP_MIN_WIDTH)
MATH_PROPERTY(max_width, CSS_PROP_MAX_WIDTH)
