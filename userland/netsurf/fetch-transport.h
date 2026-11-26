#ifndef ZENITH_NETSURF_FETCH_TRANSPORT_H
#define ZENITH_NETSURF_FETCH_TRANSPORT_H
#include <stddef.h>
#include <stdbool.h>

long zenith_web_connect(const char *host, unsigned port, bool tls);
long zenith_web_send(long socket, const void *data, size_t bytes);
long zenith_web_read(long socket, void *data, size_t capacity);
void zenith_web_close(long socket);
const char *zenith_web_error(long code);
/* Count connection attempts over the lifetime of the window for Page info. */
unsigned zenith_web_attempts(void);
#endif
