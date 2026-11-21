/* SPDX-License-Identifier: GPL-2.0-only */
#ifndef ZENITH_DOOM_ENGINE_H
#define ZENITH_DOOM_ENGINE_H
#include "upstream/PureDOOM.h"

/* The engine's ring has no full check. Return zero before an edge could overwrite it. */
int zenith_doom_key(doom_key_t key, int pressed);
void zenith_doom_reset_keys(void);

#endif
