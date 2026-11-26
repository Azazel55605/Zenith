/* Exercise response validation with deterministic single-request transcripts. */
#include "wire.h"
#include <assert.h>

struct fixture {
    uint8_t frame[84];
    size_t read_at;
    int writes;
};

static int send_bytes(void *user, const void *bytes, size_t n)
{
    struct fixture *f = user;
    (void)bytes;
    ++f->writes;
    return (int)n;
}

static int read_bytes(void *user, void *bytes, size_t n)
{
    struct fixture *f = user;
    if (n > 3) n = 3; /* exercise short transport reads */
    if (n > sizeof(f->frame) - f->read_at) return -1;
    memcpy(bytes, f->frame + f->read_at, n);
    f->read_at += n;
    return (int)n;
}

enum transcript { VALID, MISSING_SIGNATURE, WRONG_SIGNATURE, WRONG_MESSAGE_ID, TRUNCATED };
static void check_reply(unsigned dialect, enum transcript test)
{
    struct fixture f = {0};
    struct smb_io io = { .user = &f, .send = send_bytes, .recv = read_bytes };
    static struct smb_session s;
    memset(&s, 0, sizeof(s));
    s.packet = s.transport + 4;
    s.io = &io;
    s.credits = 1;
    s.dialect = (uint16_t)dialect;
    s.authenticated = 1;
    s.sid = 0x654323;
    s.tid = 7;
    for (unsigned i = 0; i < 16; ++i) s.signing[i] = (uint8_t)(3 * i);
    smb_request_start(&s, SMB_ECHO, 4);
    uint8_t *p = f.frame + 4;
    f.frame[3] = 68;
    memcpy(p, "\xfeSMB", 4);
    w16(p + 4, 64);
    w16(p + 12, SMB_ECHO);
    w32(p + 16, test == MISSING_SIGNATURE ? 1 : 9);
    w64(p + 24, test == WRONG_MESSAGE_ID ? 1 : 0);
    w32(p + 36, s.tid);
    w64(p + 40, s.sid);
    w16(p + 64, 4);
    uint8_t sig[32];
    if (dialect == 0x210) smb_hmac_sha256(s.signing, p, 68, sig);
    else smb_cmac(s.signing, p, 68, sig);
    memcpy(p + 48, sig, 16);
    if (test == WRONG_SIGNATURE) p[48] ^= 1;
    if (test == TRUNCATED) f.frame[3] = 81;
    int result = smb_exchange(&s, 68);
    assert(f.writes == 1);
    if (test == VALID) {
        assert(result == 0 && s.status == 0 && s.received == 68);
        assert(smb_response(&s, 4) == 0);
    } else {
        assert(result < 0 && s.broken);
        /* A failed verification cannot be followed by a file mutation. */
        assert(smb_exchange(&s, 68) < 0 && f.writes == 1);
    }
}

int main(void)
{
    for (int t = VALID; t <= TRUNCATED; ++t) {
        check_reply(0x210, (enum transcript)t);
        check_reply(0x302, (enum transcript)t);
    }
    char utf8[16];
    uint8_t wide[16];
    int n = smb_utf16("Grüße🌐", wide, sizeof(wide));
    assert(n == 14 && smb_utf8(wide, (size_t)n, utf8, sizeof(utf8)) == 11);
    assert(!strcmp(utf8, "Grüße🌐"));
    assert(smb_utf16("\xC0\x80", wide, sizeof(wide)) < 0);
    assert(smb_utf16("\xF4\x90\x80\x80", wide, sizeof(wide)) < 0);
    assert(smb_utf8((uint8_t[]){0, 0xd8}, 2, utf8, sizeof(utf8)) < 0);
    return 0;
}
