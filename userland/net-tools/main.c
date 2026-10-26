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
    long n = zenith_call(1, fd, (unsigned long)data, size);
    if (n <= 0)
      zenith_exit(n == -32 ? 141 : 1);
    data = (const unsigned char *)data + n;
    size -= (unsigned long)n;
  }
}
static void print(const char *s) { output(1, s, len(s)); }
static void number(unsigned long n) {
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
  output(1, b, at);
}
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
    if (*s < '0' || *s > '9' || n > (maximum - (unsigned long)(*s - '0')) / 10)
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
static int parse_url(const char *s, struct net_url *url) {
  unsigned long size = len(s), start;
  if (size > 350)
    return 0;
  if (size >= 7 && s[0] == 'h' && s[1] == 't' && s[2] == 't' && s[3] == 'p' &&
      s[4] == ':' && s[5] == '/' && s[6] == '/') {
    start = 7;
    url->secure = 0;
    url->port = 80;
  } else if (size >= 8 && s[0] == 'h' && s[1] == 't' && s[2] == 't' &&
             s[3] == 'p' && s[4] == 's' && s[5] == ':' && s[6] == '/' &&
             s[7] == '/') {
    start = 8;
    url->secure = 1;
    url->port = 443;
  } else
    return 0;
  for (unsigned long i = 0; i < size; i++)
    if ((unsigned char)s[i] < 33 || (unsigned char)s[i] > 126 || s[i] == '#' ||
        s[i] == '\\')
      return 0;
  unsigned long at = start, n = 0;
  while (s[at] && s[at] != '/' && s[at] != ':') {
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
    while (s[at] && s[at] != '/') {
      if (n == 6)
        return 0;
      port[n++] = s[at++];
    }
    port[n] = 0;
    if (!decimal(port, 65535, &url->port) || !url->port)
      return 0;
  }
  n = 0;
  if (!s[at])
    url->path[n++] = '/';
  else
    while (s[at])
      url->path[n++] = s[at++];
  url->path[n] = 0;
  return 1;
}
static unsigned char wire[65536];
static char header[8193];
struct http_state {
  unsigned long used, expected, received;
  int ready;
};
/* Strict single-response decoder. No redirect/chunk/compression handling. */
static int headers(struct http_state *s, unsigned long maximum) {
  header[s->used] = 0;
  char *line = header, *end = line;
  while (*end && *end != '\r')
    end++;
  if (end[0] != '\r' || end[1] != '\n')
    return failure("invalid HTTP status line");
  *end = 0;
  for (char *p = line; *p; p++)
    if ((unsigned char)*p < 32)
      return failure("invalid HTTP status byte");
  if (!(len(line) >= 13 && line[0] == 'H' && line[1] == 'T' && line[2] == 'T' &&
        line[3] == 'P' && line[4] == '/' && line[5] == '1' && line[6] == '.' &&
        (line[7] == '0' || line[7] == '1') && line[8] == ' ' &&
        line[12] == ' ' && line[9] >= '0' && line[9] <= '9' &&
        line[10] >= '0' && line[10] <= '9' && line[11] >= '0' &&
        line[11] <= '9'))
    return failure("invalid HTTP status line");
  if (line[9] != '2' || line[10] != '0' || line[11] != '0') {
    char status[] = "HTTP status 000; redirects and errors are refused";
    status[12] = line[9];
    status[13] = line[10];
    status[14] = line[11];
    return failure(status);
  }
  int length_seen = 0;
  line = end + 2;
  while (*line) {
    end = line;
    while (*end && *end != '\r')
      end++;
    if (end[0] != '\r' || end[1] != '\n')
      return failure("invalid HTTP header line");
    *end = 0;
    for (char *p = line; *p; p++)
      if ((unsigned char)*p < 32 && *p != '\t')
        return failure("invalid HTTP header byte");
    if (!*line)
      break;
    if (*line == ' ' || *line == '\t')
      return failure("folded HTTP headers are refused");
    char *colon = line;
    while (*colon && *colon != ':')
      colon++;
    if (colon == line || !*colon)
      return failure("invalid HTTP header");
    *colon++ = 0;
    for (char *k = line; *k; k++)
      if (!((*k >= 'A' && *k <= 'Z') || (*k >= 'a' && *k <= 'z') ||
            (*k >= '0' && *k <= '9') || *k == '-'))
        return failure("invalid HTTP header name");
    while (*colon == ' ' || *colon == '\t')
      colon++;
    char *tail = colon + len(colon);
    while (tail > colon && (tail[-1] == ' ' || tail[-1] == '\t'))
      *--tail = 0;
    if (equal_i(line, "Content-Length")) {
      if (length_seen++ || !decimal(colon, maximum, &s->expected))
        return failure("duplicate, invalid or oversized Content-Length");
    }
    if (equal_i(line, "Transfer-Encoding") ||
        (equal_i(line, "Content-Encoding") && !equal_i(colon, "identity")))
      return failure("HTTP transfer/compression encoding unsupported");
    line = end + 2;
  }
  if (!length_seen)
    return failure("HTTP requires Content-Length");
  s->ready = 1;
  return 0;
}
static int decode(struct http_state *s, unsigned char *bytes,
                  unsigned long count, unsigned long maximum,
                  unsigned long *body) {
  unsigned long at = 0;
  while (!s->ready && at < count) {
    unsigned char c = bytes[at++];
    if (!c || c > 127)
      return failure("invalid HTTP header byte");
    if (s->used == 8192)
      return failure("HTTP header exceeds 8 KiB");
    header[s->used++] = (char)c;
    if (s->used >= 4 && header[s->used - 4] == '\r' &&
        header[s->used - 3] == '\n' && header[s->used - 2] == '\r' &&
        header[s->used - 1] == '\n') {
      if (headers(s, maximum))
        return 1;
    }
  }
  *body = at;
  if (s->ready && count - at > s->expected - s->received)
    return failure("HTTP body exceeds declared length");
  return 0;
}
static int send_request(long handle, const struct net_url *url) {
  char request[1024];
  unsigned long n = 0;
  const char *parts[] = {"GET ", url->path, " HTTP/1.0\r\nHost: ", url->host,
                         ":"};
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
static int fetch(int argc, char **argv) {
  int at = 2, replace = 0, kernel = 0;
  while (at < argc && argv[at][0] == '-' && argv[at][1] == '-') {
    if (equal(argv[at], "--replace") && !replace)
      replace = 1;
    else if (equal(argv[at], "--kernel") && !kernel)
      kernel = 1;
    else
      return failure("unknown or repeated fetch option");
    at++;
  }
  if (argc - at != 2)
    return failure("fetch [--replace] [--kernel] URL FILE");
  struct net_url url;
  if (!parse_url(argv[at], &url))
    return failure("invalid bounded HTTP/HTTPS URL");
  long connection =
      control(4, (unsigned int)url.secure, url.host, 0, 0, 0, url.port, 0);
  if (connection < 0)
    return service_error(connection);
  long sink = -1;
  int status = send_request(connection, &url);
  struct http_state state = {0, 0, 0, 0};
  unsigned long maximum = kernel ? MAX_HTTP : 8UL * 1024 * 1024, next = 262144;
  while (!status && (!state.ready || state.received < state.expected)) {
    long n = zenith_call(501, connection, (unsigned long)wire, sizeof(wire));
    if (n < 0) {
      status = service_error(n);
      break;
    }
    if (!n) {
      status = failure("truncated HTTP response");
      break;
    }
    unsigned long body;
    if (decode(&state, wire, (unsigned long)n, maximum, &body)) {
      status = 1;
      break;
    }
    if (state.ready && sink < 0) {
      sink =
          control(5, (unsigned int)replace, argv[at + 1], 0, 0, 0, maximum, 0);
      if (sink < 0) {
        status = service_error(sink);
        break;
      }
    }
    if (state.ready) {
      unsigned long offset = body;
      while (offset < (unsigned long)n) {
        long written = zenith_call(502, sink, (unsigned long)(wire + offset),
                                   (unsigned long)n - offset);
        if (written <= 0) {
          status = service_error(written);
          break;
        }
        offset += (unsigned long)written;
        state.received += (unsigned long)written;
      }
      if (state.received >= next) {
        print("fetch progress ");
        number(state.received);
        print("/");
        number(state.expected);
        print("\n");
        next = state.received + 262144;
      }
    }
  }
  if (!status) {
    long result =
        control(7, 0, 0, 0, 0, 0, (unsigned long)sink, state.expected);
    if (result < 0)
      status = service_error(result);
    else
      sink = -1;
  }
  if (sink >= 0)
    zenith_call(503, sink, 0, 0);
  zenith_call(503, connection, 0, 0);
  if (!status) {
    print("fetch complete bytes=");
    number(state.received);
    print(url.secure ? " (verified TLS 1.2)\n"
                     : " (plain HTTP; unauthenticated)\n");
  }
  return status;
}
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
