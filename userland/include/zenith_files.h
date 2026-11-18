#ifndef ZENITH_FILES_H
#define ZENITH_FILES_H
#include "../sunbeam/zenith.h"
/* zenith-native-files-abi 1.0 (kernel 0.0.58): listing with modification times, free space, and copy/move/delete
 * jobs that run in the kernel as the caller. Older kernels answer -38. Contract: docs/components/HORIZON.md. */
#define Z_FILES_PAGE 32768
#define Z_FILES_COUNT_ITEMS 1u
struct zenith_files_list { unsigned int version,flags,offset,path_bytes; char path[1024]; };
/* now: UTC seconds; zone: the local time zone's offset (seconds) now, and per entry at its mtime. */
struct zenith_files_page { unsigned int version,count,total,next; long mtime; unsigned int mode,flags; long now; int zone; unsigned int reserved; };
struct zenith_file_entry { unsigned short record_bytes,name_bytes; unsigned int mode,uid,gid; unsigned long size; long mtime;
                           unsigned int links; int items; unsigned int target_mode; int zone; char name[]; };
struct zenith_filesystem { unsigned int version,flags; unsigned long total,available,block; char mount[64],type[16],reserved[16]; };
enum { Z_FS_READ_ONLY=1,Z_FS_UNIX=2 };
_Static_assert(sizeof(struct zenith_files_list)==1040,"list request");
_Static_assert(sizeof(struct zenith_files_page)==48,"page header");
_Static_assert(sizeof(struct zenith_file_entry)==48,"entry header");
_Static_assert(sizeof(struct zenith_filesystem)==128,"filesystem");
/* Returns the entries in this page, or a negative error. page must hold Z_FILES_PAGE bytes. */
static inline long zenith_files_list(const struct zenith_files_list *r,void *page){return zenith_call(654,(unsigned long)r,sizeof(*r),(unsigned long)page);}
static inline long zenith_files_filesystem(const char *path,unsigned long bytes,struct zenith_filesystem *out){return zenith_call(655,(unsigned long)path,bytes,(unsigned long)out);}

/* Kernel 0.0.65: 4 extracts .zip/.tar/.tar.gz/.tgz/.gz archives (sources) into new folders in the destination; 5 compresses the
 * sources into the destination archive (.zip, .tar or .tar.gz). */
enum { Z_JOB_COPY=1,Z_JOB_MOVE=2,Z_JOB_DELETE=3,Z_JOB_EXTRACT=4,Z_JOB_COMPRESS=5 };
enum { Z_JOB_REPLACE_ALL=1,Z_JOB_SKIP_ALL=2 };
enum { Z_JOB_COUNTING=1,Z_JOB_WORKING=2,Z_JOB_ASKING=3,Z_JOB_DONE=4,Z_JOB_FAILED=5,Z_JOB_CANCELLED=6 };
enum { Z_JOB_SKIP=1,Z_JOB_REPLACE=2,Z_JOB_KEEP_BOTH=3,Z_JOB_MERGE=4,Z_JOB_FOR_ALL=256 };
#define Z_JOB_FINAL(s) ((s)>=Z_JOB_DONE)
#define Z_JOB_REQUEST 8192
/* Header, then text_bytes of NUL-terminated paths: the destination ("" for delete), then each source. */
struct zenith_job_request { unsigned int version,operation,flags,count,text_bytes,reserved; char text[Z_JOB_REQUEST-24]; };
struct zenith_job_status { unsigned int version,state,operation,errors,items_done,items_total; unsigned long bytes_done,bytes_total;
                           unsigned int question_kind,reserved; char current[160],message[160],question[144]; };
_Static_assert(sizeof(struct zenith_job_status)==512,"job status");
_Static_assert(__builtin_offsetof(struct zenith_job_request,text)==24,"job request");
static inline long zenith_job_start(const struct zenith_job_request *r){return zenith_call(656,(unsigned long)r,24+r->text_bytes,0);}
static inline long zenith_job_status(long job,struct zenith_job_status *s){return zenith_call(657,(unsigned long)job,(unsigned long)s,sizeof(*s));}
static inline long zenith_job_cancel(long job){return zenith_call(658,(unsigned long)job,1,0);}
static inline long zenith_job_answer(long job,unsigned int answer){return zenith_call(658,(unsigned long)job,2,answer);}
static inline long zenith_job_forget(long job){return zenith_call(658,(unsigned long)job,3,0);}
/* Waits up to ms (1..1000) until the job ends or asks; returns its state. Kernels before 0.0.59 answer -22. */
static inline long zenith_job_wait(long job,unsigned int ms){return zenith_call(658,(unsigned long)job,4,ms);}
/* Input extension: 627 with bits 1 (motion), 2 (right-button presses as event 12, code 2), 4 (F1..F12 as extras 14..25)
 * and (kernel 0.0.67) 8 (middle-button presses as event 12, code 3). */
#define Z_GUI_SECONDARY 12
enum { Z_KEY_F1=14,Z_KEY_F2=15,Z_KEY_F5=18,Z_KEY_F9=22,Z_KEY_F11=24 };
static inline long zenith_gui_input(long handle,unsigned int bits){return zenith_call(627,(unsigned long)handle,bits,0);}
/* Thumbnails (kernel 0.0.65): PNG, BMP and baseline JPEG shrunk to at most max (16..256) pixels a side, ARGB with straight
 * alpha. Returns width << 16 | height, or -2 missing, -13 denied, -27 over 32 MiB, -95 not an image it can read. */
struct zenith_thumbnail_request { unsigned int version,max; unsigned long path; unsigned int path_bytes,capacity; unsigned long pixels; };
_Static_assert(sizeof(struct zenith_thumbnail_request)==32,"thumbnail request");
static inline long zenith_thumbnail(const char *path,unsigned long bytes,unsigned int max,unsigned int *pixels,unsigned int capacity){
    struct zenith_thumbnail_request r={1,max,(unsigned long)path,(unsigned int)bytes,capacity,(unsigned long)pixels};
    return zenith_call(660,(unsigned long)&r,sizeof(r),0);
}
/* Path calls (zenith-native-path-abi 1.0). */
static inline long zenith_mkdir(const char *path,unsigned long bytes){return zenith_path(1,path,bytes,0,0,0755,0);}
static inline long zenith_rename(const char *from,unsigned long fb,const char *to,unsigned long tb){return zenith_path(4,from,fb,to,tb,0,0);}
#endif
