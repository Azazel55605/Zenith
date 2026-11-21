/* SPDX-License-Identifier: GPL-2.0-only */
#define DOOM_IMPLEMENTATION
#include "engine.h"

int zenith_doom_key(doom_key_t key, int pressed)
{
    if (((eventhead + 1) & (MAXEVENTS - 1)) == eventtail) return 0;
    event_t event = {pressed ? ev_keydown : ev_keyup, (int)key, 0, 0};
    D_PostEvent(&event);
    return 1;
}

void zenith_doom_reset_keys(void)
{
    /* Drop unread edges as well as keys the game already applied. A release
     * enqueued here could otherwise be delayed behind an engine screen wipe. */
    eventtail = eventhead;
    doom_memset(gamekeydown, 0, sizeof(gamekeydown));
    turnheld = 0;
    m_paninc.x = m_paninc.y = 0;
    mtof_zoommul = ftom_zoommul = FRACUNIT;
}
