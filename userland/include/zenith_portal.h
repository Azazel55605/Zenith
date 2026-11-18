#ifndef ZENITH_PORTAL_H
#define ZENITH_PORTAL_H
#include "zenith_graphics.h"
/* zenith-native-portal-abi 1.0 (kernel 0.0.57): the Desktop Portal. A program asks for something
 * only the person may decide; the desktop shows its own dialog (programs cannot draw over it or
 * type into it) and answers. Requests are asynchronous: open returns a handle, poll it from the
 * event loop until the state is final, then close it.
 *   650 open  (request, 256)        -> handle > 0, or -errno (-38 no such interface or method,
 *                                      -2 no such action, -24 four requests already open, -16 busy)
 *   651 poll  (handle, response, 256) -> state; -9 for a handle that is not the caller's
 *   652 close (handle)              -> withdraws an open request (the dialog closes) and forgets it
 *   653 info  (buffer, 1024)        -> the interfaces this kernel offers and whether a desktop answers
 * Interfaces are named and versioned; new ones are added without changing these calls. */
enum {
    Z_PORTAL_PENDING=1,     /* waiting for the desktop to show it */
    Z_PORTAL_SHOWN=2,       /* a dialog is on screen */
    Z_PORTAL_APPROVED=3,
    Z_PORTAL_DENIED=4,      /* refused, dismissed, or three wrong passwords */
    Z_PORTAL_CANCELLED=5,   /* withdrawn, the program ended or the session ended */
    Z_PORTAL_FAILED=6,
    Z_PORTAL_UNAVAILABLE=7  /* no graphical session can answer (use sudo in a terminal) */
};
#define Z_PORTAL_FINAL(state) ((state)>=Z_PORTAL_APPROVED)
struct zenith_portal_request {
    unsigned int version;          /* 1 */
    unsigned int flags;            /* 0 */
    char interface_name[64];       /* e.g. Z_PORTAL_AUTHORIZATION, NUL-padded */
    char method[32];
    unsigned int payload_bytes;    /* <= 144; bytes past it must be zero */
    unsigned int reserved;
    unsigned char payload[144];
};
struct zenith_portal_response {
    unsigned int version, state;
    long handle;
    unsigned int response_bytes, reserved;
    char message[96];              /* why it ended as it did, for the program to show */
    unsigned char response[136];
};
struct zenith_portal_interface { char name[64]; unsigned int version, flags; /* flags 1: the desktop answers it */ };
struct zenith_portal_info {
    unsigned int version, count, flags /* 1: a graphical session is attached */, reserved;
    struct zenith_portal_interface interfaces[14];
};
_Static_assert(sizeof(struct zenith_portal_request)==256,"portal request ABI");
_Static_assert(__builtin_offsetof(struct zenith_portal_request,payload)==112,"portal payload ABI");
_Static_assert(sizeof(struct zenith_portal_response)==256,"portal response ABI");
_Static_assert(__builtin_offsetof(struct zenith_portal_response,response)==120,"portal response bytes ABI");
_Static_assert(sizeof(struct zenith_portal_info)==1024,"portal info ABI");

static inline long zenith_portal_open(const struct zenith_portal_request *r){return zenith_call(650,(unsigned long)r,sizeof(*r),0);}
static inline long zenith_portal_poll(long handle,struct zenith_portal_response *r){return zenith_call(651,(unsigned long)handle,(unsigned long)r,sizeof(*r));}
static inline long zenith_portal_close(long handle){return zenith_call(652,(unsigned long)handle,0,0);}
static inline long zenith_portal_info(struct zenith_portal_info *i){return zenith_call(653,(unsigned long)i,sizeof(*i),0);}

/* org.zenith.portal.Authorization 1: administrative actions (polkit-like). Payload: the action id,
 * NUL-padded to 64 bytes. Methods: "Authorize" asks (a dialog unless already granted or running as
 * root), "Check" answers at once, "Release" gives it back. Response: u32 granted, u32 privileges
 * held. A grant lasts until the program exits: the kernel then serves that area's calls as root
 * for this program alone (disks: 647/648; system settings: 620/621, 625/626, 640/641 and the
 * system appearance). */
#define Z_PORTAL_AUTHORIZATION "org.zenith.portal.Authorization"
#define Z_ACTION_DISKS "org.zenith.disks.manage"
#define Z_ACTION_SYSTEM_SETTINGS "org.zenith.settings.system"
/* Kernel 0.0.65: files anywhere (654..658, 405, 4, 408 as root for the program); reads and writes stay its own. */
#define Z_ACTION_AUDIO_RECORD "org.zenith.audio.record"
#define Z_ACTION_FILES "org.zenith.files.manage"
enum { Z_PRIVILEGE_DISKS=1, Z_PRIVILEGE_SYSTEM_SETTINGS=2, Z_PRIVILEGE_FILES=4, Z_PRIVILEGE_AUDIO_RECORD=8 };
struct zenith_authorization_result { unsigned int granted, privileges; };

/* Extended payload (kernel 0.0.65): request flag 1 makes the 16 payload bytes { u64 address, u32 bytes, u32 0 }
 * naming up to 4096 bytes of the program's memory, for requests that carry a path. */
#define Z_PORTAL_EXTENDED 1u
struct zenith_portal_extended { unsigned long address; unsigned int bytes, zero; };

/* org.zenith.portal.OpenWith 1 (kernel 0.0.65): the desktop opens a file with an app. "Open" uses the person's
 * remembered app, else the app that declares the file's type, and asks only when there is none; "Choose" always
 * asks (Open with...). Payload: the absolute path (extended). Response: u32 opened, then the app name (64 bytes).
 * Denied when the person cancels or no app opens files; Failed when the app could not be started. */
#define Z_PORTAL_OPEN_WITH "org.zenith.portal.OpenWith"
struct zenith_open_with_result { unsigned int opened; char app[64]; };
static inline long zenith_open_with(const char *method,const char *path,unsigned long bytes){
    struct zenith_portal_request r;unsigned char *p=(unsigned char*)&r;
    for(unsigned long i=0;i<sizeof(r);i++)p[i]=0;
    r.version=1;r.flags=Z_PORTAL_EXTENDED;r.payload_bytes=sizeof(struct zenith_portal_extended);
    const char *n=Z_PORTAL_OPEN_WITH;for(int i=0;n[i]&&i<63;i++)r.interface_name[i]=n[i];
    for(int i=0;method[i]&&i<31;i++)r.method[i]=method[i];
    struct zenith_portal_extended *x=(struct zenith_portal_extended*)r.payload;x->address=(unsigned long)path;x->bytes=(unsigned int)bytes;x->zero=0;
    return zenith_portal_open(&r);
}

static inline long zenith_authorize(const char *method,const char *action){
    struct zenith_portal_request r;unsigned char *p=(unsigned char*)&r;
    for(unsigned long i=0;i<sizeof(r);i++)p[i]=0;
    r.version=1;r.payload_bytes=64;
    const char *n=Z_PORTAL_AUTHORIZATION;for(int i=0;n[i]&&i<63;i++)r.interface_name[i]=n[i];
    for(int i=0;method[i]&&i<31;i++)r.method[i]=method[i];
    for(int i=0;action[i]&&i<63;i++)r.payload[i]=(unsigned char)action[i];
    return zenith_portal_open(&r);
}
#endif
