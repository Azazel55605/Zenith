#ifndef ZENITH_NETSURF_FETCH_TRANSPORT_H
#define ZENITH_NETSURF_FETCH_TRANSPORT_H
#include <stddef.h>
#include <stdbool.h>

long zenith_web_connect(const char *host, unsigned port, bool tls);
long zenith_web_send(long socket, const void *data, size_t bytes);
long zenith_web_read(long socket, void *data, size_t capacity);
void zenith_web_close(long socket);
const char *zenith_web_error(long code);
/* Connection attempts this browser made, and requests the kernel refused once its per-browser quota was used
 * (kernel 0.0.81: six attempts per run). The window reports both in Page info and the limit bar. */
unsigned zenith_web_attempts(void);
unsigned zenith_web_refused(void);
#endif
