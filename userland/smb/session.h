#ifndef ZENITH_SMB_SESSION_H
#define ZENITH_SMB_SESSION_H
#include "client.h"

/* Caller-owned, zero-initialized storage. Allocate statically or on the heap, outside
 * Zenith's 64 KiB native stack. Keep its original address and storage for the
 * lifetime of every issued handle. Reuse it via disconnect/connect without zeroing.
 * The caller serializes requests per session and owns the connected TCP transport
 * and its callbacks until disconnect. The request credentials are used only during connect. */
enum { SMB_MAX_OPEN_READS = 8, SMB_IO_SIZE = 32768, SMB_PACKET = 65536 };

enum smb_handle_type { SMB_HANDLE_FILE, SMB_HANDLE_DIRECTORY };
struct smb_open_slot {
    uint8_t file[16];
    uint64_t number;
    enum smb_handle_type type;
    unsigned pages;
    int at_end;
};

struct smb_session {
    const struct smb_io *io;
    uint8_t transport[4 + SMB_PACKET], *packet;
    uint8_t file[16], guid[16], client_guid[16], key[16], signing[16];
    size_t received;
    uint64_t message, sid;
    uint32_t tid, status, capabilities, max_read, max_write, credits;
    uint16_t dialect, security;
    struct smb_open_slot handles[SMB_MAX_OPEN_READS];
    uint64_t last_handle;
    int authenticated, broken;
    const char *error;
};
struct smb_read_handle {
    const struct smb_session *owner;
    uint64_t number;
};

/* Attach one signed disk share. The server, share, domain, username and password
 * fields of the request are used. Each open returns its original 64-bit file size.
 * Open readers allow writers and renames. Offset reads observe the current file
 * through the open handle and return a byte count (possibly short), zero at
 * the current EOF or -1 on error.
 * Closing, disconnecting or losing the transport invalidates the affected handles. */
int smb_session_connect(struct smb_session *s, const struct smb_io *io, const struct smb_request *req);
int smb_session_open_read(struct smb_session *s, const char *path, struct smb_read_handle *out, uint64_t *size);
int smb_session_read_at(struct smb_session *s, struct smb_read_handle handle, uint64_t offset, void *out,
                        size_t capacity);
int smb_session_close_read(struct smb_session *s, struct smb_read_handle handle);
/* Each next call requests at most one directory page. Returns 1 for a page
 * (possibly containing only dot entries), 0 at end, or -1 on failure. Entry names
 * are valid during the callback. Do not re-enter the session from the callback.
 * After an error close the cursor and reopen to restart enumeration. */
typedef int (*smb_directory_entry)(void *user, const char *name, int directory, uint64_t size);
int smb_session_open_directory(struct smb_session *s, const char *path, struct smb_read_handle *out);
int smb_session_next_directory(struct smb_session *s, struct smb_read_handle cursor, smb_directory_entry entry,
                               void *user);
int smb_session_close_directory(struct smb_session *s, struct smb_read_handle cursor);
int smb_session_disconnect(struct smb_session *s);

#endif
