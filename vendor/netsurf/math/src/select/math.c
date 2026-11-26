/* Evaluate validated length expressions when their containing block is known. */
#include <limits.h>
#include <string.h>
#include <libcss/math.h>
#include "bytecode/bytecode.h"
#include "bytecode/opcodes.h"
#include "select/math.h"
#include "select/helpers.h"
#include "math_values.h"

css_error css__cascade_width_math(uint32_t opv, css_style *s, css_select_state *state,
		css_error (*set)(css_computed_style *, uint8_t, css_fixed_or_calc, css_unit))
{
	uint8_t type = CSS_WIDTH_INHERIT;
	css_fixed_or_calc value = {.value = 0};
	css_unit unit = CSS_UNIT_PX;
	if (!hasFlagValue(opv)) {
		switch (getValue(opv)) {
		case WIDTH_SET:
			type = CSS_WIDTH_SET;
			value.value = *((css_fixed *)s->bytecode);
			advance_bytecode(s, sizeof(value.value));
			unit = css__to_css_unit(*((uint32_t *)s->bytecode));
			advance_bytecode(s, sizeof(uint32_t));
			break;
		case CSS_MATH_OPV:
			type = CSS_WIDTH_SET;
			unit = CSS_UNIT_CALC;
			css__stylesheet_string_get(s->sheet, *((uint32_t *)s->bytecode), &value.calc);
			advance_bytecode(s, sizeof(uint32_t));
			break;
		default:
			type = CSS_WIDTH_AUTO;
		}
	}
	return css__outranks_existing(getOpcode(opv), isImportant(opv), state, getFlagValue(opv)) ?
			set(state->computed, type, value, unit) : CSS_OK;
}

bool css_math_program_px(const void *program, size_t len, const css_computed_style *style,
		const css_unit_ctx *units, int available, int *out)
{
	if (len % sizeof(struct css_math_token) || len / sizeof(struct css_math_token) > CSS_MATH_LIMIT) return false;
	size_t top = 0;
	const char *data = program;
	double stack[CSS_MATH_LIMIT];
	for (size_t i = 0; i < len; i += sizeof(struct css_math_token)) {
		struct css_math_token instruction;
		memcpy(&instruction, data+i, sizeof(instruction));
		double v = (double)instruction.value / F_1;
		if (instruction.op == M_NUMBER || instruction.op == M_LENGTH) {
			if (instruction.op == M_LENGTH) {
				if (instruction.unit == CSS_UNIT_PCT) {
					if (available < 0) return false;
					v = v * available / 100;
				} else {
					v = css__device_pixels(style, units, instruction.value, instruction.unit);
				}
			}
			stack[top++] = v;
			continue;
		}
		if (top < (instruction.op == M_CLAMP ? 3u : 2u)) return false;
		double r = stack[--top], l = stack[top-1];
		switch (instruction.op) {
		case M_ADD: l += r; break;
		case M_SUB: l -= r; break;
		case M_MUL: l *= r; break;
		case M_DIV: l /= r; break;
		case M_MIN: if (l > r || r != r) l = r; break;
		case M_MAX: if (l < r || r != r) l = r; break;
		case M_CLAMP: {
			top--;
			double preferred = l, lower = stack[top-1];
			l = (preferred < r || preferred != preferred) ? preferred : r;
			if (l < lower || lower != lower) l = lower;
			break;
		}
		default: return false;
		}
		stack[top-1] = l;
	}
	if (top != 1) return false;
	*out = css__used_pixels(stack[0]);
	return true;
}

bool css__math_pixels(const lwc_string *program, const css_computed_style *style,
		const css_unit_ctx *units, int available, int *out)
{
	return css_math_program_px(lwc_string_data(program), lwc_string_length(program), style, units, available, out);
}
