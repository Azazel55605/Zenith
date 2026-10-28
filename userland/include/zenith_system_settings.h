#ifndef ZENITH_SYSTEM_SETTINGS_H
#define ZENITH_SYSTEM_SETTINGS_H
#include "zenith_graphics.h"
/* Explicit system-default scope: reads are public, saves are root-only. */
struct zenith_system_settings {
    unsigned int version,flags; unsigned long revision;
    unsigned int keyboard_count,zone_count,keyboard,zone;
    char current_zone[64],current_session[16]; unsigned char reserved[16];
    char keyboards[8][32],zones[48][64]; unsigned int session_count,session;char sessions[3][16];unsigned char tail[584];
};
struct zenith_system_settings_request {
    unsigned int version,field;unsigned long revision;
    unsigned int choice,reserved;unsigned long reserved2;
};
_Static_assert(sizeof(struct zenith_system_settings)==4096,"system preferences snapshot");
_Static_assert(sizeof(struct zenith_system_settings_request)==32,"system preferences request");
static inline long zenith_system_settings_read(struct zenith_system_settings *s){long r=zenith_call(625,(unsigned long)s,sizeof(*s),0);return r==-38?zenith_call(620,(unsigned long)s,sizeof(*s),0):r;}
static inline long zenith_system_settings_save(unsigned int field,unsigned int choice,unsigned long revision){
    struct zenith_system_settings_request r={2,field,revision,choice,0,0};long status=zenith_call(626,(unsigned long)&r,sizeof(r),0);if(status!=-38)return status;if(field>2)return -19;r.version=1;return zenith_call(621,(unsigned long)&r,sizeof(r),0);
}
struct zenith_settings_information { unsigned int version,kind,length,truncated; char text[4080]; };
_Static_assert(sizeof(struct zenith_settings_information)==4096,"settings information snapshot");
static inline long zenith_settings_information_read(unsigned int kind,struct zenith_settings_information *s){return zenith_call(624,kind,(unsigned long)s,sizeof(*s));}
#endif
