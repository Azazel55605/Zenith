/* Host-only transport for exercising the exact CLI, authentication and SMB wire code. */
#define _GNU_SOURCE
#include "../../../userland/smb/platform.h"
#include <fcntl.h>
#include <limits.h>
#include <netdb.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/random.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/time.h>
#include <termios.h>
#include <unistd.h>

static int sock = -1, src = -1, sink = -1, replace, changed_tty;
static uint64_t expected, written, received, sent_wire, received_wire;
static int drop_response;
static char stage[PATH_MAX];
static const char *dest;
static struct termios original_tty;

int platform_output(int fd, const void *data, size_t n)
{
    const char *p = data;
    while (n) {
        ssize_t count = write(fd, p, n);
        if (count <= 0)
            return -1;
        p += count;
        n -= (size_t)count;
    }
    return 0;
}
int platform_open_socket(const char *server, unsigned port)
{
    char service[8];
    snprintf(service, sizeof(service), "%u", port);
    struct addrinfo hint = {.ai_socktype = SOCK_STREAM}, *addresses;
    if (getaddrinfo(server, service, &hint, &addresses))
        return -1;
    for (struct addrinfo *a = addresses; a; a = a->ai_next) {
        sock = socket(a->ai_family, a->ai_socktype, a->ai_protocol);
        if (sock < 0)
            continue;
        struct timeval timeout = {.tv_sec = 15};
        setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
        setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout));
        if (!connect(sock, a->ai_addr, a->ai_addrlen))
            break;
        close(sock);
        sock = -1;
    }
    freeaddrinfo(addresses);
    return sock < 0 ? -1 : 0;
}
int platform_send(void *unused, const void *p, size_t n)
{
    (void)unused;
    const char *cut = getenv("ZENITH_SMB_TEST_DROP_PUBLISH");
    const unsigned char *wire = p;
    const char *create_cut = getenv("ZENITH_SMB_TEST_DROP_STAGE");
    if (create_cut && n >= 180 && wire[0] == 0 && !memcmp(wire + 4, "\xfeSMB", 4) &&
        wire[16] == 5 && (wire[24] || wire[25])) {
        if (!strcmp(create_cut, "ack"))
            drop_response = 1;
        else {
            /* A partial direct-TCP frame must not create an unarmed sibling. */
            if (!strcmp(create_cut, "partial"))
                send(sock, p, 128, MSG_NOSIGNAL);
            shutdown(sock, SHUT_RDWR);
            return -1;
        }
    }
    /* Only the final pair of signed SET_INFO operations is compounded. */
    if (cut && n >= 108 && wire[0] == 0 && !memcmp(wire + 4, "\xfeSMB", 4) &&
        wire[16] == 17 && wire[17] == 0 && wire[24] == 104) {
        if (!strcmp(cut, "before")) {
            shutdown(sock, SHUT_RDWR);
            return -1;
        }
        drop_response = !strcmp(cut, "ack");
    }
    if (sent_wire >= 131072 && getenv("ZENITH_SMB_TEST_DISCONNECT_SEND")) {
        shutdown(sock, SHUT_RDWR);
        return -1;
    }
    ssize_t sent = send(sock, p, n > 4093 ? 4093 : n, MSG_NOSIGNAL);
    if (sent > 0)
        sent_wire += (size_t)sent;
    return (int)sent;
}
int platform_recv(void *unused, void *p, size_t n)
{
    (void)unused;
    if (drop_response) {
        unsigned char byte;
        /* Wait for the server to have processed the complete compound. */
        if (recv(sock, &byte, 1, MSG_PEEK) == 1)
            shutdown(sock, SHUT_RDWR);
        return -1;
    }
    if (received_wire >= 131072 && getenv("ZENITH_SMB_TEST_DISCONNECT_RECV")) {
        shutdown(sock, SHUT_RDWR);
        return -1;
    }
    ssize_t received = recv(sock, p, n > 4093 ? 4093 : n, 0);
    if (received > 0)
        received_wire += (size_t)received;
    return (int)received;
}
int platform_random(void *unused, void *p, size_t n)
{
    (void)unused;
    return getrandom(p, n, 0) == (ssize_t)n ? 0 : -1;
}
int platform_open_source(const char *path, uint64_t *size)
{
    struct stat st;
    src = open(path, O_RDONLY | O_NONBLOCK);
    if (src < 0 || fstat(src, &st) || !S_ISREG(st.st_mode))
        return -1;
    *size = (uint64_t)st.st_size;
    return 0;
}
int platform_read_source(void *unused, void *data, size_t n)
{
    (void)unused;
    if (received >= 32768 && getenv("ZENITH_SMB_TEST_FAIL_SOURCE"))
        return -1;
    ssize_t got = read(src, data, n > 4096 ? 4096 : n);
    if (got > 0)
        received += (uint64_t)got;
    return (int)got;
}
int platform_begin_sink(const char *path, uint64_t size, int overwrite)
{
    struct stat st;
    if (!overwrite && lstat(path, &st) == 0)
        return -1;
    if (snprintf(stage, sizeof(stage), "%s.smb-XXXXXX", path) >= (int)sizeof(stage))
        return -1;
    sink = mkstemp(stage);
    if (sink < 0) {
        stage[0] = 0;
        return -1;
    }
    expected = size;
    dest = path;
    replace = overwrite;
    return 0;
}
int platform_write_sink(void *unused, const void *p, size_t n)
{
    (void)unused;
    if ((written >= 32768 && getenv("ZENITH_SMB_TEST_FAIL_SINK")) || written > expected ||
        n > expected - written)
        return -1;
    ssize_t r = write(sink, p, n);
    if (r > 0)
        written += (size_t)r;
    return (int)r;
}
int platform_commit_sink(void)
{
    if (sink < 0 || written != expected || fsync(sink) || close(sink))
        return -1;
    sink = -1;
    return replace ? rename(stage, dest) : link(stage, dest);
}
void platform_close(void)
{
    if (src >= 0)
        close(src);
    if (sink >= 0)
        close(sink);
    if (stage[0])
        unlink(stage);
    if (sock >= 0)
        close(sock);
}
int platform_open_private(const char *path, char *out, size_t capacity)
{
    int fd = open(path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK);
    if (fd < 0)
        return -1;
    int result = -1;
    struct stat st;
    if (!fstat(fd, &st) && S_ISREG(st.st_mode) && st.st_uid == getuid() &&
        ((st.st_mode & 07777) == 0600 || (st.st_mode & 07777) == 0400) &&
        st.st_size > 0 && (uint64_t)st.st_size <= capacity) {
        ssize_t n = read(fd, out, capacity);
        char extra;
        if (n == st.st_size && read(fd, &extra, 1) == 0)
            result = (int)n;
    }
    close(fd);
    return result;
}
int platform_password_mode(int from_stdin)
{
    if (from_stdin)
        return isatty(STDIN_FILENO) ? -1 : 0;
    if (tcgetattr(STDIN_FILENO, &original_tty))
        return -1;
    struct termios t = original_tty;
    t.c_lflag &= ~(ECHO | ICANON);
    t.c_cc[VMIN] = 1;
    t.c_cc[VTIME] = 0;
    if (tcsetattr(STDIN_FILENO, TCSAFLUSH, &t))
        return -1;
    changed_tty = 1;
    return 0;
}
int platform_password_read(unsigned char *p, size_t n) { return (int)read(STDIN_FILENO, p, n); }
void platform_password_restore(void)
{
    if (changed_tty)
        tcsetattr(STDIN_FILENO, TCSAFLUSH, &original_tty);
    changed_tty = 0;
}
