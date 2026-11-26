/* Box alignment shorthands set the block axis followed by the inline axis. */
#include "parse/properties/properties.h"
#include "parse/properties/utils.h"

static css_error place(css_language *c, const parserutils_vector *v, int32_t *ctx,
		css_style *result, css_prop_handler block, css_prop_handler in_line)
{
	int32_t first = *ctx, pos = first;
	css_error err = block(c, v, &pos, result);
	if (err != CSS_OK) return err;
	consumeWhitespace(v, &pos);
	const css_token *second = parserutils_vector_peek(v, pos);
	if (second && !tokenIsChar(second, '!')) {
		const css_token *start = parserutils_vector_peek(v, first);
		if (get_css_flag_value(c, start) != FLAG_VALUE__NONE ||
		    get_css_flag_value(c, second) != FLAG_VALUE__NONE) return CSS_INVALID;
	} else {
		pos = first; /* A single component sets both axes. */
	}
	err = in_line(c, v, &pos, result);
	if (err == CSS_OK) *ctx = pos;
	return err;
}

#define PLACE_PROPERTY(name) \
css_error css__parse_place_##name(css_language *c, const parserutils_vector *v, int32_t *ctx, css_style *s) \
{ return place(c, v, ctx, s, css__parse_align_##name, css__parse_justify_##name); }

PLACE_PROPERTY(items)
PLACE_PROPERTY(self)
PLACE_PROPERTY(content)
