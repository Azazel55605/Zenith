#ifndef ZENITH_DESKTOP_PREFERENCES_H
#define ZENITH_DESKTOP_PREFERENCES_H
#include "../sunbeam/zenith.h"
enum { Z_DESKTOP_TASKBAR=1, Z_DESKTOP_WINDOWS, Z_DESKTOP_SWITCHER, Z_DESKTOP_WORKSPACES, Z_DESKTOP_WINDOWS_TILING, Z_DESKTOP_LOCKING };
enum { Z_DESKTOP_MAX=8, Z_DESKTOP_PINS=6, Z_DESKTOP_APPS=62, Z_DESKTOP_GAP_MAX=32 };
struct zenith_desktop_entry { unsigned int id; char name[65]; unsigned char reserved[3]; };
struct zenith_desktop_preferences {
    unsigned int version,page,flags,count;
    unsigned long revision;
    unsigned int values[8];
    union {
        unsigned int reserved[2];
        struct { unsigned int inner_gap,outer_gap; }; /* Z_DESKTOP_WINDOWS_TILING only, logical pixels */
    };
    struct zenith_desktop_entry entries[Z_DESKTOP_MAX];
};
struct zenith_desktop_app { char id[65], label[65]; };
struct zenith_desktop_apps { unsigned int version,count; struct zenith_desktop_app entries[Z_DESKTOP_APPS]; };
_Static_assert(sizeof(struct zenith_desktop_preferences)==640,"desktop preferences wire size");
static inline long zenith_desktop_read(unsigned int page,struct zenith_desktop_preferences *out){
    return zenith_call(672,(unsigned long)out,1,page);
}
static inline long zenith_desktop_save(const struct zenith_desktop_preferences *in){
    return zenith_call(672,(unsigned long)in,2,sizeof(*in));
}
static inline long zenith_desktop_apps(struct zenith_desktop_apps *out){
    return zenith_call(672,(unsigned long)out,3,sizeof(*out));
}
/* Ops 4 and 5 use the caller's account. Read returns the byte count of up to six
 * newline-separated page IDs in a 384-byte buffer. Visit takes one ID of 1..63 bytes. */
#endif
