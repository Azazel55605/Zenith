#ifndef LIBCSS_PARSE_MATH_H
#define LIBCSS_PARSE_MATH_H
#include "parse/properties/properties.h"
#include "math_values.h"
css_error css__parse_math_program(css_language *, const parserutils_vector *, int32_t *,
		struct css_math_token *, size_t capacity, size_t *count);
css_error css__parse_math_width(css_language *, const parserutils_vector *, int32_t *, css_style *);
css_error css__parse_math_min_width(css_language *, const parserutils_vector *, int32_t *, css_style *);
css_error css__parse_math_max_width(css_language *, const parserutils_vector *, int32_t *, css_style *);
#endif
