/* Exercise the production signed client with two independent loopback transports. */
#include "../../../userland/smb/platform.h"
#include "../../../userland/smb/session.h"
#include <arpa/inet.h>
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>

void check_directory_cursors(struct smb_session *s);
void check_live_reader(struct smb_session *reader, struct smb_session *writer);
void check_cli_read_sharing(int fd, const struct smb_io *transport, struct smb_request request,
                            struct smb_session *peer);

static int socket_send(void *context, const void *p, size_t n)
{
    return (int)send(*(int *)context, p, n, MSG_NOSIGNAL);
}

static int socket_recv(void *context, void *p, size_t n)
{
    return (int)recv(*(int *)context, p, n, 0);
}

static int connection(unsigned port)
{
    struct sockaddr_in address = {.sin_family = AF_INET, .sin_port = htons(port),
                                  .sin_addr = {.s_addr = htonl(INADDR_LOOPBACK)}};
    int fd = socket(AF_INET, SOCK_STREAM, 0);
    assert(fd >= 0);
    struct timeval limit = {.tv_sec = 10};
    assert(!setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &limit, sizeof(limit)));
    assert(!setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &limit, sizeof(limit)));
    assert(!connect(fd, (const struct sockaddr *)&address, sizeof(address)));
    return fd;
}

int main(int argc, char **argv)
{
    static struct smb_session s, other;
    char password[256], data[64];
    assert(argc == 2 && fgets(password, sizeof(password), stdin));
    password[strcspn(password, "\n")] = 0;
    struct smb_request req = {.server = "127.0.0.1", .share = "transfer", .domain = "WORKGROUP",
                              .username = "zenithtest", .password = password};
    unsigned port = (unsigned)strtoul(argv[1], NULL, 10);
    int fd = connection(port), second = connection(port);
    struct smb_io io = {.user = &fd, .send = socket_send, .recv = socket_recv, .random = platform_random};
    struct smb_io other_io = io;
    other_io.user = &second;
    assert(!smb_session_connect(&s, &io, &req));
    assert(!smb_session_connect(&other, &other_io, &req));
    assert(smb_session_connect(&s, &io, &req) < 0);
    check_directory_cursors(&s);
    check_live_reader(&s, &other);
    int copy_fd = connection(port);
    check_cli_read_sharing(copy_fd, &io, req, &other);
    close(copy_fd);

    struct smb_read_handle large, small, second_file, stale;
    uint64_t size;
    const uint64_t position = UINT64_C(0x100000011);
    assert(!smb_session_open_read(&s, "session-large", &large, &size) && size == position + 6);
    assert(!smb_session_open_read(&s, "session-small", &small, &size) && size == 5);
    assert(!smb_session_open_read(&other, "session-small", &second_file, NULL));
    assert(smb_session_read_at(&other, large, 0, data, sizeof(data)) < 0);
    assert(smb_session_read_at(&s, second_file, 0, data, sizeof(data)) < 0);
    assert(smb_session_read_at(&s, large, position, data, sizeof(data)) == 6 && !memcmp(data, "BEYOND", 6));
    assert(smb_session_read_at(&s, small, 1, data, 2) == 2 && !memcmp(data, "ma", 2));
    assert(smb_session_read_at(&s, large, 3, data, 3) == 3 && !memcmp(data, "LOW", 3));
    memset(data, 0xff, sizeof(data));
    assert(smb_session_read_at(&s, large, position - 3, data, 6) == 6 && !memcmp(data, "\0\0\0BEY", 6));
    assert(smb_session_read_at(&s, large, position + 6, data, sizeof(data)) == 0);
    assert(smb_session_read_at(&s, small, UINT64_MAX, data, sizeof(data)) == 0);
    assert(smb_session_read_at(&s, small, 0, NULL, 0) == 0);
    assert(!smb_session_close_read(&s, small));
    stale = small;
    assert(!smb_session_open_read(&s, "session-small", &small, NULL));
    assert(smb_session_read_at(&s, stale, 0, data, 1) < 0 && smb_session_close_read(&s, stale) < 0);
    assert(smb_session_open_read(&s, "../session-small", &stale, NULL) < 0);
    assert(smb_session_open_read(&s, "cursor-empty", &stale, NULL) < 0);
    assert(smb_session_open_read(&s, "missing-file", &stale, NULL) < 0);
    assert(!smb_session_close_read(&s, small));
    struct smb_read_handle slots[SMB_MAX_OPEN_READS - 1];
    for (unsigned i = 0; i < SMB_MAX_OPEN_READS - 1; i++)
        assert(!smb_session_open_read(&s, "session-small", &slots[i], NULL));
    assert(smb_session_open_read(&s, "session-small", &small, NULL) < 0);
    assert(!smb_session_disconnect(&s));
    close(fd);
    fd = connection(port);
    assert(!smb_session_connect(&s, &io, &req));
    assert(!smb_session_open_read(&s, "session-small", &small, NULL));
    assert(smb_session_read_at(&s, large, 0, data, 1) < 0);
    assert(smb_session_close_read(&s, slots[0]) < 0);
    assert(smb_session_read_at(&other, second_file, 0, data, 5) == 5 && !memcmp(data, "small", 5));
    assert(!shutdown(fd, SHUT_RDWR));
    assert(smb_session_read_at(&s, small, 0, data, 1) < 0);
    assert(smb_session_read_at(&s, small, 0, data, 1) < 0);
    assert(smb_session_disconnect(&s) < 0);
    close(fd);
    assert(!smb_session_disconnect(&other));
    close(second);
    puts("PASS signed read sessions and interleaved offset reads above 4 GiB, EOF, handle limits, reconnect and disconnect");
}
