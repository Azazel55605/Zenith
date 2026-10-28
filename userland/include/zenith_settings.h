#ifndef ZENITH_SETTINGS_H
#define ZENITH_SETTINGS_H
#include "zenith_graphics.h"
/* Settings ABI 1, opaque RGB palette roles match client UI v1. No paths or commands. */
struct zenith_appearance {
    unsigned int version,flags;
    unsigned long revision;
    unsigned int mode,accent,active_mode,active_accent;
    unsigned long active_revision,resources;
    unsigned int palette[2][6][8];
    unsigned long reserved[2];
};
struct zenith_appearance_request {
    unsigned int version,flags;
    unsigned long expected_revision;
    unsigned int mode,accent;
    unsigned long reserved;
};
_Static_assert(sizeof(struct zenith_appearance)==448,"appearance snapshot layout");
_Static_assert(sizeof(struct zenith_appearance_request)==32,"appearance request layout");
enum { Z_SETTINGS_PERSISTENT=1,Z_SETTINGS_WRITABLE=2,Z_SETTINGS_PENDING=4,Z_SETTINGS_PACKAGED=8 };
static inline long zenith_appearance_read(struct zenith_appearance *state){return zenith_call(610,(unsigned long)state,sizeof(*state),0);}
static inline long zenith_appearance_save(unsigned int mode,unsigned int accent,unsigned long revision){
    struct zenith_appearance_request r={1,0,revision,mode,accent,0};
    return zenith_call(611,(unsigned long)&r,sizeof(r),0);
}
/* Window-state ABI 1. Dirty chrome close emits intent event 9; explicit close/revocation stays terminal. */
#define Z_GUI_CLOSE_INTENT 9
static inline long zenith_gui_dirty(long handle,int dirty){return zenith_call(604,(unsigned long)handle,!!dirty,0);}
struct zenith_material_settings { unsigned int version,flags; unsigned long revision; unsigned int saved,effective,available,reason; unsigned long active_revision,reserved; };
struct zenith_material_settings_request { unsigned int version,flags; unsigned long revision; unsigned int mode,accent,materials,reserved; };
_Static_assert(sizeof(struct zenith_material_settings)==48,"material settings snapshot");
_Static_assert(sizeof(struct zenith_material_settings_request)==32,"material settings request");
static inline long zenith_material_settings_read(struct zenith_material_settings *state){
    long r=zenith_call(616,(unsigned long)state,sizeof(*state),0);
    return r==-38?zenith_call(608,(unsigned long)state,sizeof(*state),0):r;
}
static inline long zenith_material_settings_save(unsigned int mode,unsigned int accent,unsigned int materials,unsigned long revision){
    struct zenith_material_settings_request r={2,0,revision,mode,accent,materials,0};
    long result=zenith_call(617,(unsigned long)&r,sizeof(r),0);
    if(result!=-38)return result;
    if(materials>3)return -19;
    r.version=1;return zenith_call(609,(unsigned long)&r,sizeof(r),0);
}
/* Material-control ABI 3 adds a host-clamped translucency preference. */
struct zenith_material_control { unsigned int version,flags; unsigned long revision; unsigned int saved,effective,available,reason; unsigned long active_revision; unsigned int translucency,active_translucency; };
struct zenith_material_control_request { unsigned int version,flags; unsigned long revision; unsigned int mode,accent,materials,translucency; unsigned long reserved; };
_Static_assert(sizeof(struct zenith_material_control)==48,"material control snapshot");
_Static_assert(sizeof(struct zenith_material_control_request)==40,"material control request");
static inline long zenith_material_control_read(struct zenith_material_control *state){return zenith_call(618,(unsigned long)state,sizeof(*state),0);}
static inline long zenith_material_control_save(unsigned int mode,unsigned int accent,unsigned int materials,unsigned int level,unsigned long revision){
    struct zenith_material_control_request r={3,0,revision,mode,accent,materials,level,0};return zenith_call(619,(unsigned long)&r,sizeof(r),0);
}
struct zenith_scoped_appearance { struct zenith_appearance appearance; struct zenith_material_control materials; unsigned int version,scope,uid,inherited; };
struct zenith_scoped_request { unsigned int version,scope; unsigned long revision; unsigned int mode,accent,materials,level,inherit,reserved; unsigned long reserved2; };
_Static_assert(sizeof(struct zenith_scoped_appearance)==512,"scoped appearance snapshot");
_Static_assert(sizeof(struct zenith_scoped_request)==48,"scoped appearance request");
static inline long zenith_scoped_appearance_read(unsigned int scope,struct zenith_scoped_appearance *s){return zenith_call(622,scope,(unsigned long)s,sizeof(*s));}
static inline long zenith_scoped_appearance_save(unsigned int scope,unsigned int mode,unsigned int accent,unsigned int materials,unsigned int level,unsigned long revision,int inherit){
    struct zenith_scoped_request r={1,scope,revision,mode,accent,materials,level,!!inherit,0,0};return zenith_call(623,(unsigned long)&r,sizeof(r),0);
}
#endif
