#ifndef ZENITH_MONITOR_H
#define ZENITH_MONITOR_H
#include "zenith_graphics.h"
/* zenith-native-monitor-abi 1.0: Observatory's view of the running system.
 * 642 fills a 4096-byte system snapshot: one sample per second at most, the last
 * 120 kept oldest first. 643 lists the caller's programs (root: everyone's).
 * 644 asks a program to stop, the same polite request as the shell's kill. */
#define Z_MONITOR_HISTORY 120
#define Z_MONITOR_CPUS 8
#define Z_MONITOR_OFFLINE 255
struct zenith_monitor_sample { unsigned char cpu[8],total,memory,reserved[6]; };
struct zenith_monitor {
    unsigned int version,cpus,detected,samples;
    unsigned long uptime_ms,memory_total,memory_free;
    unsigned int programs,frames_per_second,frame_average_us,frame_worst_us,interval_ms,sample_age_ms; /* age: 0 on kernels before 0.0.46 */
    struct zenith_monitor_sample latest;
    unsigned char reserved1[32];
    unsigned long memory_installed; /* RAM the firmware reports; 0 before kernel 0.0.47 */
    unsigned char reserved1b[8];
    char processor[64];
    unsigned char reserved2[64];
    struct zenith_monitor_sample history[Z_MONITOR_HISTORY];
    unsigned char reserved3[4096-256-Z_MONITOR_HISTORY*16];
};
_Static_assert(sizeof(struct zenith_monitor)==4096,"monitor snapshot ABI");
_Static_assert(__builtin_offsetof(struct zenith_monitor,latest)==64,"monitor latest ABI");
_Static_assert(__builtin_offsetof(struct zenith_monitor,processor)==128,"monitor processor ABI");
_Static_assert(__builtin_offsetof(struct zenith_monitor,memory_installed)==112,"monitor installed ABI");
_Static_assert(__builtin_offsetof(struct zenith_monitor,history)==256,"monitor history ABI");
enum { Z_PROCESS_STARTING=0,Z_PROCESS_RUNNING=1,Z_PROCESS_WAITING=2,Z_PROCESS_EXITED=3 };
enum { Z_PROCESS_STOP_REQUESTED=1,Z_PROCESS_HAS_EXITED=2 };
struct zenith_process_entry { int pid; unsigned int uid; unsigned char state,flags; signed char cpu; unsigned char affinity; int status; char name[32],user[16]; };
struct zenith_process_list { unsigned int version,count,total,reserved; struct zenith_process_entry entries[63]; };
_Static_assert(sizeof(struct zenith_process_entry)==64,"process entry ABI");
_Static_assert(sizeof(struct zenith_process_list)==4048,"process list ABI");
/* Call 649 (kernel 0.0.56; older kernels refuse it): disks and network interfaces with bytes since
 * boot and the latest per-second rates, 120 samples of combined rates oldest first, and the
 * windows on the desktop with the program that owns each (pid 0: the desktop's own windows; others
 * only for programs the caller may see). Disk bytes are counted in Zenith's ext2 and raw /dev paths;
 * network bytes are whole Ethernet frames, all on the primary interface. */
#define Z_ACTIVITY_DISKS 8
#define Z_ACTIVITY_NETWORKS 4
#define Z_ACTIVITY_WINDOWS 24
enum { Z_NET_LINK_UP=1, Z_NET_READY=2, Z_NET_PRIMARY=4 };
enum { Z_WINDOW_FOCUSED=1, Z_WINDOW_MINIMIZED=2 };
struct zenith_activity_disk { char name[16]; unsigned long read,written; unsigned int read_rate,write_rate; unsigned char reserved[8]; };
struct zenith_activity_network { char name[16]; unsigned long received,sent; unsigned int receive_rate,send_rate,flags; unsigned char address[4],mac[6],reserved[2]; };
struct zenith_activity_sample { unsigned int read,written,received,sent; };
struct zenith_activity_window { int pid; unsigned int flags; char title[56]; };
struct zenith_activity {
    unsigned int version,disk_count,network_count,samples,window_count,interval_ms,sample_age_ms,reserved;
    struct zenith_activity_disk disks[Z_ACTIVITY_DISKS];
    struct zenith_activity_network networks[Z_ACTIVITY_NETWORKS];
    struct zenith_activity_sample history[Z_MONITOR_HISTORY];
    struct zenith_activity_window windows[Z_ACTIVITY_WINDOWS];
};
_Static_assert(sizeof(struct zenith_activity_disk)==48,"activity disk ABI");
_Static_assert(sizeof(struct zenith_activity_network)==56,"activity network ABI");
_Static_assert(__builtin_offsetof(struct zenith_activity,networks)==416,"activity networks ABI");
_Static_assert(__builtin_offsetof(struct zenith_activity,history)==640,"activity history ABI");
_Static_assert(__builtin_offsetof(struct zenith_activity,windows)==2560,"activity windows ABI");
_Static_assert(sizeof(struct zenith_activity)==4096,"activity snapshot ABI");
static inline long zenith_activity_read(struct zenith_activity *a){return zenith_call(649,(unsigned long)a,sizeof(*a),0);}
static inline long zenith_monitor_read(struct zenith_monitor *m){return zenith_call(642,(unsigned long)m,sizeof(*m),0);}
/* The kernel writes a full 4096-byte page: give it one. */
static inline long zenith_process_list_read(void *page4096){return zenith_call(643,(unsigned long)page4096,4096,0);}
static inline long zenith_process_stop(int pid){return zenith_call(644,(unsigned long)pid,0,0);}
#endif
