#ifndef ZENITH_DISPLAY_H
#define ZENITH_DISPLAY_H
#include "zenith_graphics.h"
/* Primary output, host-owned 15 second preview; no framebuffer or input access.
 * flags: 1 persistent, 2 writable, 4 own preview, 8 percent is valid and requests may name
 * 125/150/175 (scale = percent; 1 and 2 remain 100% and 200%). scale stays 1 or 2 for older
 * clients (1 under a fractional percent). */
struct zenith_display {
    unsigned int version,flags; unsigned long revision,token;
    unsigned int width,height,scale,logical_width,logical_height,phase,seconds;
    int error; unsigned int mode_count,reserved;
    char driver[64]; struct {unsigned int width,height;} modes[24];
    unsigned int percent; unsigned char tail[188];
};
struct zenith_display_request {
    unsigned int version,action; unsigned long revision,token;
    unsigned int mode,scale; unsigned long reserved;
};
_Static_assert(sizeof(struct zenith_display)==512,"display snapshot");
_Static_assert(sizeof(struct zenith_display_request)==40,"display request");
static inline long zenith_display_read(struct zenith_display *s){return zenith_call(640,(unsigned long)s,sizeof(*s),0);}
static inline long zenith_display_change(unsigned int action,unsigned int mode,unsigned int scale,unsigned long revision,unsigned long token){
    struct zenith_display_request r={1,action,revision,token,mode,scale,0};return zenith_call(641,(unsigned long)&r,sizeof(r),0);
}
#endif
