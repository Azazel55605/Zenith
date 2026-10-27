#ifndef ZENITH_UI_H
#define ZENITH_UI_H
#include "../include/zenith_graphics.h"
/* Client-only library v1. Owns no host state or syscall handles. All colors XRGB. */
#define ZUI_API_VERSION 1
#define ZUI_MAX_CONTROLS 32
#define ZUI_MAX_TEXT_BYTES 4096
struct zui_theme { unsigned int surface,raised,subtle,border,text,secondary,accent,on_accent; };
struct zui_font { unsigned short width,height,ascent; const unsigned char *advance,*pixels; };
extern const struct zui_font zui_body,zui_label,zui_heading,zui_caption;
struct zui_rect { int x,y,w,h; };
enum { ZUI_BUTTON=1,ZUI_TOGGLE=2,ZUI_CHOICE=3 };
struct zui_control { int id,kind,enabled,value; struct zui_rect rect; const char *label; };
struct zui {
    unsigned int *pixels; int width,height;
    struct zui_theme theme;
    struct zui_rect clip;
    struct zui_control controls[ZUI_MAX_CONTROLS];
    int count,focus,pressed,window_focused,dirty;
};
/* Returns 0 for invalid dimensions/buffer; caller supplies width*height pixels. */
int zui_init(struct zui *ui,unsigned int *pixels,int width,int height);
void zui_appearance(struct zui *ui,int light,int accent);
void zui_clip(struct zui *ui,struct zui_rect rect);
void zui_fill(struct zui *ui,struct zui_rect rect,unsigned int color);
void zui_round(struct zui *ui,struct zui_rect rect,int radius,unsigned int color);
/* Up to 4096 UTF-8 input bytes per call. Unsupported/malformed characters render '?'. No shaping yet. */
void zui_text(struct zui *ui,int x,int y,const char *text,unsigned long bytes,
              const struct zui_font *font,unsigned int color);
int zui_measure(const char *text,unsigned long bytes,const struct zui_font *font);
/* Positive unique IDs; labels remain owned by caller and must outlive the control. */
int zui_add(struct zui *ui,int id,int kind,struct zui_rect rect,const char *label,int value,int enabled);
/* Returns activated ID or 0. Application owns values; disabled controls never activate.
 * Forward focus/reset/close events, too; events never modify saved preferences. */
int zui_event(struct zui *ui,const struct zenith_gui_event *event);
void zui_draw_controls(struct zui *ui);
#endif
