#include "../sunbeam/zenith.h"
#include "platform.h"

struct net_request {
    uint32_t operation, flags;
    uint64_t input, length, target, target_length, output, capacity, value, extra, reserved;
};
_Static_assert(sizeof(struct net_request) == 80, "native network request");
static long socket_handle = -1, upload = -1, sink = -1, saved_mode = -1;
static uint64_t sink_size, source_size, source_offset;

static size_t length(const char *s)
{
    size_t n = 0;
    while (s[n])
        n++;
    return n;
}

static long net(uint32_t op, uint32_t flags, const char *path, void *out, size_t cap, uint64_t value,
                uint64_t extra)
{
    struct net_request r = {
        op, flags, (uint64_t)path, path ? length(path) : 0, 0, 0, (uint64_t)out, cap, value, extra, 0};
    return zenith_call(500, (uintptr_t)&r, sizeof(r), 0);
}

int platform_output(int fd, const void *data, size_t length)
{
    const unsigned char *p = data;
    while (length) {
        long n = zenith_call(1, fd, (uintptr_t)p, length > 4096 ? 4096 : length);
        if (n <= 0)
            return -1;
        p += n;
        length -= (size_t)n;
    }
    return 0;
}

int platform_open_socket(const char *server, unsigned port)
{
    socket_handle = net(8, 0, server, 0, 0, port, 0);
    return socket_handle < 0 ? -1 : 0;
}
int platform_recv(void *unused, void *p, size_t n)
{
    (void)unused;
    return (int)zenith_call(501, socket_handle, (uintptr_t)p, n > 65536 ? 65536 : n);
}
int platform_send(void *unused, const void *p, size_t n)
{
    (void)unused;
    return (int)zenith_call(502, socket_handle, (uintptr_t)p, n > 65536 ? 65536 : n);
}
int platform_random(void *unused, void *out, size_t count)
{
    (void)unused;
    unsigned char *p = out;
    while (count) {
        size_t n = count > 64 ? 64 : count;
        if (net(9, 0, 0, p, n, 0, 0) != (long)n)
            return -1;
        p += n;
        count -= n;
    }
    return 0;
}
int platform_open_source(const char *path, uint64_t *size)
{
    struct zenith_stat st;
    if (zenith_call(4, (uintptr_t)path, length(path), (uintptr_t)&st) < 0 || (st.mode & 0170000) != 0100000)
        return -1;
    upload = zenith_call(2, (uintptr_t)path, length(path), Z_OPEN_READ);
    if (upload < 0)
        return -1;
    long end = zenith_call(406, upload, 0, 2);
    if (end < 0 || zenith_call(406, upload, 0, 0) != 0)
        return -1;
    source_size = *size = (uint64_t)end;
    return 0;
}
int platform_read_source(void *unused, void *data, size_t count)
{
    (void)unused;
    if (source_offset == source_size)
        return zenith_call(406, upload, 0, 2) == (long)source_size ? 0 : -1;
    if (count > source_size - source_offset)
        count = (size_t)(source_size - source_offset);
    long n = zenith_call(0, upload, (uintptr_t)data, count > 4096 ? 4096 : count);
    if (n > 0)
        source_offset += (uint64_t)n;
    return (int)n;
}
int platform_begin_sink(const char *path, uint64_t size, int replace)
{
    sink = net(5, (replace ? 1u : 0u) | 2u, path, 0, 0, size, 0);
    sink_size = size;
    return sink < 0 ? -1 : 0;
}
int platform_write_sink(void *unused, const void *data, size_t n)
{
    (void)unused;
    return (int)zenith_call(502, sink, (uintptr_t)data, n > 65536 ? 65536 : n);
}
int platform_commit_sink(void)
{
    if (sink < 0 || net(7, 0, 0, 0, 0, (uint64_t)sink, sink_size) < 0)
        return -1;
    sink = -1;
    return 0;
}
void platform_close(void)
{
    if (upload >= 0)
        zenith_call(3, upload, 0, 0);
    if (sink >= 0)
        zenith_call(503, sink, 0, 0);
    if (socket_handle >= 0)
        zenith_call(503, socket_handle, 0, 0);
}
int platform_open_private(const char *path, char *out, size_t cap)
{
    return (int)net(10, 0, path, out, cap, 0, 0);
}
int platform_password_mode(int from_stdin)
{
    long tty = zenith_isatty(0);
    if (from_stdin)
        return tty == 0 ? 0 : -1;
    if (tty != 1)
        return -1;
    long mode = zenith_call(401, 0, 0, 0);
    if (mode < 0 || zenith_call(402, 0, 4, 0) != 0)
        return -1; /* Signals, no echo or canonical line editing. */
    saved_mode = mode;
    return 0;
}
int platform_password_read(unsigned char *data, size_t n)
{
    return (int)zenith_call(0, 0, (uintptr_t)data, n);
}
void platform_password_restore(void)
{
    if (saved_mode >= 0) {
        zenith_call(402, 0, (uintptr_t)saved_mode, 0);
        saved_mode = -1;
    }
}

void *memset(void *dst, int value, size_t n)
{
    unsigned char *d = dst;
    for (size_t i = 0; i < n; i++)
        d[i] = (unsigned char)value;
    return dst;
}
void *memcpy(void *dst, const void *src, size_t n)
{
    unsigned char *d = dst;
    const unsigned char *s = src;
    for (size_t i = 0; i < n; i++)
        d[i] = s[i];
    return dst;
}
int memcmp(const void *left, const void *right, size_t n)
{
    const unsigned char *a = left, *b = right;
    for (size_t i = 0; i < n; i++)
        if (a[i] != b[i])
            return (int)a[i] - (int)b[i];
    return 0;
}
int strcmp(const char *a, const char *b)
{
    while (*a && *a == *b) {
        a++;
        b++;
    }
    return (int)(unsigned char)*a - (int)(unsigned char)*b;
}
size_t strlen(const char *s) { return length(s); }
