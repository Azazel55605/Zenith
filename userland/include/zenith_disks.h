#ifndef ZENITH_DISKS_H
#define ZENITH_DISKS_H
#include "zenith_graphics.h"
/* zenith-native-disk-abi 1.0: Equinox's view of disks and its edits.
 * 647 fills a 16384-byte snapshot: up to 8 disks, 16 partitions and 8 free regions each, the
 * revision, and the background edit's state and message. 648 starts one edit (root only):
 * init (new empty GPT), create (in the first free region that fits; sectors 0 = the largest),
 * delete, grow (sectors 0 = as far as possible). Partitions are named by disk and start
 * sector, which deleting another partition does not change. A request against an older
 * revision is refused (-11); -16 while another edit runs; -13 without root. A positive result
 * is the edit's token: poll 647 until job_token matches and job_state is done or failed.
 * Extras (extra.version 1, kernel 0.0.54 and later; zero before): bytes in use and the
 * filesystem's state per partition (by disk and partition index, 0 when unknown) and whether
 * both GPT copies match. Same capability version: the tail was reserved and zero.
 * Extras version 2 (kernel 0.0.56): flags bit Z_DISKS_CAN_MOUNT says actions 5..7 are understood
 * (mount at /mnt/<label>, unmount, mount at startup; request flags bit 0 = at startup / forget /
 * on), startup[][] marks partitions /etc/fstab mounts at startup, fstab_skipped counts its lines
 * Zenith does not understand (kept as written, skipped at startup). Older kernels refuse 5..7. */
#define Z_DISKS 8
#define Z_DISK_PARTS 16
#define Z_DISK_FREE 8
enum { Z_DISK_GPT=1, Z_DISK_SYSTEM=2, Z_DISK_MOUNTED=4, Z_DISK_EDITABLE=8 };
enum { Z_PART_OTHER=0, Z_PART_EFI=1, Z_PART_LINUX=2 };
enum { Z_FS_UNKNOWN=0, Z_FS_EXT2=1, Z_FS_FAT32=2 };
enum { Z_ROLE_NONE=0, Z_ROLE_ROOT=1, Z_ROLE_BOOT=2 };
enum { Z_DISKS_CAN_EDIT=1, Z_DISKS_CAN_MOUNT=2 };
enum { Z_DISK_JOB_IDLE=0, Z_DISK_JOB_RUNNING=1, Z_DISK_JOB_DONE=2, Z_DISK_JOB_FAILED=3 };
enum { Z_DISK_INIT=1, Z_DISK_CREATE=2, Z_DISK_DELETE=3, Z_DISK_GROW=4, Z_DISK_MOUNT=5, Z_DISK_UNMOUNT=6, Z_DISK_AT_STARTUP=7 };
enum { Z_DISK_REQUEST_STARTUP=1 };
enum { Z_FS_STATE_UNKNOWN=0, Z_FS_STATE_CLEAN=1, Z_FS_STATE_CHECK=2, Z_FS_STATE_IN_USE=3 };
enum { Z_GPT_UNKNOWN=0, Z_GPT_COPIES_MATCH=1, Z_GPT_COPIES_DIFFER=2 };
struct zenith_disk_part {
    char name[16];
    unsigned long start,sectors,grow_limit;      /* 512-byte sectors; grow_limit: largest in-place length */
    unsigned int type,filesystem,block_size,role;
    char label[16],mount[32];
};
struct zenith_disk_region { unsigned long start,sectors; };
struct zenith_disk {
    char name[16];
    unsigned long sectors;
    unsigned int flags,part_count,free_count,reserved;
    struct zenith_disk_part parts[Z_DISK_PARTS];
    struct zenith_disk_region free[Z_DISK_FREE];
};
struct zenith_disks {
    unsigned int version,flags;
    unsigned long revision;
    unsigned int job_state,job_action;
    unsigned long job_token;
    char job_message[160];
    unsigned int disk_count,reserved;
    struct zenith_disk disks[Z_DISKS];
    struct zenith_disks_extra {
        unsigned int version,reserved;
        unsigned long used[Z_DISKS][Z_DISK_PARTS];        /* bytes in use */
        unsigned char state[Z_DISKS][Z_DISK_PARTS];       /* Z_FS_STATE_* */
        unsigned char gpt[Z_DISKS];                       /* Z_GPT_* */
        unsigned char startup[Z_DISKS][Z_DISK_PARTS];     /* version 2: 1 = /etc/fstab mounts it at startup */
        unsigned int fstab_skipped;                       /* version 2: fstab lines Zenith skips */
        unsigned char tail[228];
    } extra;
};
struct zenith_disk_request {
    unsigned int version,action;
    unsigned long revision;
    char disk[16];
    unsigned long start,sectors;
    unsigned int filesystem,flags;      /* filesystem: Z_FS_EXT2 or Z_FS_UNKNOWN (none); flags: actions 5..7 only */
    char label[16];                     /* create: the ext2 label; mount: the name under /mnt */
};
_Static_assert(sizeof(struct zenith_disk_part)==104,"disk part ABI");
_Static_assert(sizeof(struct zenith_disk)==1832,"disk ABI");
_Static_assert(__builtin_offsetof(struct zenith_disks,disks)==200,"disk snapshot header ABI");
_Static_assert(sizeof(struct zenith_disks)==16384,"disk snapshot ABI");
_Static_assert(__builtin_offsetof(struct zenith_disks,extra)==14856,"disk snapshot extras ABI");
_Static_assert(sizeof(struct zenith_disk_request)==72,"disk request ABI");
_Static_assert(__builtin_offsetof(struct zenith_disks,extra.startup)==16024,"disk snapshot startup ABI");
_Static_assert(__builtin_offsetof(struct zenith_disks,extra.fstab_skipped)==16152,"disk snapshot fstab ABI");
static inline long zenith_disks_read(struct zenith_disks *s){return zenith_call(647,(unsigned long)s,sizeof(*s),0);}
static inline long zenith_disks_request(const struct zenith_disk_request *r){return zenith_call(648,(unsigned long)r,sizeof(*r),0);}
#endif
