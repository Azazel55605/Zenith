/* Private compiled representation shared by LibCSS parsing and selection. */
#ifndef LIBCSS_MATH_VALUES_H
#define LIBCSS_MATH_VALUES_H
#include <libcss/types.h>
enum { CSS_MATH_LIMIT = 128, CSS_MATH_DEPTH = 32, CSS_MATH_OPV = 0x81 };
enum css_math_op { M_NUMBER, M_LENGTH, M_ADD, M_SUB, M_MUL, M_DIV, M_MIN, M_MAX, M_CLAMP };
struct css_math_token {
	uint16_t op, unit;
	css_fixed value;
};
#endif
