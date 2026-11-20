#ifndef ZENITH_NOTIFY_H
#define ZENITH_NOTIFY_H
/* zenith-native-notifications-abi 1.0 (kernel 0.0.72): call 670. Post a notification the desktop shows as a toast; the
 * sender shown is the program's name as the kernel knows it. Five posts per program per ten seconds (-16 beyond).
 * Settings: Do Not Disturb and the popup time (0 short, 1 normal, 2 long); saving needs root or the system settings
 * grant. Older kernels answer -38. */
#include "zenith_graphics.h"

struct zenith_notify_request { unsigned int version,op,argument,reserved; unsigned long buffer; unsigned int size,reserved2; };
_Static_assert(sizeof(struct zenith_notify_request)==32,"notify request");
struct zenith_notification { char title[64],body[160],icon[32]; };   /* NUL terminated; icon: a system icon name or empty */
_Static_assert(sizeof(struct zenith_notification)==256,"notification");
struct zenith_notify_settings { unsigned int version,dnd,popup,choices; unsigned long revision; unsigned int reserved[2]; };
_Static_assert(sizeof(struct zenith_notify_settings)==32,"notify settings");
enum { Z_NOTIFY_POST=1,Z_NOTIFY_SETTINGS=2,Z_NOTIFY_SAVE=3 };

static inline void zenith_notify_copy(char *to,unsigned long capacity,const char *from){
    unsigned long n=0;if(from)while(from[n]&&n+1<capacity){to[n]=from[n];n++;}
    while(n<capacity)to[n++]=0;
}
/* Returns the notification's id, -22 for an empty title or text that does not fit, -16 when posting too often. */
static inline long zenith_notify(const char *title,const char *body,const char *icon){
    struct zenith_notification n;
    zenith_notify_copy(n.title,sizeof(n.title),title);zenith_notify_copy(n.body,sizeof(n.body),body);zenith_notify_copy(n.icon,sizeof(n.icon),icon);
    struct zenith_notify_request r={1,Z_NOTIFY_POST,0,0,(unsigned long)&n,sizeof(n),0};
    return zenith_call(670,(unsigned long)&r,sizeof(r),0);
}
static inline long zenith_notify_settings(struct zenith_notify_settings *out){
    struct zenith_notify_request r={1,Z_NOTIFY_SETTINGS,0,0,(unsigned long)out,sizeof(*out),0};
    return zenith_call(670,(unsigned long)&r,sizeof(r),0);
}
/* Saves against the revision the caller read (-11 when changed elsewhere, -13 without permission). */
static inline long zenith_notify_save(unsigned int dnd,unsigned int popup,unsigned long revision){
    unsigned long expected=revision;
    struct zenith_notify_request r={1,Z_NOTIFY_SAVE,(dnd?1u:0u)|popup<<8,0,(unsigned long)&expected,8,0};
    return zenith_call(670,(unsigned long)&r,sizeof(r),0);
}
#endif
