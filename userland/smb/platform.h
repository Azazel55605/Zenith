#ifndef ZENITH_SMB_PLATFORM_H
#define ZENITH_SMB_PLATFORM_H
#include <stddef.h>
#include <stdint.h>

#define SMB_FILE_LIMIT (32u * 1024u * 1024u)
#define SMB_CREDENTIAL_LIMIT 4096

int platform_output(int fd, const void *data, size_t length);
int platform_open_socket(const char *server, unsigned port);
int platform_send(void *unused, const void *data, size_t length);
int platform_recv(void *unused, void *data, size_t length);
int platform_random(void *unused, void *data, size_t count);
int platform_open_source(const char *path, uint64_t *size);
int platform_read_source(void *unused, void *data, size_t length);
int platform_begin_sink(const char *path, uint64_t size, int replace);
int platform_write_sink(void *unused, const void *data, size_t length);
int platform_commit_sink(void);
void platform_close(void);
int platform_open_private(const char *path, char *out, size_t capacity);
int platform_password_mode(int from_stdin);
int platform_password_read(unsigned char *buffer, size_t capacity);
void platform_password_restore(void);
#endif
