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
/* Interactive ABI 1: renews bounded service/CPU/IO quotas every minute only
 * while this invocation owns a live window. Legacy clients remain finite. */
static inline long zenith_gui_interactive(long handle){return zenith_call(605,(unsigned long)handle,1,0);}
/* Opt-in materials extension. Query first; ABI 1 remains forced opaque. */
struct zenith_material_caps { unsigned int version,features,max_regions,max_radius,max_area,blur_radius,max_width,max_height; };
struct zenith_material_region { int x,y,w,h,radius,role,effect,reserved; };
struct zenith_material_frame { unsigned int version,flags; unsigned long pixels,length,regions; unsigned int count,reserved; unsigned long reserved2; };
_Static_assert(sizeof(struct zenith_material_frame)==48,"material frame layout");
_Static_assert(sizeof(struct zenith_material_region)==32,"material region layout");
static inline long zenith_gui_material_caps(struct zenith_material_caps *caps){return zenith_call(607,(unsigned long)caps,sizeof(*caps),0);}
static inline long zenith_gui_open_material(unsigned int width,unsigned int height,const char *title,unsigned long length){
    struct zenith_surface_request r={2,1,width,height,(unsigned long)title,length,{0,0}};
    return zenith_call(600,(unsigned long)&r,sizeof(r),0);
}
static inline long zenith_gui_present_material(long handle,const unsigned int *pixels,unsigned long length,const struct zenith_material_region *regions,unsigned int count){
    struct zenith_material_frame r={1,0,(unsigned long)pixels,length,count?(unsigned long)regions:0,count,0,0};
    return zenith_call(606,(unsigned long)handle,(unsigned long)&r,sizeof(r));
}
/* Viewport ABI 1: explicit opt-in; legacy surfaces remain fixed. */
#define Z_GUI_RESIZE 10
struct zenith_viewport { unsigned int version,flags,width,height; unsigned long generation; unsigned int max_width,max_height; };
struct zenith_resize_request { unsigned int version,flags,width,height; unsigned long generation; };
_Static_assert(sizeof(struct zenith_viewport)==32,"viewport snapshot");
_Static_assert(sizeof(struct zenith_resize_request)==24,"resize acknowledgement");
static inline long zenith_gui_resize_enable(long handle){return zenith_call(614,(unsigned long)handle,1,0);}
/* Opt in to pointer motion (event 11; x=y=-1 on leave). Older kernels return -38. */
#define Z_GUI_MOVE 11
/* Terminal sessions (kernel 0.0.41): a kernel-owned shell with the caller's credentials. Needs a live surface. */
struct zenith_terminal_key { int code,modifiers,extra; };
static inline long zenith_terminal_open(int columns){return zenith_call(630,(unsigned long)columns,0,0);}
static inline long zenith_terminal_key(long handle,const struct zenith_terminal_key *k){return zenith_call(631,(unsigned long)handle,(unsigned long)k,sizeof(*k));}
static inline long zenith_terminal_read(long handle,char *buffer,unsigned long capacity){return zenith_call(632,(unsigned long)handle,(unsigned long)buffer,capacity);}
#define Z_TERMINAL_STATE_BYTES 4096
enum { Z_TERMINAL_BUSY=1,Z_TERMINAL_SECRET=2,Z_TERMINAL_RAW=4,Z_TERMINAL_EXIT=8,Z_TERMINAL_DROPPED=16 };
/* version,flags,cursor,prompt bytes,input bytes,directory bytes, then that UTF-8 text in order */
static inline long zenith_terminal_state(long handle,void *buffer){return zenith_call(633,(unsigned long)handle,(unsigned long)buffer,Z_TERMINAL_STATE_BYTES);}
static inline long zenith_terminal_resize(long handle,int columns){return zenith_call(634,(unsigned long)handle,(unsigned long)columns,0);}
static inline long zenith_terminal_close(long handle){return zenith_call(635,(unsigned long)handle,0,0);}
/* The clipboard stays in the kernel: a client can set it, and paste it into its own session, without reading it. */
static inline long zenith_terminal_copy(long handle,const char *text,unsigned long bytes){return zenith_call(636,(unsigned long)handle,(unsigned long)text,bytes);}
static inline long zenith_terminal_paste(long handle){return zenith_call(637,(unsigned long)handle,0,0);}
/* Asks the desktop to crossfade this rectangle from the last frame into the next present,
 * the new content rising `rise` pixels into place. -38 before kernel 0.0.38. */
struct zenith_gui_fade_request { int x,y,width,height,rise,reserved; };
static inline long zenith_gui_fade(long handle,int x,int y,int width,int height,int rise){
    struct zenith_gui_fade_request r={x,y,width,height,rise,0};return zenith_call(629,(unsigned long)handle,(unsigned long)&r,sizeof(r));
}
static inline long zenith_gui_pointer_motion(long handle){return zenith_call(627,(unsigned long)handle,1,0);}
static inline long zenith_gui_viewport(long handle,struct zenith_viewport *v){return zenith_call(613,(unsigned long)handle,(unsigned long)v,sizeof(*v));}
static inline long zenith_gui_resize_accept(long handle,const struct zenith_viewport *v){
    struct zenith_resize_request r={1,0,v->width,v->height,v->generation};return zenith_call(615,(unsigned long)handle,(unsigned long)&r,sizeof(r));
}
#endif
