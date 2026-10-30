#ifndef ZENITH_UI_H
#define ZENITH_UI_H
#include "../include/zenith_graphics.h"
/* Client-only library v1. Owns no host state or handles. Colors are RGB; targets
 * are legacy XRGB or explicitly initialized premultiplied foreground planes. */
#define ZUI_API_VERSION 1
#define ZUI_MAX_CONTROLS 128
#define ZUI_MAX_TEXT_BYTES 4096
struct zui_theme { unsigned int surface,raised,subtle,border,text,secondary,accent,on_accent; };
/* width/height/ascent describe the glyph cell. Each glyph stores only its ink box:
 * boxes[4*g] = x,y,w,h within the cell; pixels+offsets[g] holds w*h coverage bytes. */
struct zui_font { unsigned short width,height,ascent; const unsigned char *advance,*pixels,*boxes; const unsigned int *offsets; };
extern const struct zui_font zui_body,zui_label,zui_heading,zui_caption;
struct zui_rect { int x,y,w,h; };
/* Stable surface intent; current XRGB painting is always opaque. Candidate means
 * future host-negotiated composition is possible, never authority to sample it. */
enum { ZUI_SURFACE_CONTENT=1,ZUI_SURFACE_CONTROL=2,ZUI_SURFACE_NAVIGATION=3,
       ZUI_SURFACE_PANEL=4,ZUI_SURFACE_POPOVER=5 };
/* Host key extras added with pointer motion; older kernels never send them. */
enum { ZUI_KEY_PAGE_UP=12,ZUI_KEY_PAGE_DOWN=13 };
struct zui;
int zui_surface(struct zui *ui,struct zui_rect rect,int radius,int role);
int zui_backdrop_candidate(int role);
enum { ZUI_BUTTON=1,ZUI_TOGGLE=2,ZUI_CHOICE=3,ZUI_SEGMENT=4,ZUI_SWATCH=5,ZUI_NAVIGATION=6 };
/* icon/detail/no_chevron: optional ZUI_NAVIGATION decorations painted with the row;
 * a ZUI_BUTTON with an icon is drawn as a field (leading label, trailing 16 px icon). */
struct zui_control { int id,kind,enabled,value,swatch; struct zui_rect rect,clip; const char *label,*icon,*detail; int no_chevron; };
struct zui {
    unsigned int *pixels; int width,height;
    struct zui_theme theme;
    unsigned int swatches[6];
    struct zui_rect clip;
    struct zui_control controls[ZUI_MAX_CONTROLS];
    int count,focus,pressed,window_focused,dirty;
    int focus_visible; /* last focus move came from the keyboard: draw the ring */
    int hover,hover_supported,pointer_x,pointer_y; /* hover: control under the pointer or -1 */
    int foreground,material_count,full_background;
    int theme_preview; /* theme differs from the host's, whose tint would mismatch: no materials */
    struct zenith_material_region materials[8];
};
/* Returns 0 for invalid dimensions/buffer; caller supplies width*height pixels. */
int zui_init(struct zui *ui,unsigned int *pixels,int width,int height);
int zui_resize(struct zui *ui,int width,int height,unsigned long capacity);
struct zui_workspace { struct zui_rect sidebar,content; };
struct zui_workspace zui_workspace_begin(struct zui *ui,int sidebar_width);
int zui_scroll_step(int value,int delta,int maximum);
/* Separate premultiplied foreground target. Begin clears coverage, not a whole
 * window's opacity; paint opaque content/control roles over declared materials. */
void zui_begin_material_frame(struct zui *ui);
int zui_material_surface(struct zui *ui,struct zui_rect rect,int radius,int role,int effect);
void zui_appearance(struct zui *ui,int light,int accent);
void zui_clip(struct zui *ui,struct zui_rect rect);
void zui_fill(struct zui *ui,struct zui_rect rect,unsigned int color);
void zui_round_alpha(struct zui *ui,struct zui_rect rect,int radius,unsigned int color,unsigned int opacity);
void zui_round(struct zui *ui,struct zui_rect rect,int radius,unsigned int color);
/* Up to 4096 UTF-8 input bytes per call. Unsupported/malformed characters render '?'. No shaping yet. */
void zui_text(struct zui *ui,int x,int y,const char *text,unsigned long bytes,
              const struct zui_font *font,unsigned int color);
/* Shared 24px inset / 36px control / 8px radius geometry. Row help stays
 * separate from the opaque control so future panel materials do not fade it. */
void zui_setting_row(struct zui *ui,struct zui_rect rect,const char *label,const char *help);
void zui_swatch_caption(struct zui *ui,int x,int y);
void zui_segment_track(struct zui *ui,struct zui_rect rect);
unsigned int zui_accent_color(const struct zui *ui,int index);
int zui_measure(const char *text,unsigned long bytes,const struct zui_font *font);
/* System-wide icons: /usr/share/zenith/icons/<name>.zic, installed by zenith-icons or by
 * any package adding its own name. Names are 1-31 of [a-z0-9-]. The source hook reads one
 * file (bytes read, or negative); files load once into a fixed cache. Native programs
 * call zui_icons_use_packages(). A missing or malformed icon draws nothing. */
void zui_icon_source(long (*read)(const char *name,unsigned char *out,unsigned long capacity));
void zui_icons_use_packages(void);
/* Tint roles: normal accent, accent contrast on selected rows, muted disabled, body text. */
enum { ZUI_ICON_NORMAL=0,ZUI_ICON_SELECTED=1,ZUI_ICON_DISABLED=2,ZUI_ICON_TEXT=3 };
/* Draws the named mask at (x,y) within the clip; 0 for an unknown name or size. */
int zui_icon(struct zui *ui,const char *name,int x,int y,int size,int role);
/* Positive unique IDs; labels remain owned by caller and must outlive the control.
 * clip records a narrowed viewport active at registration (empty = whole window);
 * rect within it is the hit and draw area, so callers may still move rect later. A control with no visible part
 * when added is not registered and returns 0. */
int zui_add_swatch(struct zui *ui,int id,struct zui_rect rect,const char *label,int accent,int selected,int enabled);
int zui_add(struct zui *ui,int id,int kind,struct zui_rect rect,const char *label,int value,int enabled);
/* Returns activated ID or 0. Application owns values; disabled controls never activate.
 * Forward focus/reset/close events, too; events never modify saved preferences. */
int zui_event(struct zui *ui,const struct zenith_gui_event *event);
/* Visible hit/draw area of control index (rect within its viewport); empty if out of range. */
struct zui_rect zui_control_area(const struct zui *ui,int index);
/* Recompute hover from the last pointer position after rebuilding controls. */
void zui_update_hover(struct zui *ui);
void zui_draw_controls(struct zui *ui);
void zui_draw_range(struct zui *ui,int first,int count);
#endif
