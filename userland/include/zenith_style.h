#ifndef ZENITH_STYLE_H
#define ZENITH_STYLE_H
/* zenith-native-style-catalog-abi 1.1 (kernel 0.0.72; 1.0 in 0.0.71 had ops 1-2): call 669 for Meridian's pickers (docs/components/APPEARANCE.md).
 * List: every choice of a setting (field numbers of the system settings save call, 626) with its label, kind and color.
 * Preview: a picture of one choice drawn by the desktop itself (wallpapers). Older kernels answer -38; a field without a
 * catalogue answers -19. */
#include "zenith_graphics.h"

struct zenith_style_request { unsigned int version,op,field,index; unsigned long output; unsigned int capacity,size; };
_Static_assert(sizeof(struct zenith_style_request)==32,"style request");
struct zenith_style_entry { char name[24],label[32]; unsigned int kind,color; };   /* wallpapers: kind 0 dynamic, 1 solid (color RGB) */
struct zenith_style_list { unsigned int version,field,count,current; unsigned long revision; unsigned int reserved[2]; struct zenith_style_entry entries[63]; };
_Static_assert(sizeof(struct zenith_style_list)==4064,"style list");
enum { Z_STYLE_LIST=1,Z_STYLE_PREVIEW=2,Z_STYLE_SOUNDS=3,Z_STYLE_SOUNDS_SAVE=4,Z_STYLE_SOUND_PLAY=5 };
/* System sounds: level 0..100, a bit per event (1 on), the events' labels and the settings revision. */
struct zenith_sound_settings { unsigned int version,level,mask,count; unsigned long revision; unsigned int reserved[2]; char labels[15][32]; };
_Static_assert(sizeof(struct zenith_sound_settings)==512,"sound settings");
static inline long zenith_sound_settings(struct zenith_sound_settings *out){
    struct zenith_style_request r={1,Z_STYLE_SOUNDS,0,0,(unsigned long)out,sizeof(*out),0};return zenith_call(669,(unsigned long)&r,sizeof(r),0);
}
/* Saves level and mask against the revision the caller read (-11 when changed elsewhere, -13 without permission). */
static inline long zenith_sound_save(unsigned int level,unsigned int mask,unsigned long revision){
    unsigned long expected=revision;
    struct zenith_style_request r={1,Z_STYLE_SOUNDS_SAVE,0,level,(unsigned long)&expected,8,mask};return zenith_call(669,(unsigned long)&r,sizeof(r),0);
}
static inline long zenith_sound_play(unsigned int event){
    struct zenith_style_request r={1,Z_STYLE_SOUND_PLAY,0,event,0,0,0};return zenith_call(669,(unsigned long)&r,sizeof(r),0);
}
enum { Z_STYLE_WALLPAPER=6 };

static inline long zenith_style_list(unsigned int field,struct zenith_style_list *out){
    static unsigned char page[4096];
    struct zenith_style_request r={1,Z_STYLE_LIST,field,0,(unsigned long)page,sizeof(page),0};
    long n=zenith_call(669,(unsigned long)&r,sizeof(r),0);
    if(n<0)return n;
    unsigned char *o=(unsigned char*)out;for(unsigned long i=0;i<sizeof(*out);i++)o[i]=page[i];
    return 0;
}
/* ARGB pixels, width x height (32..320 x 20..240). */
static inline long zenith_style_preview(unsigned int field,unsigned int index,int width,int height,unsigned int *pixels){
    struct zenith_style_request r={1,Z_STYLE_PREVIEW,field,index,(unsigned long)pixels,(unsigned int)(width*height*4),(unsigned int)(width<<16|height)};
    return zenith_call(669,(unsigned long)&r,sizeof(r),0);
}
#endif
