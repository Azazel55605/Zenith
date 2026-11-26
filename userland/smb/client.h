#ifndef ZENITH_SMB_CLIENT_H
#define ZENITH_SMB_CLIENT_H
#include <stdint.h>
#include <stddef.h>

/* One synchronous session per invocation. Short TCP and local reads/writes are supported. */
struct smb_io {
    void *user;
    int (*send)(void *user, const void *data, size_t count);
    int (*recv)(void *user, void *data, size_t count);
    int (*random)(void *user, void *data, size_t count);                      /* 0 on success */
    int (*entry)(void *user, const char *name, int directory, uint64_t size); /* 0 on success */
    int (*download_begin)(void *user, uint64_t size);                  /* 0 on success, before any write */
    int (*download_write)(void *user, const void *data, size_t count); /* bytes written */
    int (*upload_read)(void *user, void *data, size_t count);          /* bytes read or zero at EOF */
};

enum smb_operation { SMB_SHARES, SMB_LIST, SMB_GET, SMB_PUT };
struct smb_request {
    enum smb_operation operation;
    const char *server, *share, *path; /* UTF-8, path relative to share; slash separates components */
    const char *domain, *username, *password;
    uint64_t upload_size;
    int replace;
};
/* Caller owns the connected direct-TCP socket, closes it on all exits, and
 * commits its local staging file only after success. Single invocation per process. */
int smb_valid_component(const char *text, size_t length);
int smb_execute(const struct smb_io *io, const struct smb_request *request);
const char *smb_error(void);
uint32_t smb_ntstatus(void);
void smb_clear(void);
#endif
