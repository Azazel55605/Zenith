/* Private serialization shared between the grid parser and cascade. String
 * payloads follow the header immediately; never assume lwc data alignment. */
#ifndef LIBCSS_GRID_VALUES_H
#define LIBCSS_GRID_VALUES_H
#include <stdint.h>
struct grid_line_value {
	int32_t number;
	uint32_t span;
	/* Optional custom-ident in the remaining bytes. */
};
struct grid_areas_header {
	uint16_t columns, rows;
};
struct grid_named_area {
	uint16_t col_start, col_end, row_start, row_end;
	uint32_t length;
};
#endif
