#ifndef ZENITH_SYSTEM_SETTINGS_H
#define ZENITH_SYSTEM_SETTINGS_H
#include "zenith_graphics.h"
/* Explicit system-default scope: reads are public, saves are root-only. */
struct zenith_system_settings {
    unsigned int version,flags; unsigned long revision;
    unsigned int keyboard_count,zone_count,keyboard,zone;
    char current_zone[64]; unsigned char reserved[32];
    char keyboards[8][32],zones[48][64]; unsigned char tail[640];
};
struct zenith_system_settings_request {
    unsigned int version,field;unsigned long revision;
    unsigned int choice,reserved;unsigned long reserved2;
};
_Static_assert(sizeof(struct zenith_system_settings)==4096,"system preferences snapshot");
_Static_assert(sizeof(struct zenith_system_settings_request)==32,"system preferences request");
static inline long zenith_system_settings_read(struct zenith_system_settings *s){return zenith_call(620,(unsigned long)s,sizeof(*s),0);}
static inline long zenith_system_settings_save(unsigned int field,unsigned int choice,unsigned long revision){
    struct zenith_system_settings_request r={1,field,revision,choice,0,0};return zenith_call(621,(unsigned long)&r,sizeof(r),0);
}
#endif
