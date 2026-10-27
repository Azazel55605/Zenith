#ifndef ZENITH_UI_TEXT_H
#define ZENITH_UI_TEXT_H
#include "ui.h"
/* Caller-owned storage, always NUL terminated. Cursor offsets are UTF-8 boundaries.
 * Rendering may substitute unsupported glyphs; editing never rewrites other bytes. */
struct zui_input { char *text; unsigned int capacity,length,cursor; };
int zui_input_init(struct zui_input *input,char *buffer,unsigned int capacity);
int zui_input_event(struct zui_input *input,const struct zenith_gui_event *event);
void zui_input_draw(struct zui *ui,struct zui_rect rect,const struct zui_input *input,int focused);
/* Word wraps bounded text inside rect; scroll is in lines; returns total lines. */
int zui_paragraph(struct zui *ui,struct zui_rect rect,const char *text,const struct zui_font *font,unsigned int color,int scroll);
#endif
