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
#endif
