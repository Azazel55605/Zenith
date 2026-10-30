#ifndef ZENITH_UI_WIDGETS_H
#define ZENITH_UI_WIDGETS_H
#include "ui.h"
/* Composite widgets over the immediate-mode toolkit. Applications own every state
 * struct below; the toolkit never allocates or keeps pointers between frames.
 *
 * Frame order: lay out the page (including combo fields), call zui_draw_controls,
 * then draw overlays (zui_combo_popup, zui_dialog_draw). Overlays paint their own
 * surface and controls, so page controls never cover them. Route events to an open
 * dialog first, then open combos, then page widgets. */

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

/* Combo box: a field (ZUI_BUTTON with id) showing the current item and a chevron;
 * when open, a popup list of up to 8 rows kept inside the window (below the field
 * when it fits, otherwise above). zui_combo_draw registers the field during layout;
 * zui_combo_popup is an overlay drawn after zui_draw_controls. */
struct zui_combo { int id,open,value; struct zui_list popup; struct zui_rect field,panel; };
void zui_combo_draw(struct zui *ui,struct zui_combo *c,struct zui_rect field,int count,zui_list_source items,void *context);
void zui_combo_popup(struct zui *ui,struct zui_combo *c,int count,zui_list_source items,void *context);
/* Returns value+1 when a choice is committed. Esc or a click outside cancels. An open
 * combo consumes pointer and key events. */
int zui_combo_event(struct zui *ui,struct zui_combo *c,const struct zenith_gui_event *e,zui_list_source items,void *context);

/* Form layout: a column centered in area, at most 640 px wide. Wide windows put
 * right-aligned labels in a 40% column beside the control; windows narrower than
 * 720 px stack the label above it. Help text goes below the control. content_top is
 * the scrolled y origin from zui_scroll_begin. */
struct zui_form { struct zui_rect area; int top,y,label_w,narrow,x,w; };
void zui_form_begin(struct zui_form *f,struct zui_rect area,int content_top,int window_width);
/* Draws a section heading with a rule; returns its content y. */
int zui_form_section(struct zui *ui,struct zui_form *f,const char *title);
/* Draws the label (and help) and returns the control rect; width is clamped. */
struct zui_rect zui_form_row(struct zui *ui,struct zui_form *f,const char *label,const char *help,int control_w,int control_h);
/* Laid-out height so far, for zui_scroll_end. */
int zui_form_height(const struct zui_form *f,int content_top);

/* Uniform footer: [Help] ... Defaults · Reset · Apply, ids base_id+ZUI_FOOTER_*.
 * Apply/Reset are enabled only when changed; Defaults only when not at defaults.
 * Returns the bar height (56). */
enum { ZUI_FOOTER_HELP=1,ZUI_FOOTER_DEFAULTS=2,ZUI_FOOTER_RESET=3,ZUI_FOOTER_APPLY=4 };
int zui_footer(struct zui *ui,struct zui_rect bar,int base_id,int help,int changed,int at_defaults);

/* Modal dialog over a dimmed window with two or three buttons (ids base_id+1..count).
 * An overlay: draw it after zui_draw_controls. While open it owns input: focus starts on button 1 and
 * Tab stays within the dialog, Esc chooses the last button (Cancel), and controls
 * behind it cannot activate. */
struct zui_dialog { int open,base_id; const char *title,*message,*labels[3]; int count; };
void zui_dialog_draw(struct zui *ui,const struct zui_dialog *d);
/* Returns the chosen button number (1..count) and closes, else 0. */
int zui_dialog_event(struct zui *ui,struct zui_dialog *d,const struct zenith_gui_event *e);
#endif
