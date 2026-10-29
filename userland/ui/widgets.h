#ifndef ZENITH_UI_WIDGETS_H
#define ZENITH_UI_WIDGETS_H
#include "ui.h"
/* Composite widgets over the immediate-mode toolkit. Applications own every state
 * struct below; the toolkit never allocates or keeps pointers between frames. */

/* Vertical scroll area. Wheel: -code/2 px per event, clamped to +/-120. The overlay
 * scrollbar appears only on overflow; the thumb drags and the track pages. */
struct zui_scroll { struct zui_rect rect,saved_clip; int offset,content,dragging,drag_y,drag_offset; };
/* Clips to rect (within the current clip) and returns the y of content row 0. */
int zui_scroll_begin(struct zui *ui,struct zui_scroll *s,struct zui_rect rect);
/* Records content height, clamps offset, draws the scrollbar and restores the clip. */
void zui_scroll_end(struct zui *ui,struct zui_scroll *s,int content_height);
/* Returns 1 when the offset changed. */
int zui_scroll_event(struct zui *ui,struct zui_scroll *s,const struct zenith_gui_event *e);
/* Scrolls minimally so content [top,top+height) is visible. */
void zui_scroll_reveal(struct zui_scroll *s,int top,int height);
int zui_scroll_maximum(const struct zui_scroll *s);
#endif
