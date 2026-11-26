/* MS-NLMP 3.3.2 (NTLMv2) and RFC 4178 NegTokenInit / NegTokenResp. */
#include "wire.h"

static const uint8_t spnego[] = {0x2b, 6, 1, 5, 5, 2};
static const uint8_t ntlm_oid[] = {0x2b, 6, 1, 4, 1, 0x82, 0x37, 2, 2, 10};
static const uint8_t ntlm_header[] = {'N', 'T', 'L', 'M', 'S', 'S', 'P', 0};
enum { AUTH_CAP = 8192 };
struct auth_workspace {
    uint8_t first[40], challenge[4096], auth[AUTH_CAP], blob[4096], converted[1024];
    uint8_t nthash[16], response_key[16], proof[16], mic[16], nonce[8];
};

static size_t der_len(size_t n)
{
    return n < 128 ? 2 : n < 256 ? 3 : 4;
}

static uint8_t *der(uint8_t *out, unsigned tag, size_t n)
{
    *out++ = (uint8_t)tag;
    if (n < 128)
        *out++ = (uint8_t)n;
    else if (n < 256) {
        *out++ = 0x81;
        *out++ = (uint8_t)n;
    } else {
        *out++ = 0x82;
        *out++ = (uint8_t)(n >> 8);
        *out++ = (uint8_t)n;
    }
    return out;
}

static int token(const uint8_t **p, const uint8_t *end, unsigned tag, const uint8_t **value, size_t *size)
{
    if (end - *p < 2 || *(*p)++ != tag)
        return -1;
    size_t n = *(*p)++;
    if (n >= 128) {
        size_t bytes = n & 127;
        if (!bytes || bytes > 2 || (size_t)(end - *p) < bytes || !**p)
            return -1;
        n = 0;
        while (bytes--)
            n = (n << 8) | *(*p)++;
        if (n < 128)
            return -1;
    }
    if (n > (size_t)(end - *p))
        return -1;
    *value = *p;
    *size = n;
    *p += n;
    return 0;
}

static int challenge_token(const uint8_t *p, size_t n, const uint8_t **out, size_t *len)
{
    const uint8_t *end = p + n, *body, *val;
    size_t size;
    if (token(&p, end, 0xa1, &body, &size) || p != end)
        return -1;
    p = body;
    end = p + size;
    if (token(&p, end, 0x30, &body, &size) || p != end)
        return -1;
    p = body;
    end = p + size;
    int seen_oid = 0;
    *len = 0;
    while (p < end) {
        unsigned tag = *p;
        if (token(&p, end, tag, &body, &size))
            return -1;
        const uint8_t *limit = body + size;
        if (tag == 0xa0) {
            if (token(&body, limit, 0x0a, &val, &size) || body != limit || size != 1 || *val != 1)
                return -1;
        } else if (tag == 0xa1) {
            if (seen_oid || token(&body, limit, 6, &val, &size) || body != limit ||
                size != sizeof(ntlm_oid) || memcmp(val, ntlm_oid, size))
                return -1;
            seen_oid = 1;
        } else if (tag == 0xa2) {
            if (*len || token(&body, limit, 4, out, len) || body != limit)
                return -1;
        } else
            return -1;
    }
    return seen_oid && *len ? 0 : -1;
}

static size_t wrap(uint8_t *out, const uint8_t *msg, size_t n, int first)
{
    uint8_t *p = out;
    size_t octets = der_len(n) + n, response = der_len(octets) + octets;
    size_t oidseq = 6 + sizeof(ntlm_oid), seqbody = response + (first ? oidseq : 0);
    size_t seq = der_len(seqbody) + seqbody;
    if (first) {
        p = der(p, 0x60, 2 + sizeof(spnego) + der_len(seq) + seq);
        p = der(p, 6, sizeof(spnego));
        memcpy(p, spnego, sizeof(spnego));
        p += sizeof(spnego);
    }
    p = der(p, first ? 0xa0 : 0xa1, seq);
    p = der(p, 0x30, seqbody);
    if (first) {
        p = der(p, 0xa0, oidseq - 2);
        p = der(p, 0x30, oidseq - 4);
        p = der(p, 6, sizeof(ntlm_oid));
        memcpy(p, ntlm_oid, sizeof(ntlm_oid));
        p += sizeof(ntlm_oid);
    }
    p = der(p, 0xa2, octets);
    p = der(p, 4, n);
    memcpy(p, msg, n);
    return (size_t)(p - out) + n;
}

static int setup(struct smb_session *s, const uint8_t *msg, size_t n, int first)
{
    smb_request_start(s, SMB_SESSION_SETUP, 25);
    uint8_t *p = s->packet + 64;
    p[3] = 3;
    w16(p + 12, 88);
    n = wrap(p + 24, msg, n, first);
    w16(p + 14, (unsigned)n);
    return smb_exchange(s, 88 + n);
}

static void secbuf(uint8_t *p, size_t n, size_t offset)
{
    w16(p, (unsigned)n);
    w16(p + 2, (unsigned)n);
    w32(p + 4, (uint32_t)offset);
}

#define FLAGS (0x20000000u | 0x02000000u | 0x00800000u | 0x00080000u | 0x00008000u | 0x200u | 4u | 1u)

static int authenticate(struct smb_session *s, const struct smb_request *req, struct auth_workspace *auth)
{
    uint8_t *first = auth->first;
    memcpy(first, ntlm_header, 8);
    w32(first + 8, 1);
    w32(first + 12, FLAGS);
    first[32] = 10;
    first[39] = 15;
    if (setup(s, first, sizeof(auth->first), 1))
        return -1;
    if (s->status != NT_MORE_PROCESSING || !inside(s->received, 64, 8) || r16(s->packet + 64) != 9)
        return fail(s, "expected an NTLMv2 challenge");
    s->sid = r64(s->packet + 40);
    if (!s->sid)
        return fail(s, "missing session identifier");
    size_t offset = r16(s->packet + 68), bytes = r16(s->packet + 70), clen;
    const uint8_t *ch;
    if (offset < 72 || !inside(s->received, offset, bytes) ||
        challenge_token(s->packet + offset, bytes, &ch, &clen) || clen < 48 ||
        clen > sizeof(auth->challenge) || memcmp(ch, ntlm_header, 8) || r32(ch + 8) != 2)
        return fail(s, "invalid NTLMv2 challenge");
    memcpy(auth->challenge, ch, clen);
    ch = auth->challenge;
    uint32_t flags = FLAGS & r32(ch + 20);
    const uint32_t required = 1u | 0x200u | 0x80000u | 0x800000u;
    if ((flags & required) != required)
        return fail(s, "server lacks NTLMv2 target information");
    size_t avsize = r16(ch + 40), avoff = r32(ch + 44);
    if (!avsize || avoff < 48 || !inside(clen, avoff, avsize))
        return fail(s, "invalid NTLM target information");
    uint8_t *blob = auth->blob;
    blob[0] = blob[1] = 1;
    if (s->io->random(s->io->user, auth->nonce, 8))
        return fail(s, "secure random unavailable");
    memcpy(blob + 16, auth->nonce, 8);
    size_t end = 28, consumed = 0;
    int timestamp = 0, ended = 0, avflags = 0;
    while (inside(avsize, consumed, 4)) {
        const uint8_t *av = ch + avoff + consumed;
        size_t n = r16(av + 2);
        unsigned id = r16(av);
        consumed += 4;
        if (!inside(avsize, consumed, n) || end + 4 + n + 32 > sizeof(auth->blob))
            return fail(s, "invalid NTLM attribute");
        if (id == 0) {
            ended = !n && consumed == avsize;
            break;
        }
        if (id == 7) {
            if (timestamp || n != 8)
                return fail(s, "invalid NTLM timestamp");
            memcpy(blob + 8, av + 4, 8);
            timestamp = 1;
        }
        if (id == 9) {
            consumed += n;
            continue;
        } /* use the requested server identity */
        memcpy(blob + end, av, n + 4);
        if (id == 6) {
            if (avflags || n != 4)
                return fail(s, "invalid NTLM flags");
            w32(blob + end + 4, r32(av + 4) | 2);
            avflags = 1;
        }
        end += 4 + n;
        consumed += n;
    }
    if (!timestamp || !ended)
        return fail(s, "NTLM target timestamp or terminator missing");
    if (!avflags) {
        w16(blob + end, 6);
        w16(blob + end + 2, 4);
        w32(blob + end + 4, 2);
        end += 8;
    }
    size_t server_len = strlen(req->server);
    if (server_len > 255)
        return fail(s, "server name too long");
    char service[261] = "cifs/";
    memcpy(service + 5, req->server, server_len + 1);
    int spn = smb_utf16(service, blob + end + 4, sizeof(auth->blob) - end - 12);
    if (spn < 0)
        return fail(s, "invalid server name");
    w16(blob + end, 9);
    w16(blob + end + 2, (unsigned)spn);
    end += 4 + (size_t)spn + 8;
    uint8_t *t = auth->converted;
    int plen = smb_utf16(req->password, t, sizeof(auth->converted));
    if (plen < 0)
        return fail(s, "password exceeds input limit");
    smb_md4(t, (size_t)plen, auth->nthash);
    size_t userlen = strlen(req->username);
    if (!userlen || userlen > 255)
        return fail(s, "invalid account name");
    /* MS-NLMP needs invariant uppercase, so limit account names to ASCII until
     * the userspace SDK provides Unicode case mapping. Passwords are UTF-8. */
    for (size_t i = 0; i < userlen; i++) {
        unsigned c = (unsigned char)req->username[i];
        if (c < 32 || c > 126)
            return fail(s, "account name must be printable ASCII");
        w16(t + 2 * i, c >= 'a' && c <= 'z' ? c - 32 : c);
    }
    int dlen = smb_utf16(req->domain, t + 2 * userlen, sizeof(auth->converted) - 2 * userlen);
    if (dlen < 0)
        return fail(s, "invalid domain");
    smb_hmac_md5(auth->nthash, t, 2 * userlen + (size_t)dlen, 0, 0, 0, 0, auth->response_key);
    smb_hmac_md5(auth->response_key, ch + 24, 8, blob, end, 0, 0, auth->proof);
    smb_hmac_md5(auth->response_key, auth->proof, 16, 0, 0, 0, 0, s->key);
    uint8_t *a = auth->auth;
    memcpy(a, ntlm_header, 8);
    w32(a + 8, 3);
    w32(a + 60, flags);
    if (flags & 0x02000000u)
        memcpy(a + 64, first + 32, 8);
    size_t mic_at = (flags & 0x02000000u) ? 72 : 64, pos = mic_at + 16;
    secbuf(a + 12, 24, pos);
    pos += 24; /* timestamped challenge uses a zero LM response */
    secbuf(a + 20, 16 + end, pos);
    memcpy(a + pos, auth->proof, 16);
    memcpy(a + pos + 16, blob, end);
    pos += 16 + end;
    const char *fields[] = {req->domain, req->username, "ZENITH"};
    for (unsigned i = 0; i < 3; i++) {
        int n = smb_utf16(fields[i], a + pos, sizeof(auth->auth) - pos);
        if (n < 0)
            return fail(s, "credentials exceed input limit");
        secbuf(a + 28 + 8 * i, (size_t)n, pos);
        pos += (size_t)n;
    }
    secbuf(a + 52, 0, pos);
    smb_hmac_md5(s->key, first, sizeof(auth->first), ch, clen, a, pos, auth->mic);
    memcpy(a + mic_at, auth->mic, 16);
    if (s->dialect >= 0x300) {
        /* SP800-108 HMAC-SHA256. The protocol's label and context each include NUL. */
        static const uint8_t derivation[] = "\x00\x00\x00\x01"
                                            "SMB2AESCMAC\0\0SmbSign\0"
                                            "\0\0\0\x80";
        uint8_t key[32];
        smb_hmac_sha256(s->key, derivation, sizeof(derivation) - 1, key);
        memcpy(s->signing, key, 16);
        smb_wipe(key, sizeof(key));
    } else
        memcpy(s->signing, s->key, 16);
    if (setup(s, a, pos, 0) || smb_response(s, 9))
        return -1;
    if (r16(s->packet + 66) & 7)
        return fail(s, "server requires guest, anonymous or encrypted session");
    s->authenticated = 1;
    return 0;
}

int smb_auth(struct smb_session *s, const struct smb_request *req)
{
    struct auth_workspace auth = {0};
    int result = authenticate(s, req, &auth);
    smb_wipe(&auth, sizeof(auth));
    return result;
}
