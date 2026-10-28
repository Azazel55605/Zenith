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
#endif
