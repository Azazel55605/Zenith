#include "../sunbeam/zenith.h"
#ifndef SUNBEAM_VERSION
#define SUNBEAM_VERSION "0.1.0"
#endif
#define MAX_HTTP (32UL * 1024 * 1024 + 4096)
struct net_request {
  unsigned int operation, flags;
  unsigned long input, length, target, target_length, output, capacity, value,
      extra, reserved;
};
_Static_assert(sizeof(struct net_request) == 80, "network request ABI");
static unsigned long len(const char *s) {
  unsigned long n = 0;
  while (s[n])
    n++;
  return n;
}
static int equal(const char *a, const char *b) {
  while (*a && *a == *b) {
    a++;
    b++;
  }
  return *a == *b;
}
static unsigned char lower(unsigned char c) {
  return c >= 'A' && c <= 'Z' ? c + 32 : c;
}
static int equal_i(const char *a, const char *b) {
  while (*a && lower((unsigned char)*a) == lower((unsigned char)*b)) {
    a++;
    b++;
  }
  return *a == *b;
}
static void output(unsigned long fd, const void *data, unsigned long size) {
  while (size) {
    long n = zenith_call(1, fd, (unsigned long)data, size < 4096 ? size : 4096);
    if (n <= 0)
      zenith_exit(n == -32 ? 141 : 1);
    data = (const unsigned char *)data + n;
    size -= (unsigned long)n;
  }
}
static void print(const char *s) { output(1, s, len(s)); }
static void number_to(unsigned long fd, unsigned long n) {
  char b[24];
  unsigned int at = 0;
  do {
    b[at++] = (char)('0' + n % 10);
    n /= 10;
  } while (n);
  for (unsigned int i = 0; i < at / 2; i++) {
    char c = b[i];
    b[i] = b[at - 1 - i];
    b[at - 1 - i] = c;
  }
  output(fd, b, at);
}
static void number(unsigned long n) { number_to(1, n); }
static long control(unsigned int op, unsigned int flags, const char *input,
                    const char *target, void *out, unsigned long cap,
                    unsigned long value, unsigned long extra) {
  struct net_request r = {op,
                          flags,
                          (unsigned long)input,
                          input ? len(input) : 0,
                          (unsigned long)target,
                          target ? len(target) : 0,
                          (unsigned long)out,
                          cap,
                          value,
                          extra,
                          0};
  return zenith_call(500, (unsigned long)&r, sizeof(r), 0);
}
static int failure(const char *message) {
  output(2, "net-tools: ", 11);
  output(2, message, len(message));
  output(2, "\n", 1);
  return 1;
}
static int service_error(long code) {
  char text[513];
  long n = control(6, 0, 0, 0, text, 512, 0, 0);
  if (n > 0 && n <= 512) {
    text[n] = 0;
    return failure(text);
  }
  return failure(code == -13   ? "permission denied"
                 : code == -9  ? "invalid owned network handle"
                 : code == -14 ? "invalid user buffer"
                 : code == -16 ? "network busy"
                 : code == -28 ? "network resource limit exceeded"
                               : "network service failed");
}
static int decimal(const char *s, unsigned long maximum, unsigned long *value) {
  unsigned long n = 0;
  if (!*s)
    return 0;
  for (; *s; s++) {
    if (*s < '0' || *s > '9' || n > maximum / 10 ||
        (n == maximum / 10 && (unsigned long)(*s - '0') > maximum % 10))
      return 0;
    n = n * 10 + (unsigned long)(*s - '0');
  }
  *value = n;
  return 1;
}
struct net_url {
  char host[254], path[351];
  unsigned long port;
  int secure;
};
static int starts_i(const char *value, const char *prefix) {
  while (*prefix)
    if (lower((unsigned char)*value++) != (unsigned char)*prefix++)
      return 0;
  return 1;
}
static int parse_url(const char *s, struct net_url *url) {
  unsigned long size = len(s), start;
  if (size > 350)
    return 0;
  if (starts_i(s, "http://")) {
    start = 7;
    url->secure = 0;
    url->port = 80;
  } else if (starts_i(s, "https://")) {
    start = 8;
    url->secure = 1;
    url->port = 443;
  } else
    return 0;
  for (unsigned long i = 0; s[i]; i++) {
    unsigned char c = (unsigned char)s[i];
    if (c < 33 || c > 126 || c == '\\')
      return 0;
    if (c == '#' && i < size)
      size = i;
  }
  unsigned long at = start, n = 0;
  while (at < size && s[at] != '/' && s[at] != '?' && s[at] != ':') {
    unsigned char c = (unsigned char)s[at++];
    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
          (c >= '0' && c <= '9') || c == '.' || c == '-') ||
        n == 253)
      return 0;
    url->host[n++] = (char)c;
  }
  if (!n)
    return 0;
  url->host[n] = 0;
  if (s[at] == ':') {
    char port[7];
    n = 0;
    at++;
    while (at < size && s[at] != '/' && s[at] != '?') {
      if (n == 6)
        return 0;
      port[n++] = s[at++];
    }
    port[n] = 0;
    if (!decimal(port, 65535, &url->port) || !url->port)
      return 0;
  }
  n = 0;
  if (s[at] != '/')
    url->path[n++] = '/';
  while (at < size)
    url->path[n++] = s[at++];
  url->path[n] = 0;
  return 1;
}
static unsigned char wire[65536];
#include "http_response.h"
static int send_request(long handle, const struct net_url *url, int head) {
  char request[1024];
  unsigned long n = 0;
  const char *parts[] = {head ? "HEAD " : "GET ", url->path,
                         " HTTP/1.1\r\nHost: ", url->host, ":"};
  for (unsigned int i = 0; i < 5; i++)
    for (const char *p = parts[i]; *p; p++)
      request[n++] = *p;
  char port[8];
  unsigned int k = 0;
  unsigned long value = url->port;
  do {
    port[k++] = (char)('0' + value % 10);
    value /= 10;
  } while (value);
  while (k)
    request[n++] = port[--k];
  const char *suffix =
      "\r\nConnection: close\r\nAccept-Encoding: identity\r\n\r\n";
  while (*suffix)
    request[n++] = *suffix++;
  unsigned long offset = 0;
  while (offset < n) {
    long sent =
        zenith_call(502, handle, (unsigned long)(request + offset), n - offset);
    if (sent <= 0)
      return service_error(sent);
    offset += (unsigned long)sent;
  }
  return 0;
}
#include "fetch.h"
int main(int argc, char **argv, char **envp) {
  (void)envp;
  if (argc > 32)
    return 2;
  if (argc < 2) {
    print("Zenith network tools " SUNBEAM_VERSION "\n");
    return 0;
  }
  if (equal(argv[1], "--abi-bounds")) {
    struct net_request r = {1, 0, 0, 0, 0, 0, (unsigned long)wire, 32, 0, 0, 0};
    if (zenith_call(500, (unsigned long)&r, 79, 0) != -22)
      return 1;
    r.output = 0xffff800000000000UL;
    if (zenith_call(500, (unsigned long)&r, 80, 0) != -14)
      return 1;
    r.output = (unsigned long)&main;
    if (zenith_call(500, (unsigned long)&r, 80, 0) != -14)
      return 1;
    r.output = (unsigned long)wire;
    r.flags = 2;
    if (zenith_call(500, (unsigned long)&r, 80, 0) != -22)
      return 1;
    r.flags = 0;
    r.reserved = 1;
    if (zenith_call(500, (unsigned long)&r, 80, 0) != -22)
      return 1;
    r = (struct net_request){4, 0, 0xffff800000000000UL, 8, 0, 0, 0, 0, 80,
                             0, 0};
    if (zenith_call(500, (unsigned long)&r, 80, 0) != -14)
      return 1;
    return zenith_call(501, 10000, (unsigned long)wire, 1) == -9 &&
                   zenith_call(502, 10000, (unsigned long)wire, 1) == -9 &&
                   zenith_call(501, 10000, 0xffff800000000000UL, 1) == -14 &&
                   zenith_call(501, 10000, (unsigned long)wire, 65537) == -22 &&
                   zenith_call(503, 10000, 0, 0) == -9
               ? 0
               : 1;
  }
  if (equal(argv[1], "--abandon-sink") && argc == 3) {
    long h = control(5, 1, argv[2], 0, 0, 0, 16, 0);
    if (h < 0)
      return service_error(h);
    if (zenith_call(502, h, (unsigned long)"BAD", 3) != 3)
      return 1;
    *(volatile unsigned long *)0xffff800000000000UL = 1;
    return 1;
  }
  if ((equal(argv[1], "--fault-connected") ||
       equal(argv[1], "--cancel-connected") ||
       equal(argv[1], "--connect-bounds")) &&
      argc == 4) {
    unsigned long port;
    if (!decimal(argv[3], 65535, &port) || !port)
      return 1;
    long h = control(4, 0, argv[2], 0, 0, 0, port, 0);
    if (h < 0)
      return service_error(h);
    if (equal(argv[1], "--fault-connected")) {
      *(volatile unsigned long *)0xffff800000000000UL = 1;
      return 1;
    }
    if (equal(argv[1], "--cancel-connected")) {
      print("NETWORK_READ_READY\n");
      return zenith_call(501, h, (unsigned long)wire, 1) < 0 ? 130 : 1;
    }
    if (control(4, 0, argv[2], 0, 0, 0, port, 0) != -16)
      return 1;
    if (zenith_call(503, h, 0, 0) != 0)
      return 1;
    return zenith_call(501, h, (unsigned long)wire, 1) == -9 ? 0 : 1;
  }
  if (equal(argv[1], "--version")) {
    print("net-tools " SUNBEAM_VERSION "\n");
    return 0;
  }
  if (equal(argv[1], "ip") || equal(argv[1], "ifconfig")) {
    if (argc > 3 ||
        (argc == 3 && (!equal(argv[1], "ip") ||
                       (!equal(argv[2], "addr") && !equal(argv[2], "route")))))
      return failure("ip [addr|route] | ifconfig");
    char status[4096];
    long n = control(1, 0, 0, 0, status, sizeof(status), 0, 0);
    if (n < 0)
      return service_error(n);
    output(1, status, (unsigned long)n);
    return 0;
  }
  if (equal(argv[1], "nslookup")) {
    unsigned long port = 53;
    if (argc < 3 || argc > 5 ||
        (argc == 5 && (!decimal(argv[4], 65535, &port) || !port)))
      return failure("nslookup HOST [SERVER [PORT]]");
    char address[16];
    long n = control(2, 0, argv[2], argc > 3 ? argv[3] : 0, address,
                     sizeof(address), port, 0);
    if (n < 0)
      return service_error(n);
    print(argv[2]);
    print(" address=");
    output(1, address, (unsigned long)n);
    print("\n");
    return 0;
  }
  if (equal(argv[1], "ping")) {
    if (argc != 3)
      return failure("ping HOST");
    long elapsed = control(3, 0, argv[2], 0, 0, 0, 0, 0);
    if (elapsed < 0)
      return service_error(elapsed);
    print("reply from ");
    print(argv[2]);
    print(" time=");
    number((unsigned long)elapsed);
    print("ms\n");
    return 0;
  }
  if (equal(argv[1], "nc")) {
    unsigned long port;
    if (argc != 5 || !equal(argv[2], "-z") || !decimal(argv[4], 65535, &port) ||
        !port)
      return failure("nc -z HOST PORT");
    long handle = control(4, 0, argv[3], 0, 0, 0, port, 0);
    if (handle < 0)
      return service_error(handle);
    zenith_call(503, handle, 0, 0);
    print("TCP connected ");
    print(argv[3]);
    print(":");
    number(port);
    print("\n");
    return 0;
  }
  if (equal(argv[1], "fetch"))
    return fetch(argc, argv);
  return failure("commands: ip ifconfig nslookup ping nc fetch");
}
