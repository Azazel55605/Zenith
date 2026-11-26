#ifndef NEBULA_START_PAGE_H
#define NEBULA_START_PAGE_H

#include "utils/errors.h"

#define NEBULA_START_URL "nebula:start"
struct nebula_profile;
struct zui_theme;

/* References stay valid through netsurf_exit. Read the current profile on each navigation. */
nserror nebula_start_register(const struct nebula_profile *profile, const struct zui_theme *theme);

#endif
