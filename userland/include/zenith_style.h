#ifndef ZENITH_STYLE_H
#define ZENITH_STYLE_H
/* zenith-native-style-catalog-abi 1.0 (kernel 0.0.71): call 669 for Meridian's pickers (docs/components/APPEARANCE.md).
 * List: every choice of a setting (field numbers of the system settings save call, 626) with its label, kind and color.
 * Preview: a picture of one choice drawn by the desktop itself (wallpapers). Older kernels answer -38; a field without a
 * catalogue answers -19. */
#include "zenith_graphics.h"

struct zenith_style_request { unsigned int version,op,field,index; unsigned long output; unsigned int capacity,size; };
_Static_assert(sizeof(struct zenith_style_request)==32,"style request");
struct zenith_style_entry { char name[24],label[32]; unsigned int kind,color; };   /* wallpapers: kind 0 dynamic, 1 solid (color RGB) */
struct zenith_style_list { unsigned int version,field,count,current; unsigned long revision; unsigned int reserved[2]; struct zenith_style_entry entries[63]; };
_Static_assert(sizeof(struct zenith_style_list)==4064,"style list");
enum { Z_STYLE_LIST=1,Z_STYLE_PREVIEW=2 };
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
