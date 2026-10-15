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
/* Filesystem extension 1: explicit UTF-8 path length; read-only regular or directory. */
static inline long zenith_call(long number, unsigned long a, unsigned long b, unsigned long c) {
    __asm__ volatile("int $0xfe" : "+a"(number) : "D"(a), "S"(b), "d"(c) : "memory", "cc"); return number;
}
struct zenith_stat { unsigned long size; unsigned int mode, uid, gid, reserved; };
struct zenith_dirent { struct zenith_stat stat; char name[256]; };
#endif
