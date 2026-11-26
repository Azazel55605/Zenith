/* Platform operations reached by the browser's single-threaded musl build.
 * Keep Linux request numbers private to libc. Guest calls use the Zenith ABI. */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <stdint.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/uio.h>
#include <time.h>
#include <unistd.h>
#include "zenith_network.h"

enum { page_size = 4096, page_count = 6144 };
_Alignas(page_size) static unsigned char arena[page_size * page_count];
static unsigned char occupied[page_count];
/* Zenith allows sixteen open files. Its descriptor numbers increase with each open. */
static struct { int fd; struct stat info; } files[16];

static long anonymous_pages(size_t bytes)
{
    if (bytes == 0 || bytes > sizeof(arena)) return -ENOMEM;
    size_t count = (bytes + page_size - 1) / page_size;
    size_t run = 0;
    for (size_t i = 0; i < page_count; ++i) {
        run = occupied[i] ? 0 : run + 1;
        if (run == count) {
            size_t first = i + 1 - run;
            memset(occupied + first, 1, count);
            unsigned char *address = arena + first * page_size;
            memset(address, 0, count * page_size);
            return (long)address;
        }
    }
    return -ENOMEM;
}

static long release_pages(uintptr_t address, size_t length, int discard)
{
    uintptr_t offset = address - (uintptr_t)arena;
    if (offset >= sizeof(arena) || offset % page_size || !length || length > sizeof(arena) - offset)
        return -EINVAL;
    size_t first = offset / page_size, count = (length + page_size - 1) / page_size;
    if (memchr(occupied + first, 0, count)) return -EINVAL;
    if (discard) memset(arena + offset, 0, count * page_size);
    else memset(occupied + first, 0, count);
    return 0;
}

static long path_stat(const char *path, int follow, struct stat *out)
{
    struct zenith_stat info;
    long result = zenith_call(follow ? 4 : 408, (uintptr_t)path, strlen(path), (uintptr_t)&info);
    if (result < 0) return result;
    *out = (struct stat){.st_mode=info.mode, .st_nlink=1, .st_uid=info.uid, .st_gid=info.gid,
        .st_size=info.size, .st_blksize=page_size, .st_blocks=(info.size + 511)/512};
    return 0;
}

static long open_readonly(const char *path, int flags)
{
    if (flags & (O_ACCMODE | O_CREAT | O_TRUNC | O_APPEND)) return -EROFS;
    if (flags & ~(O_DIRECTORY | O_CLOEXEC | O_LARGEFILE | O_NONBLOCK | O_NOCTTY)) return -ENOTSUP;
    size_t i;
    for (i = 0; i < sizeof(files)/sizeof(files[0]); ++i)
        if (!files[i].fd) break;
    if (i == sizeof(files)/sizeof(files[0])) return -EMFILE;
    long result = path_stat(path, 1, &files[i].info);
    if (result < 0) return result;
    long fd = zenith_call(2, (uintptr_t)path, strlen(path), flags & O_DIRECTORY ? Z_OPEN_DIRECTORY : Z_OPEN_READ);
    if (fd >= 0) files[i].fd = fd;
    return fd;
}

static long transfer(long operation, long fd, void *data, size_t length)
{
    size_t done = 0;
    while (done < length) {
        size_t count = length - done;
        if (count > 4096) count = 4096;
        long n = zenith_call(operation, fd, (uintptr_t)data + done, count);
        if (n < 0) return done ? (long)done : n;
        done += n;
        if ((size_t)n < count) break;
    }
    return done;
}

static long vector_io(long op, long fd, struct iovec *iov, long count)
{
    if (count < 0 || count > 1024) return -EINVAL;
    long done = 0;
    for (long i = 0; i < count; ++i) {
        long n = transfer(op, fd, iov[i].iov_base, iov[i].iov_len);
        if (n < 0) return done ? done : n;
        done += n;
        if ((size_t)n < iov[i].iov_len) break;
    }
    return done;
}

static long clock_value(long clock, struct timespec *value)
{
    if (clock != CLOCK_REALTIME && clock != CLOCK_MONOTONIC) return -EINVAL;
    long ms = zenith_call(628, 0, 0, 0);
    if (ms < 0) return ms;
    if (clock == CLOCK_REALTIME) {
        static long epoch, sampled;
        if (!epoch || ms - sampled >= 60000) {
            struct zenith_network_status status;
            long n = zenith_network(Z_NET_STATUS, 0, &status, sizeof(status), 0, 0);
            if (n != sizeof(status) || status.utc_now <= 0) return -EIO;
            epoch = status.utc_now * 1000 - ms;
            sampled = ms;
        }
        ms += epoch;
    }
    value->tv_sec = ms / 1000;
    value->tv_nsec = ms % 1000 * 1000000;
    return 0;
}

long zns_syscall(long n, long a, long b, long c, long d, long e, long f)
{
    switch (n) {
    case SYS_mmap:
        if (a || c != (PROT_READ | PROT_WRITE) || d != (MAP_PRIVATE | MAP_ANONYMOUS) || e != -1 || f)
            return -ENOTSUP;
        return anonymous_pages(b);
    case SYS_munmap: return release_pages(a, b, 0);
    case SYS_madvise: return c == MADV_DONTNEED ? release_pages(a, b, 1) : -ENOTSUP;
    case SYS_brk: return 0; /* oldmalloc falls back to anonymous mappings. */
    case SYS_mremap: return -ENOMEM; /* realloc can allocate and copy. */
    case SYS_open: return open_readonly((const char *)a, b);
    case SYS_openat: return a == AT_FDCWD ? open_readonly((const char *)b, c) : -ENOTSUP;
    case SYS_read: return transfer(0, a, (void *)b, c);
    case SYS_write: return a == 1 || a == 2 ? transfer(1, a, (void *)b, c) : -EBADF;
    case SYS_readv: return vector_io(0, a, (struct iovec *)b, c);
    case SYS_writev: return a == 1 || a == 2 ? vector_io(1, a, (struct iovec *)b, c) : -EBADF;
    case SYS_lseek: return zenith_call(406, a, b, c);
    case SYS_stat: case SYS_lstat: return path_stat((const char *)a, n == SYS_stat, (struct stat *)b);
    case SYS_access: {
        if (!b) {
            struct stat st;
            return path_stat((const char *)a, 1, &st);
        }
        if (b != R_OK) return -EACCES;
        const char *path = (const char *)a;
        long fd = zenith_call(2, a, strlen(path), Z_OPEN_READ);
        return fd < 0 ? fd : zenith_call(3, fd, 0, 0);
    }
    case SYS_fstat:
        for (size_t i = 0; i < sizeof(files)/sizeof(files[0]); ++i) {
            if (a >= 3 && files[i].fd == a) { *(struct stat *)b = files[i].info; return 0; }
        }
        return -EBADF;
    case SYS_close:
        for (size_t i = 0; i < sizeof(files)/sizeof(files[0]); ++i)
            if (a >= 3 && files[i].fd == a) files[i].fd = 0;
        return zenith_call(3, a, 0, 0);
    case SYS_getcwd: return zenith_call(79, a, b, 0);
    case SYS_ioctl: return -ENOTTY;
    case SYS_clock_gettime: return clock_value(a, (struct timespec *)b);
    case SYS_getpid: return zenith_identity(39);
    case SYS_getuid: case SYS_geteuid: return zenith_identity(102);
    case SYS_getgid: case SYS_getegid: return zenith_identity(104);
    case SYS_exit: case SYS_exit_group: zenith_exit(a);
    default: return -ENOSYS;
    }
}
