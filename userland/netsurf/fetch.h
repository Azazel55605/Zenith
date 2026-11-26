#ifndef ZENITH_NETSURF_FETCH_H
#define ZENITH_NETSURF_FETCH_H
#include "utils/errors.h"

/* Register once after netsurf_init, before navigating the first window. */
nserror zenith_fetch_register(void);
#endif
