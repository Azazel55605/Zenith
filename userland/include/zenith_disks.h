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
 * is the edit's token: poll 647 until job_token matches and job_state is done or failed. */
#define Z_DISKS 8
#define Z_DISK_PARTS 16
#define Z_DISK_FREE 8
enum { Z_DISK_GPT=1, Z_DISK_SYSTEM=2, Z_DISK_MOUNTED=4, Z_DISK_EDITABLE=8 };
enum { Z_PART_OTHER=0, Z_PART_EFI=1, Z_PART_LINUX=2 };
enum { Z_FS_UNKNOWN=0, Z_FS_EXT2=1, Z_FS_FAT32=2 };
enum { Z_ROLE_NONE=0, Z_ROLE_ROOT=1, Z_ROLE_BOOT=2 };
enum { Z_DISKS_CAN_EDIT=1 };
enum { Z_DISK_JOB_IDLE=0, Z_DISK_JOB_RUNNING=1, Z_DISK_JOB_DONE=2, Z_DISK_JOB_FAILED=3 };
enum { Z_DISK_INIT=1, Z_DISK_CREATE=2, Z_DISK_DELETE=3, Z_DISK_GROW=4 };
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
    unsigned char tail[1528];
};
struct zenith_disk_request {
    unsigned int version,action;
    unsigned long revision;
    char disk[16];
    unsigned long start,sectors;
    unsigned int filesystem,reserved;   /* filesystem: Z_FS_EXT2 or Z_FS_UNKNOWN (none) */
    char label[16];
};
_Static_assert(sizeof(struct zenith_disk_part)==104,"disk part ABI");
_Static_assert(sizeof(struct zenith_disk)==1832,"disk ABI");
_Static_assert(__builtin_offsetof(struct zenith_disks,disks)==200,"disk snapshot header ABI");
_Static_assert(sizeof(struct zenith_disks)==16384,"disk snapshot ABI");
_Static_assert(sizeof(struct zenith_disk_request)==72,"disk request ABI");
static inline long zenith_disks_read(struct zenith_disks *s){return zenith_call(647,(unsigned long)s,sizeof(*s),0);}
static inline long zenith_disks_request(const struct zenith_disk_request *r){return zenith_call(648,(unsigned long)r,sizeof(*r),0);}
#endif
