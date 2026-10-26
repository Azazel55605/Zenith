#ifndef ZENITH_GRAPHICS_H
#define ZENITH_GRAPHICS_H
#include "../sunbeam/zenith.h"
/* ABI 1, x86_64 little endian. Full contract: docs/GRAPHICAL-CLIENTS.md.
 * Handles are invocation-local; no display/device address is ever returned. */
struct zenith_surface_request {
    unsigned int version,flags,width,height;
    unsigned long title,length,reserved[2];
};
struct zenith_gui_event { int type,code,x,y,modifiers,extra; };
_Static_assert(sizeof(struct zenith_surface_request)==48,"surface request layout");
_Static_assert(sizeof(struct zenith_gui_event)==24,"event layout");
enum { Z_GUI_NONE=0,Z_GUI_CLOSE=1,Z_GUI_FOCUS=2,Z_GUI_KEY=3,Z_GUI_DOWN=4,
       Z_GUI_UP=5,Z_GUI_DRAG=6,Z_GUI_RESET=7,Z_GUI_SCROLL=8 };
static inline long zenith_gui_open(unsigned int width,unsigned int height,const char *title,unsigned long length) {
    struct zenith_surface_request r={1,0,width,height,(unsigned long)title,length,{0,0}};
    return zenith_call(600,(unsigned long)&r,sizeof(r),0);
}
/* Complete opaque XRGB8888 frame, exact width*height*4 bytes. */
static inline long zenith_gui_present(long handle,const unsigned int *pixels,unsigned long bytes) {
    return zenith_call(601,(unsigned long)handle,(unsigned long)pixels,bytes);
}
/* 1 event, 0 no event (yields briefly), negative error. */
static inline long zenith_gui_poll(long handle,struct zenith_gui_event *event) {
    return zenith_call(602,(unsigned long)handle,(unsigned long)event,sizeof(*event));
}
static inline long zenith_gui_close(long handle) { return zenith_call(603,(unsigned long)handle,0,0); }
#endif
