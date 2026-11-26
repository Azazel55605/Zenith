/* Synchronous direct-TCP client. Upload staging and publication use related compounds.
 * See MS-SMB2 chapters 2 and 3 for wire fields and signing rules. */
#include "wire.h"

static struct smb_session session;

const char *smb_error(void)
{
    return session.error ? session.error : "SMB request failed";
}
uint32_t smb_ntstatus(void)
{
    return session.status;
}
void smb_clear(void)
{
    smb_wipe(&session, sizeof(session));
}

int smb_utf16(const char *text, uint8_t *out, size_t cap)
{
    const uint8_t *p = (const uint8_t *)text;
    size_t used = 0;
    while (*p) {
        unsigned c = *p++, extra = 0, min = 0;
        if (c >= 0xf0 && c <= 0xf4) {
            c &= 7;
            extra = 3;
            min = 0x10000;
        } else if (c >= 0xe0 && c <= 0xef) {
            c &= 15;
            extra = 2;
            min = 0x800;
        } else if (c >= 0xc2 && c <= 0xdf) {
            c &= 31;
            extra = 1;
            min = 0x80;
        } else if (c >= 128)
            return -1;
        while (extra--) {
            if ((*p & 0xc0) != 0x80)
                return -1;
            c = (c << 6) | (*p++ & 63);
        }
        if (c < min || c > 0x10ffff || (c >= 0xd800 && c <= 0xdfff))
            return -1;
        if (c >= 0x10000) {
            if (!inside(cap, used, 4))
                return -1;
            c -= 0x10000;
            w16(out + used, 0xd800 + (c >> 10));
            used += 2;
            c = 0xdc00 + (c & 1023);
        }
        if (!inside(cap, used, 2))
            return -1;
        w16(out + used, c);
        used += 2;
    }
    return (int)used;
}

int smb_utf8(const uint8_t *text, size_t n, char *out, size_t cap)
{
    size_t used = 0;
    if (n % 2 || !cap)
        return -1;
    for (size_t i = 0; i < n; i += 2) {
        unsigned c = r16(text + i);
        if (c >= 0xd800 && c <= 0xdbff) {
            if (i + 4 > n || r16(text + i + 2) < 0xdc00 || r16(text + i + 2) > 0xdfff)
                return -1;
            i += 2;
            c = 0x10000 + ((c - 0xd800) << 10) + (r16(text + i) - 0xdc00);
        } else if (!c || (c >= 0xdc00 && c <= 0xdfff))
            return -1;
        size_t count = c < 128 ? 1 : c < 0x800 ? 2 : c < 0x10000 ? 3 : 4;
        if (!inside(cap - 1, used, count))
            return -1;
        for (size_t j = count - 1; j; j--) {
            out[used + j] = (char)(0x80 | (c & 63));
            c >>= 6;
        }
        out[used] = (char)((count == 1 ? 0 : count == 2 ? 0xc0 : count == 3 ? 0xe0 : 0xf0) | c);
        used += count;
    }
    out[used] = 0;
    return (int)used;
}

static int socket_io(struct smb_session *s, uint8_t *data, size_t count, int sending)
{
    while (count) {
        int n = sending ? s->io->send(s->io->user, data, count) : s->io->recv(s->io->user, data, count);
        if (n <= 0 || (size_t)n > count) {
            s->broken = 1;
            return fail(s, "connection interrupted");
        }
        data += n;
        count -= (size_t)n;
    }
    return 0;
}

static void start_at(struct smb_session *s, uint8_t *p, uint64_t message, uint16_t command, uint16_t size)
{
    memset(p, 0, 64 + size);
    memcpy(p, "\xfeSMB", 4);
    w16(p + 4, 64);
    if (command != SMB_NEGOTIATE)
        w16(p + 6, 1);
    w16(p + 12, command);
    w16(p + 14, s->credits < 2 ? 2 : 1);
    w64(p + 24, message);
    w32(p + 36, s->tid);
    w64(p + 40, s->sid);
    w16(p + 64, size);
}

void smb_request_start(struct smb_session *s, uint16_t command, uint16_t size)
{
    start_at(s, s->packet, s->message, command, size);
}

static void signature(struct smb_session *s, uint8_t *p, size_t n, uint8_t out[32])
{
    memset(p + 48, 0, 16);
    if (s->dialect >= 0x300)
        smb_cmac(s->signing, p, n, out);
    else
        smb_hmac_sha256(s->signing, p, n, out);
}

static int protocol_error(struct smb_session *s, const char *why)
{
    s->broken = 1;
    return fail(s, why);
}

static void sign_request(struct smb_session *s, uint8_t *p, size_t n)
{
    uint8_t sign[32];
    w32(p + 16, r32(p + 16) | 8);
    signature(s, p, n, sign);
    memcpy(p + 48, sign, 16);
}

static int read_frame(struct smb_session *s, size_t *n)
{
    uint8_t *frame = s->transport;
    if (socket_io(s, frame, 4, 0))
        return -1;
    *n = ((size_t)frame[1] << 16) | ((size_t)frame[2] << 8) | frame[3];
    if (frame[0] || *n < 64 || *n > SMB_PACKET)
        return protocol_error(s, "invalid SMB frame size");
    return socket_io(s, s->packet, *n, 0);
}

static int validate_reply(struct smb_session *s, uint8_t *p, size_t n, uint16_t command, uint64_t id)
{
    uint32_t flags = r32(p + 16), status = r32(p + 8);
    if (memcmp(p, "\xfeSMB", 4) || r16(p + 4) != 64 || r16(p + 12) != command || !(flags & 1) ||
        r64(p + 24) != id || (s->sid && r64(p + 40) != s->sid) ||
        (!(flags & 2) && s->tid && r32(p + 36) != s->tid))
        return protocol_error(s, "mismatched SMB response");
    if (status == 0x103 && (flags & 2))
        return 1; /* Pending; final response carries data. */
    if (s->authenticated || (command == SMB_SESSION_SETUP && s->sid && !status)) {
        if (!(flags & 8))
            return protocol_error(s, "server omitted required SMB signature");
        uint8_t original[16], sign[32];
        memcpy(original, p + 48, 16);
        signature(s, p, n, sign);
        unsigned difference = 0;
        for (unsigned i = 0; i < 16; i++)
            difference |= original[i] ^ sign[i];
        if (difference)
            return protocol_error(s, "SMB signature verification failed");
    }
    return 0;
}

/* The upload compounds end in SET_INFO. Their signed responses can arrive
 * together or in separate direct-TCP frames. */
static int transact(struct smb_session *s, size_t len, size_t second)
{
    uint8_t *p = s->packet, *frame = s->transport;
    unsigned parts = second ? 2 : 1;
    if (s->broken || len < 64 || len > SMB_PACKET ||
        (second && (second < 64 || (second & 7) || !inside(len, second, 64))))
        return protocol_error(s, "invalid request or closed connection");
    if (s->credits < parts)
        return fail(s, "server granted insufficient SMB credits");
    uint16_t command = r16(p + 12);
    uint64_t id = s->message;
    if (second) {
        w32(p + 20, (uint32_t)second);
        w32(p + second + 16, 4); /* SMB2_FLAGS_RELATED_OPERATIONS */
        sign_request(s, p + second, len - second);
    }
    if (s->authenticated)
        sign_request(s, p, second ? second : len);
    s->credits -= parts;
    s->message += parts;
    frame[0] = 0;
    frame[1] = (uint8_t)(len >> 16);
    frame[2] = (uint8_t)(len >> 8);
    frame[3] = (uint8_t)len;
    if (socket_io(s, frame, len + 4, 1))
        return -1;
    unsigned done = 0;
    uint32_t first_error = 0;
    for (unsigned frames = 0; frames < 10; frames++) {
        size_t bytes;
        if (read_frame(s, &bytes))
            return -1;
        size_t at = 0;
        do {
            if (done >= parts || !inside(bytes, at, 64))
                return protocol_error(s, "unexpected compound response");
            uint8_t *reply = p + at;
            size_t next = r32(reply + 20), size = next ? next : bytes - at;
            if ((next & 7) || size < 64 || !inside(bytes, at, size) ||
                (next && (done + 1 >= parts || !inside(bytes, at + size, 64))))
                return protocol_error(s, "invalid compound response boundary");
            uint16_t expected = done ? SMB_SET_INFO : command;
            int check = validate_reply(s, reply, size, expected, id + done);
            if (check < 0)
                return -1;
            s->credits += r16(reply + 14);
            if (s->credits > 65535)
                s->credits = 65535;
            if (!check) {
                uint32_t status = r32(reply + 8);
                if (!first_error)
                    first_error = status;
                if (second && !status) {
                    unsigned shape = expected == SMB_CREATE ? 89 : 2;
                    if (size < 64 + (shape & ~1u) || r16(reply + 64) != shape)
                        return protocol_error(s, "invalid upload compound response");
                    if (expected == SMB_CREATE)
                        memcpy(s->file, reply + 128, 16);
                }
                done++;
                s->received = size;
            }
            at += size;
        } while (at < bytes);
        if (done == parts) {
            s->status = first_error;
            return 0;
        }
    }
    return protocol_error(s, "too many pending SMB responses");
}

int smb_exchange(struct smb_session *s, size_t len)
{
    return transact(s, len, 0);
}

int smb_response(struct smb_session *s, unsigned size)
{
    if (s->status) {
        switch (s->status) {
        case 0xc0000022u:
            return fail(s, "access denied");
        case 0xc0000035u:
            return fail(s, "destination already exists");
        case 0xc000006du:
            return fail(s, "logon failed");
        case 0xc00000ccu:
            return fail(s, "share not found");
        case 0xc0000034u:
        case 0xc000003au:
            return fail(s, "path not found");
        case 0xc000007fu:
            return fail(s, "server volume is full");
        default:
            return fail(s, "server rejected request");
        }
    }
    if (!inside(s->received, 64, size & ~1u) || r16(s->packet + 64) != size)
        return protocol_error(s, "invalid SMB response structure");
    return 0;
}

static const uint16_t dialects[] = {0x210, 0x300, 0x302};

static int negotiate(struct smb_session *s)
{
    if (s->io->random(s->io->user, s->client_guid, 16))
        return fail(s, "secure random unavailable");
    smb_request_start(s, SMB_NEGOTIATE, 36);
    uint8_t *p = s->packet + 64;
    w16(p + 2, 3);
    w16(p + 4, 3);
    memcpy(p + 12, s->client_guid, 16);
    for (unsigned i = 0; i < 3; i++)
        w16(p + 36 + 2 * i, dialects[i]);
    if (smb_exchange(s, 106) || smb_response(s, 65))
        return -1;
    s->security = r16(p + 2);
    s->dialect = r16(p + 4);
    s->capabilities = r32(p + 24);
    if (!(s->security & 1) || (s->dialect != 0x210 && s->dialect != 0x300 && s->dialect != 0x302))
        return fail(s, "signed SMB 2.1, 3.0 or 3.0.2 required");
    memcpy(s->guid, p + 8, 16);
    s->max_read = r32(p + 32);
    s->max_write = r32(p + 36);
    if (s->max_read < 1024 || s->max_write < 1024)
        return fail(s, "server transfer limit too small");
    return 0;
}

static int tree_connect(struct smb_session *s, const char *share)
{
    char unc[520];
    size_t host = strlen(s->req->server), len = strlen(share);
    if (!host || host > 255 || !len || len > 255)
        return fail(s, "invalid share or server name");
    unc[0] = unc[1] = '\\';
    memcpy(unc + 2, s->req->server, host);
    unc[host + 2] = '\\';
    memcpy(unc + 3 + host, share, len + 1);
    smb_request_start(s, SMB_TREE_CONNECT, 9);
    uint8_t *p = s->packet + 64;
    int n = smb_utf16(unc, p + 8, 2 * sizeof(unc));
    if (n < 0)
        return fail(s, "invalid share name");
    w16(p + 4, 72);
    w16(p + 6, (unsigned)n);
    if (smb_exchange(s, 72 + (size_t)n) || smb_response(s, 16))
        return -1;
    if (r32(p + 4) & 0x8000u)
        return fail(s, "share requires SMB encryption");
    if (p[2] != (s->req->operation == SMB_SHARES ? 2 : 1))
        return fail(s, "unsupported share type");
    s->tid = r32(s->packet + 36);
    return 0;
}

int smb_ioctl(struct smb_session *s, uint32_t code, const uint8_t *data, size_t n, size_t max_out,
              const uint8_t **reply, size_t *reply_len)
{
    if (n > SMB_IO_SIZE || max_out > SMB_PACKET - 112)
        return fail(s, "IOCTL exceeds frame limit");
    smb_request_start(s, SMB_IOCTL, 57);
    uint8_t *p = s->packet + 64;
    w32(p + 4, code);
    memcpy(p + 8, s->file, 16);
    w32(p + 24, 120);
    w32(p + 28, (uint32_t)n);
    w32(p + 44, (uint32_t)max_out);
    w32(p + 48, 1);
    memcpy(p + 56, data, n);
    if (smb_exchange(s, 120 + n) || smb_response(s, 49))
        return -1;
    size_t offset = r32(p + 32), count = r32(p + 36);
    if (r32(p + 4) != code || offset < 112 || !inside(s->received, offset, count) || count > max_out)
        return protocol_error(s, "invalid SMB IOCTL response");
    *reply = s->packet + offset;
    *reply_len = count;
    return 0;
}

static int validate_negotiation(struct smb_session *s)
{
    uint8_t request[30] = {0};
    memcpy(request + 4, s->client_guid, 16);
    w16(request + 20, 3);
    w16(request + 22, 3);
    for (unsigned i = 0; i < 3; i++)
        w16(request + 24 + 2 * i, dialects[i]);
    memset(s->file, 0xff, 16);
    const uint8_t *p;
    size_t n;
    if (smb_ioctl(s, 0x140204, request, sizeof(request), 24, &p, &n)) {
        /* SMB2.1 servers predate this FSCTL. Accept only signed unsupported
         * responses on that dialect; the session cannot proceed unsigned. */
        if (!s->broken && s->dialect == 0x210 && (s->status == 0xc0000010u || s->status == 0xc00000bbu)) {
            s->status = 0;
            s->error = 0;
            return 0;
        }
        return -1;
    }
    if (n != 24 || r32(p) != s->capabilities || memcmp(p + 4, s->guid, 16) || r16(p + 20) != s->security ||
        r16(p + 22) != s->dialect)
        return protocol_error(s, "signed SMB negotiation changed");
    return 0;
}

static int create_request(struct smb_session *s, const char *path, uint32_t access, uint32_t sharing,
                          uint32_t disposition, uint32_t options)
{
    smb_request_start(s, SMB_CREATE, 57);
    uint8_t *p = s->packet + 64;
    w32(p + 4, 2);
    w32(p + 24, access);
    w32(p + 32, sharing);
    w32(p + 36, disposition);
    w32(p + 40, options);
    int n = smb_utf16(path, p + 56, 2 * SMB_PATH);
    if (n < 0)
        return fail(s, "invalid remote path");
    for (int i = 0; i < n; i += 2)
        if (r16(p + 56 + i) == '/')
            w16(p + 56 + i, '\\');
    w16(p + 44, 120);
    w16(p + 46, (unsigned)n);
    return n ? 120 + n : 121;
}

int smb_open(struct smb_session *s, const char *path, uint32_t access, uint32_t sharing, uint32_t disposition,
             uint32_t options, uint64_t *size)
{
    int n = create_request(s, path, access, sharing, disposition, options);
    uint8_t *p = s->packet + 64;
    if (n < 0 || smb_exchange(s, (size_t)n) || smb_response(s, 89))
        return -1;
    memcpy(s->file, p + 64, 16);
    if (size)
        *size = r64(p + 48);
    return 0;
}

static int close_file(struct smb_session *s)
{
    smb_request_start(s, SMB_CLOSE, 24);
    memcpy(s->packet + 72, s->file, 16);
    return smb_exchange(s, 88) || smb_response(s, 60) ? -1 : 0;
}

static int directory(struct smb_session *s)
{
    if (smb_open(s, s->req->path, 0x81, 7, 1, 1, 0))
        return -1;
    for (unsigned page = 0; page < 4096; page++) {
        smb_request_start(s, SMB_QUERY_DIR, 33);
        uint8_t *p = s->packet + 64;
        p[2] = 1;
        memcpy(p + 8, s->file, 16);
        /* The search handle retains its cursor with RestartScans clear.
         * Some SMB servers require the search pattern on subsequent pages. */
        w16(p + 24, 96);
        w16(p + 26, 2);
        w16(p + 32, '*');
        w32(p + 28, SMB_IO_SIZE);
        if (smb_exchange(s, 98))
            return -1;
        if (s->status == NT_NO_MORE_FILES || (!page && s->status == 0xc000000fu)) {
            s->status = 0;
            return close_file(s);
        }
        if (smb_response(s, 9))
            return -1;
        size_t offset = r16(p + 2), len = r32(p + 4);
        if (offset < 72 || !len || !inside(s->received, offset, len))
            return protocol_error(s, "invalid directory response");
        p = s->packet + offset;
        for (;;) {
            if (len < 64)
                return protocol_error(s, "short directory record");
            size_t next = r32(p), namesize = r32(p + 60);
            size_t record = next ? next : len;
            if (next > len || (next & 7) || !inside(record, 64, namesize))
                return protocol_error(s, "invalid directory record");
            char name[SMB_PATH];
            if (smb_utf8(p + 64, namesize, name, sizeof(name)) <= 0)
                return fail(s, "invalid directory name");
            if (strcmp(name, ".") && strcmp(name, "..") &&
                s->io->entry(s->io->user, name, (r32(p + 56) & 16) != 0, r64(p + 40)))
                return fail(s, "directory output failed");
            if (!next)
                break;
            len -= next;
            p += next;
        }
    }
    return fail(s, "directory exceeds enumeration limit");
}

static int download(struct smb_session *s)
{
    uint64_t size = 0;
    if (smb_open(s, s->req->path, 0x81, 1, 1, 0x40, &size))
        return -1;
    if (s->io->download_begin(s->io->user, size))
        return fail(s, "cannot stage local download");
    uint32_t limit = s->max_read < SMB_IO_SIZE ? s->max_read : SMB_IO_SIZE;
    for (uint64_t at = 0; at < size;) {
        smb_request_start(s, SMB_READ, 49);
        uint8_t *p = s->packet + 64;
        uint32_t wanted = size - at < limit ? (uint32_t)(size - at) : limit;
        w32(p + 4, wanted);
        w64(p + 8, at);
        memcpy(p + 16, s->file, 16);
        if (smb_exchange(s, 113) || smb_response(s, 17))
            return -1;
        size_t off = p[2], n = r32(p + 4);
        if (!n || n > wanted || off < 80 || !inside(s->received, off, n))
            return protocol_error(s, "invalid read response");
        while (n) {
            int written = s->io->download_write(s->io->user, s->packet + off, n);
            if (written <= 0 || (size_t)written > n)
                return fail(s, "local download write failed");
            off += (size_t)written;
            n -= (size_t)written;
            at += (size_t)written;
        }
    }
    return close_file(s);
}

static void make_disposition(struct smb_session *s, size_t at, int deleted)
{
    uint8_t *request = s->packet + at;
    start_at(s, request, s->message + (at != 0), SMB_SET_INFO, 33);
    uint8_t *p = request + 64;
    p[2] = 1;
    p[3] = 13;
    w32(p + 4, 1);
    w16(p + 8, 96);
    if (at)
        memset(p + 16, 0xff, 16); /* FileId returned by the related CREATE */
    else
        memcpy(p + 16, s->file, 16);
    p[32] = (uint8_t)deleted;
}

static int disposition(struct smb_session *s, int deleted)
{
    make_disposition(s, 0, deleted);
    return smb_exchange(s, 97) || smb_response(s, 2) ? -1 : 0;
}

static int publish_file(struct smb_session *s)
{
    if (s->credits < 2)
        return fail(s, "server must grant two credits to publish an upload");
    make_disposition(s, 0, 0);
    const size_t second = 104; /* align the 97-byte disposition request */
    memset(s->packet + 97, 0, second - 97);
    start_at(s, s->packet + second, s->message + 1, SMB_SET_INFO, 33);
    uint8_t *p = s->packet + second + 64;
    p[2] = 1;
    p[3] = 10;
    w16(p + 8, 96);
    memcpy(p + 16, s->file, 16);
    memset(p + 32, 0, 20);
    p[32] = (uint8_t)(s->req->replace != 0);
    int n = smb_utf16(s->req->path, p + 52, 2 * SMB_PATH);
    if (n <= 0)
        return fail(s, "invalid upload filename");
    for (int i = 0; i < n; i += 2)
        if (r16(p + 52 + i) == '/')
            w16(p + 52 + i, '\\');
    w32(p + 48, (uint32_t)n);
    w32(p + 4, (uint32_t)n + 20);
    if (transact(s, second + 116 + (size_t)n, second)) {
        if (s->broken)
            return fail(s, "upload commit acknowledgement lost; check destination before retrying");
        return -1;
    }
    return s->status ? smb_response(s, 2) : 0;
}

static int upload(struct smb_session *s)
{
    char tmp[SMB_PATH];
    uint8_t random[12];
    const char *path = s->req->path;
    size_t prefix = 0;
    for (size_t i = 0; path[i]; i++)
        if (path[i] == '/' || path[i] == '\\')
            prefix = i + 1;
    if (!*path || prefix + 10 + 2 * sizeof(random) >= sizeof(tmp))
        return fail(s, "invalid upload path");
    if (s->io->random(s->io->user, random, sizeof(random)))
        return fail(s, "secure random unavailable");
    memcpy(tmp, path, prefix);
    memcpy(tmp + prefix, ".zn-smb-", 8);
    prefix += 8;
    static const char hex[] = "0123456789abcdef";
    for (size_t i = 0; i < sizeof(random); i++) {
        tmp[prefix++] = hex[random[i] >> 4];
        tmp[prefix++] = hex[random[i] & 15];
    }
    tmp[prefix] = 0;
    /* Create and arm cleanup in one server request, before sending file data. */
    int n = create_request(s, tmp, 0x10182, 0, 2, 0x40);
    if (n < 0)
        return -1;
    size_t second = ((size_t)n + 7) & ~(size_t)7;
    memset(s->packet + n, 0, second - (size_t)n);
    make_disposition(s, second, 1);
    memset(s->file, 0xff, 16);
    int result = -1;
    if (transact(s, second + 97, second) || (s->status && smb_response(s, 2))) {
        /* The first operation may have failed without opening a handle. */
        if (r64(s->file) == UINT64_MAX && r64(s->file + 8) == UINT64_MAX)
            return -1;
        goto cleanup;
    }
    uint32_t limit = s->max_write < SMB_IO_SIZE ? s->max_write : SMB_IO_SIZE;
    for (uint64_t at = 0; at < s->req->upload_size;) {
        size_t want = s->req->upload_size - at < limit ? (size_t)(s->req->upload_size - at) : limit;
        smb_request_start(s, SMB_WRITE, 49);
        uint8_t *p = s->packet + 64;
        size_t n = 0;
        while (n < want) {
            int bytes = s->io->upload_read(s->io->user, p + 48 + n, want - n);
            if (bytes <= 0 || (size_t)bytes > want - n) {
                fail(s, "local upload source changed or read failed");
                goto cleanup;
            }
            n += (size_t)bytes;
        }
        w16(p + 2, 112);
        w32(p + 4, (uint32_t)n);
        w64(p + 8, at);
        memcpy(p + 16, s->file, 16);
        if (smb_exchange(s, 112 + (size_t)n) || smb_response(s, 17))
            goto cleanup;
        if (r32(p + 4) != (uint32_t)n) {
            fail(s, "short remote write");
            goto cleanup;
        }
        at += (size_t)n;
    }
    uint8_t last;
    if (s->io->upload_read(s->io->user, &last, 1) != 0) {
        fail(s, "local upload source changed");
        goto cleanup;
    }
    smb_request_start(s, SMB_FLUSH, 24);
    memcpy(s->packet + 72, s->file, 16);
    if (smb_exchange(s, 88) || smb_response(s, 4) || publish_file(s))
        goto cleanup;
    result = 0;
cleanup:
    if (result < 0 && !s->broken) {
        const char *why = s->error;
        uint32_t status = s->status;
        disposition(s, 1);
        if (!s->broken)
            close_file(s);
        s->error = why;
        s->status = status;
    }
    if (result == 0)
        return close_file(s);
    return result;
}

int smb_execute(const struct smb_io *io, const struct smb_request *req)
{
    struct smb_session *s = &session;
    smb_clear();
    s->packet = s->transport + 4;
    s->io = io;
    s->credits = 1;
    s->req = req;
    if (!io || !req || !io->send || !io->recv || !io->random || !req->server || !req->domain ||
        !req->username || !req->password || !req->username[0] || !req->password[0] ||
        (req->operation != SMB_SHARES && (!req->share || !req->path)))
        return fail(s, "missing SMB credentials or request");
    if (negotiate(s) || smb_auth(s) || tree_connect(s, req->operation == SMB_SHARES ? "IPC$" : req->share) ||
        validate_negotiation(s))
        return -1;
    switch (req->operation) {
    case SMB_SHARES:
        if (io->entry)
            return smb_shares(s);
        break;
    case SMB_LIST:
        if (io->entry)
            return directory(s);
        break;
    case SMB_GET:
        if (io->download_begin && io->download_write)
            return download(s);
        break;
    case SMB_PUT:
        if (io->upload_read)
            return upload(s);
        break;
    }
    return fail(s, "unsupported SMB operation");
}
