/* SPDX-License-Identifier: GPL-2.0-only */
#ifndef ZENITH_DOOM_LAUNCHER_H
#define ZENITH_DOOM_LAUNCHER_H
#include "../ui/ui.h"

enum { DOOM_WINDOW_WIDTH = 640, DOOM_WINDOW_HEIGHT = 480 };
#define DOOM_WINDOW_PIXELS ((unsigned long)DOOM_WINDOW_WIDTH * DOOM_WINDOW_HEIGHT)
#define DOOM_PATH_BYTES 1025

/* The callback loads a WAD, returning a displayable error or NULL on success. */
int doom_launcher(long window, struct zui *ui, char **envp, const char *start,
                  const char *(*load)(const char *));
#endif
