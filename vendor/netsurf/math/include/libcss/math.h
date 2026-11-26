#ifndef LIBCSS_MATH_H
#define LIBCSS_MATH_H
#include <libcss/unit.h>
/* Resolve a validated LibCSS length-percentage program at used-value time.
 * available < 0 means percentages cannot be resolved in this measurement. */
bool css_math_program_px(const void *program, size_t bytes, const css_computed_style *style,
		const css_unit_ctx *units, int available, int *out);
#endif
