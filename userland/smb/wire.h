#ifndef ZENITH_SMB_WIRE_H
#define ZENITH_SMB_WIRE_H
#include "client.h"
#include "crypto.h"
#include <string.h>

enum { SMB_IO_SIZE = 32768, SMB_PACKET = 65536, SMB_PATH = 1024 };
enum {
    SMB_NEGOTIATE,
    SMB_SESSION_SETUP,
    SMB_LOGOFF,
    SMB_TREE_CONNECT,
    SMB_TREE_DISCONNECT,
    SMB_CREATE,
    SMB_CLOSE,
    SMB_FLUSH,
    SMB_READ,
    SMB_WRITE,
    SMB_LOCK,
    SMB_IOCTL,
    SMB_CANCEL,
    SMB_ECHO,
    SMB_QUERY_DIR,
    SMB_CHANGE_NOTIFY,
    SMB_QUERY_INFO,
    SMB_SET_INFO
};
#define NT_MORE_PROCESSING 0xc0000016u
#define NT_NO_MORE_FILES 0x80000006u

struct smb_session {
    const struct smb_io *io;
    const struct smb_request *req;
    uint8_t transport[4 + SMB_PACKET], *packet;
    uint8_t file[16], guid[16], client_guid[16], key[16], signing[16];
    size_t received;
    uint64_t message, sid;
    uint32_t tid, status, capabilities, max_read, max_write, credits;
    uint16_t dialect, security;
    int authenticated, broken;
    const char *error;
};
static inline uint16_t r16(const uint8_t *p)
{
    return p[0] | (uint16_t)p[1] << 8;
}
static inline uint32_t r32(const uint8_t *p)
{
    return r16(p) | (uint32_t)r16(p + 2) << 16;
}
static inline uint64_t r64(const uint8_t *p)
{
    return r32(p) | (uint64_t)r32(p + 4) << 32;
}
static inline void w16(uint8_t *p, unsigned v)
{
    p[0] = (uint8_t)v;
    p[1] = (uint8_t)(v >> 8);
}
static inline void w32(uint8_t *p, uint32_t v)
{
    w16(p, v);
    w16(p + 2, v >> 16);
}
static inline void w64(uint8_t *p, uint64_t v)
{
    w32(p, (uint32_t)v);
    w32(p + 4, (uint32_t)(v >> 32));
}
static inline int inside(size_t total, size_t offset, size_t n)
{
    return offset <= total && n <= total - offset;
}
static inline int fail(struct smb_session *s, const char *why)
{
    s->error = why;
    return -1;
}
int smb_utf16(const char *text, uint8_t *out, size_t cap);
int smb_utf8(const uint8_t *text, size_t n, char *out, size_t cap);
void smb_request_start(struct smb_session *s, uint16_t command, uint16_t size);
int smb_exchange(struct smb_session *s, size_t length);
int smb_response(struct smb_session *s, unsigned size);
int smb_auth(struct smb_session *s);
int smb_shares(struct smb_session *s);
int smb_open(struct smb_session *s, const char *path, uint32_t access, uint32_t sharing, uint32_t disposition,
             uint32_t options, uint64_t *size);
int smb_ioctl(struct smb_session *s, uint32_t code, const uint8_t *data, size_t n, size_t max_out,
              const uint8_t **reply, size_t *reply_len);
#endif
