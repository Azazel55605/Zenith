/* Connections belong to this invocation. TLS uses the kernel's verified web trust. */
#include "fetch-transport.h"
#include "../sunbeam/zenith.h"
#include <limits.h>
#include <string.h>

struct web_request {
    unsigned operation, flags;
    unsigned long input, input_length, target, target_length, output, capacity, value, extra, reserved;
};
_Static_assert(sizeof(struct web_request) == 80, "native network ABI");

static unsigned attempts;
unsigned zenith_web_attempts(void) { return attempts; }

long zenith_web_connect(const char *host, unsigned port, bool tls)
{
    struct web_request req = {.operation = 11, .flags = tls, .input = (unsigned long)host,
                              .input_length = strlen(host), .value = port};
    if (attempts < UINT_MAX)
        attempts++;
    return zenith_call(500, (unsigned long)&req, sizeof(req), 0);
}

long zenith_web_send(long socket, const void *data, size_t bytes)
{
    return zenith_call(502, socket, (unsigned long)data, bytes);
}

long zenith_web_read(long socket, void *data, size_t capacity)
{
    return zenith_call(501, socket, (unsigned long)data, capacity);
}

void zenith_web_close(long socket)
{
    zenith_call(503, socket, 0, 0);
}

const char *zenith_web_error(long code)
{
    if (code == -28) return "This network transfer reached a resource limit.";
    if (code == -13) return "Network access denied.";
    if (code == -16) return "Network connection busy.";
    static char reason[513];
    struct web_request req = {.operation = 6, .output = (unsigned long)reason, .capacity = sizeof(reason) - 1};
    long n = zenith_call(500, (unsigned long)&req, sizeof(req), 0);
    if (n <= 0 || (size_t)n >= sizeof(reason)) return "Network request failed.";
    reason[n] = 0;
    /* NetSurf inserts its failure reason into a generated HTML text node. */
    if (strpbrk(reason, "<>&")) return "Network request failed.";
    return reason;
}
