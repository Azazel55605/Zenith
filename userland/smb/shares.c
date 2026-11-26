/* One NDR32 MS-SRVS NetrShareEnum operation over the authenticated srvsvc pipe. */
#include "wire.h"

static const uint8_t srvsvc_syntax[20] = {0xc8, 0x4f, 0x32, 0x4b, 0x70, 0x16, 0xd3, 0x01, 0x12, 0x78,
                                          0x5a, 0x47, 0xbf, 0x6e, 0xe1, 0x88, 3,    0,    0,    0};
static const uint8_t ndr32_syntax[20] = {0x04, 0x5d, 0x88, 0x8a, 0xeb, 0x1c, 0xc9, 0x11, 0x9f, 0xe8,
                                         0x08, 0x00, 0x2b, 0x10, 0x48, 0x60, 2,    0,    0,    0};

static int rpc(struct smb_session *s, uint8_t *request, size_t bytes, unsigned type, unsigned expected,
               uint32_t id, const uint8_t **answer, size_t *length)
{
    request[0] = 5;
    request[2] = (uint8_t)type;
    request[3] = 3;
    request[4] = 0x10;
    w16(request + 8, (unsigned)bytes);
    w32(request + 12, id);
    if (smb_ioctl(s, 0x11c017, request, bytes, 8192, answer, length))
        return -1;
    const uint8_t *p = *answer;
    if (*length < 24 || p[0] != 5 || p[1] || p[2] != expected || (p[3] & 3) != 3 || r32(p + 4) != 0x10 ||
        r16(p + 8) != *length || r16(p + 10) || r32(p + 12) != id)
        return fail(s, "invalid srvsvc RPC response");
    return 0;
}

static int bind_pipe(struct smb_session *s)
{
    uint8_t bind[72] = {0};
    w16(bind + 16, 4280);
    w16(bind + 18, 4280);
    bind[24] = 1;
    bind[30] = 1;
    memcpy(bind + 32, srvsvc_syntax, 20);
    memcpy(bind + 52, ndr32_syntax, 20);
    const uint8_t *p;
    size_t n;
    if (rpc(s, bind, sizeof(bind), 11, 12, 1, &p, &n))
        return -1;
    if (n < 26)
        return fail(s, "invalid RPC bind acknowledgement");
    size_t results = (26u + r16(p + 24) + 3u) & ~3u;
    if (!inside(n, results, 28) || p[results] != 1 || r16(p + results + 4) ||
        memcmp(p + results + 8, ndr32_syntax, 20))
        return fail(s, "server refused srvsvc NDR32");
    return 0;
}

static int enumerate(struct smb_session *s, uint32_t id, uint32_t *resume)
{
    uint8_t request[60] = {0};
    w32(request + 16, 36);
    w16(request + 22, 15); /* opnum NetrShareEnum */
    /* ServerName=NULL denotes the server bound by this authenticated pipe.
     * Level and discriminant 0; container { 0, NULL }; unique resume handle. */
    w32(request + 36, 0x20000);
    w32(request + 48, 1024);
    w32(request + 52, 0x20004);
    w32(request + 56, *resume);
    const uint8_t *response;
    size_t received;
    if (rpc(s, request, sizeof(request), 0, 2, id, &response, &received))
        return -1;
    const uint8_t *p = response + 24;
    size_t n = received - 24;
    if (r16(response + 20) || n < 20 || r32(p) || r32(p + 4) || !r32(p + 8))
        return fail(s, "invalid share enumeration response");
    uint32_t count = r32(p + 12);
    size_t at = 20;
    if (count > 1024 || (count && !r32(p + 16)))
        return fail(s, "invalid share count");
    if (r32(p + 16)) {
        if (!inside(n, at, 4 + (size_t)count * 4) || r32(p + at) != count)
            return fail(s, "invalid share array");
        at += 4;
        for (unsigned i = 0; i < count; i++)
            if (!r32(p + at + 4 * i))
                return fail(s, "missing share name");
        at += 4 * count;
    }
    for (unsigned i = 0; i < count; i++) {
        if (!inside(n, at, 12))
            return fail(s, "missing share name");
        uint32_t max = r32(p + at), base = r32(p + at + 4), len = r32(p + at + 8);
        at += 12;
        if (base || !len || len > max || len > SMB_PATH || !inside(n, at, 2 * len) ||
            r16(p + at + 2 * (len - 1)))
            return fail(s, "invalid share name length");
        char name[SMB_PATH];
        if (smb_utf8(p + at, 2 * (len - 1), name, sizeof(name)) <= 0)
            return fail(s, "invalid UTF-16 share name");
        if (s->io->entry(s->io->user, name, 1, 0))
            return fail(s, "share output failed");
        at = (at + 2 * len + 3) & ~(size_t)3;
    }
    if (!inside(n, at, 12))
        return fail(s, "short share enumeration trailer");
    uint32_t more = r32(p + at + 4), next = 0;
    at += 8;
    if (more) {
        if (!inside(n, at, 8))
            return fail(s, "invalid share resume handle");
        next = r32(p + at);
        at += 4;
    }
    if (at + 4 != n)
        return fail(s, "invalid share enumeration trailer");
    uint32_t status = r32(p + at);
    if (!status)
        return 0;
    if (status != 234 || !count || !more || next == *resume)
        return fail(s, "server could not enumerate shares");
    *resume = next;
    return 1;
}

int smb_shares(struct smb_session *s)
{
    if (smb_open(s, "srvsvc", 0x12019fu, 3, 1, 0, 0) || bind_pipe(s))
        return -1;
    uint32_t resume = 0;
    for (unsigned i = 0; i < 256; i++) {
        int result = enumerate(s, i + 2, &resume);
        if (result <= 0)
            return result;
    }
    return fail(s, "share enumeration exceeds limit");
}
