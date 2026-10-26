#ifndef ZENITH_NATIVE_H
#define ZENITH_NATIVE_H
/* Zenith native ABI 1. No Linux/glibc dependency. write(1), pid(39), exit(60), uid(102), gid(104). */
static inline long zenith_write(const void *data, unsigned long length) {
    long result = 1;
    __asm__ volatile("int $0xfe" : "+a"(result) : "D"(1L), "S"(data), "d"(length) : "memory", "cc");
    return result;
}
static inline __attribute__((noreturn)) void zenith_exit(int code) {
    __asm__ volatile("int $0xfe" : : "a"(60L), "D"((long)code) : "memory", "cc");
    __builtin_unreachable();
}
static inline long zenith_identity(long number) {
    __asm__ volatile("int $0xfe" : "+a"(number) : : "memory", "cc"); return number;
}
/* Stream extension 1: fd 0 canonical input, live byte writes on 1/2.
 * isatty returns 1/0 or -9 for a closed/invalid standard descriptor. */
static inline long zenith_isatty(unsigned long fd) {
    long number=400;
    __asm__ volatile("int $0xfe" : "+a"(number) : "D"(fd) : "memory", "cc"); return number;
}
/* FS extension 1 and IO extension 1; Zenith calls, not Linux open flags. */
#define Z_OPEN_READ 0UL
#define Z_OPEN_DIRECTORY 1UL
#define Z_OPEN_WRITE 2UL
#define Z_OPEN_CREATE 4UL
#define Z_OPEN_TRUNCATE 8UL
#define Z_OPEN_APPEND 16UL
static inline long zenith_call(long number, unsigned long a, unsigned long b, unsigned long c) {
    __asm__ volatile("int $0xfe" : "+a"(number) : "D"(a), "S"(b), "d"(c) : "memory", "cc"); return number;
}
struct zenith_stat { unsigned long size; unsigned int mode, uid, gid, reserved; };
struct zenith_dirent { struct zenith_stat stat; char name[256]; };

/* Path extension 1: the complete fixed request and both path buffers are checked
 * before any namespace mutation. Values are Zenith operations, not Linux flags. */
struct zenith_path_request {
    unsigned int operation, flags;
    unsigned long path, length, target, target_length, value, extra;
};
_Static_assert(sizeof(struct zenith_path_request)==56,"path request layout");
static inline long zenith_path(unsigned int operation,const char *path,unsigned long length,
    const char *target,unsigned long target_length,unsigned long value,unsigned long extra) {
    struct zenith_path_request request={operation,0,(unsigned long)path,length,(unsigned long)target,target_length,value,extra};
    return zenith_call(405,(unsigned long)&request,sizeof(request),0);
}

#endif
