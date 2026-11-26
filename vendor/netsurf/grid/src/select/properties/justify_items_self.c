/* Item alignment on the inline axis shares the computed values of the
 * corresponding block-axis property. Each keyword occupies one opcode. */
#include "bytecode/bytecode.h"
#include "select/propset.h"
#include "select/propget.h"
#include "select/properties/properties.h"
#include "select/properties/helpers.h"
#include <libcss/grid.h>

#define INLINE_ALIGNMENT(name, initial) \
css_error css__cascade_##name(uint32_t opv, css_style *s, css_select_state *state) \
{ \
	(void)s; \
	if (!css__outranks_existing(getOpcode(opv), isImportant(opv), state, getFlagValue(opv))) return CSS_OK; \
	return set_##name(state->computed, hasFlagValue(opv) ? 0 : getValue(opv)); \
} \
css_error css__initial_##name(css_select_state *state) \
{ return set_##name(state->computed, initial); } \
css_error css__set_##name##_from_hint(const css_hint *hint, css_computed_style *s) \
{ return set_##name(s, hint->status); } \
css_error css__copy_##name(const css_computed_style *from, css_computed_style *to) \
{ return from == to ? CSS_OK : set_##name(to, get_##name(from)); } \
css_error css__compose_##name(const css_computed_style *parent, const css_computed_style *child, css_computed_style *result) \
{ return css__copy_##name(get_##name(child) ? child : parent, result); } \
uint8_t css_computed_##name(const css_computed_style *s) { return get_##name(s); }

INLINE_ALIGNMENT(justify_items, CSS_ALIGN_ITEMS_STRETCH)
INLINE_ALIGNMENT(justify_self, CSS_ALIGN_SELF_AUTO)
