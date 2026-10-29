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

/* Virtualized single-selection list. items(context,index,&item) supplies rows lazily,
 * so only visible rows cost work or regions. Rows register as ZUI_NAVIGATION with id
 * base_id+index. Row height defaults to 40 (36 + 4 gap). */
struct zui_list_item { const char *icon,*label,*subtitle; int chevron,enabled; };
typedef int (*zui_list_source)(void *context,int index,struct zui_list_item *out);
struct zui_list { struct zui_scroll scroll; int selected,count,row_height,base_id,first_control,visible_controls; };
void zui_list_draw(struct zui *ui,struct zui_list *l,struct zui_rect rect,int count,zui_list_source items,void *context);
/* Returns index+1 when a row is activated (click release or Enter), else 0. Handles
 * Up/Down/Home/End, first-letter type-ahead and the embedded scroll area. */
int zui_list_event(struct zui *ui,struct zui_list *l,const struct zenith_gui_event *e,zui_list_source items,void *context);
#endif
