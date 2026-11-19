#ifndef ZENITH_PACKAGES_H
#define ZENITH_PACKAGES_H
/* zenith-native-package-center-abi 1.0 (kernel 0.0.70): call 668 (request, 64, 0) for Orbit, the graphical front end
 * to pkg (docs/components/ORBIT.md). Same signatures, planner, transactions and kernel staging as the shell; no second
 * database. Older kernels answer -38.
 * Reads (overview, list, details, plan, job, history, sources) are open to every user and answer line-based text:
 * a header line ("ZENITH-ORBIT-LIST\t1"), then records of tab-separated fields ("-" for none). Apply needs root or the
 * Desktop Portal grant org.zenith.packages.manage (-13 without). Results: bytes written, a job id, 0; -16 busy,
 * -22 malformed, -34 buffer too small, -5 failed (a plain-words message is written to the output buffer). */
#include "zenith_graphics.h"

struct zenith_packages_request { unsigned int version,op; unsigned long output; unsigned int capacity,value; unsigned long input; unsigned int input_bytes,reserved[7]; };
_Static_assert(sizeof(struct zenith_packages_request)==64,"packages request");
enum {
    Z_PKG_OVERVIEW=1,   /* kernel, counts, key, source and system records */
    Z_PKG_LIST=2,       /* "pkg" records: name kind repository state installed candidate latest bytes payload files programs title summary category icon reason */
    Z_PKG_DETAILS=3,    /* input: the name; value 1 downloads a package that is not installed to read its own description */
    Z_PKG_PLAN=4,       /* input: lines "install NAME", "upgrade NAME", "remove NAME", "kernel" */
    Z_PKG_APPLY=5,      /* the same input; starts a job and answers its id */
    Z_PKG_JOB=6,        /* "job" id kind state(1 running, 2 done, 3 failed, 4 cancelled) mine done total restart changes; "step"; "message" */
    Z_PKG_CANCEL=7,     /* before the changes are being written */
    Z_PKG_HISTORY=8,    /* "transaction" time uid user source ok command message, each followed by its "change" lines */
    Z_PKG_SOURCES=9,    /* "key", "source" name priority mirror generation issued expires entries checked, "cache" */
    Z_PKG_REFRESH=10,   /* downloads the signed package lists (a job; open to every user) */
    Z_PKG_WAIT=11       /* value: milliseconds up to 5000; 1 when no job runs any more */
};
/* List states. */
enum { Z_PKG_CURRENT=0,Z_PKG_UPDATE=1,Z_PKG_AVAILABLE=2,Z_PKG_NOT_OFFERED=3,Z_PKG_LOCAL=4 };

static inline long zenith_packages(unsigned int op,unsigned int value,void *output,unsigned int capacity,const void *input,unsigned int input_bytes){
    struct zenith_packages_request r={0};
    r.version=1;r.op=op;r.value=value;r.output=(unsigned long)output;r.capacity=capacity;r.input=(unsigned long)input;r.input_bytes=input_bytes;
    return zenith_call(668,(unsigned long)&r,sizeof(r),0);
}
#define Z_ACTION_PACKAGES "org.zenith.packages.manage"
#endif
