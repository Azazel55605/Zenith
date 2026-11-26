#ifndef LIBCSS_SELECT_MATH_H
#define LIBCSS_SELECT_MATH_H
#include "select/computed.h"
#include "select/properties/properties.h"
css_error css__cascade_width_math(uint32_t, css_style *, css_select_state *,
		css_error (*)(css_computed_style *, uint8_t, css_fixed_or_calc, css_unit));
/* Ordinary widths and math expressions share the fixed-point layout range
 * and round once, when the final used value becomes an integer width. */
static inline int css__used_pixels(double value)
{
	const int limit = FIXTOINT(INT_MAX);
	return !(value > 0) ? 0 : value >= limit ? limit : (int)(value + 0.5);
}
double css__device_pixels(const css_computed_style *, const css_unit_ctx *, css_fixed, css_unit);
bool css__math_pixels(const lwc_string *, const css_computed_style *, const css_unit_ctx *, int, int *);
#endif
