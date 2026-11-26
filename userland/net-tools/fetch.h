#define FETCH_USAGE                                                            \
  "fetch [-ILs] [-o FILE | -O] [--replace] [--max-filesize BYTES | --kernel] " \
  "URL [FILE]"
#define FETCH_STDOUT (1024UL * 1024)
struct fetch_options {
  const char *url, *file;
  unsigned long limit;
  int head, follow, silent, remote_name, replace, legacy;
};

static int fetch_options(int argc, char **argv, struct fetch_options *o) {
  int options = 1, custom_limit = 0;
  for (int i = 2; i < argc; i++) {
    const char *arg = argv[i];
    if (options && equal(arg, "--")) {
      options = 0;
      continue;
    }
    if (options && arg[0] == '-' && arg[1]) {
      if (equal(arg, "--replace")) {
        o->replace = 1;
        continue;
      }
      if (equal(arg, "--max-filesize") || equal(arg, "--kernel")) {
        if (custom_limit++)
          return failure("choose one fetch byte limit");
        if (equal(arg, "--kernel"))
          o->limit = MAX_HTTP;
        else if (++i >= argc || !decimal(argv[i], MAX_HTTP, &o->limit))
          return failure("invalid fetch byte limit");
        continue;
      }
      if (equal(arg, "--location")) {
        o->follow = 1;
        continue;
      }
      if (equal(arg, "--head")) {
        o->head = 1;
        continue;
      }
      if (equal(arg, "--silent")) {
        o->silent = 1;
        continue;
      }
      if (equal(arg, "--remote-name")) {
        o->remote_name = 1;
        continue;
      }
      if (equal(arg, "--output"))
        arg = "-o";
      for (const char *f = arg + 1; *f; f++) {
        switch (*f) {
        case 'L':
          o->follow = 1;
          break;
        case 'I':
          o->head = 1;
          break;
        case 's':
          o->silent = 1;
          break;
        case 'O':
          o->remote_name = 1;
          break;
        case 'o':
          if (o->file || (!f[1] && ++i >= argc))
            return failure(FETCH_USAGE);
          o->file = f[1] ? f + 1 : argv[i];
          f += len(f) - 1;
          break;
        default:
          return failure("unknown fetch option (see fetch --help)");
        }
      }
    } else if (!o->url)
      o->url = arg;
    else if (!o->file) {
      o->file = arg;
      o->legacy = 1;
    } else
      return failure(FETCH_USAGE);
  }
  if (!o->url || (o->file && (!*o->file || o->remote_name)))
    return failure(FETCH_USAGE);
  if (o->file && equal(o->file, "-"))
    o->file = 0;
  if (!o->file && !o->remote_name && o->limit > FETCH_STDOUT)
    o->limit = FETCH_STDOUT;
  return 0;
}

static int remote_filename(const char *path, char *name) {
  const char *base = path, *end = path;
  while (*end && *end != '?') {
    if (*end++ == '/')
      base = end;
  }
  unsigned n = 0;
  while (base < end)
    name[n++] = *base++;
  name[n] = 0;
  return n && !equal(name, ".") && !equal(name, "..");
}

/* Compose the next reference using the previous authority, directory or path as
   applicable. Each hop is parsed and revalidated before it can acquire another
   network handle. */
static int redirect(const char *base, const struct net_url *url,
                    const char *location, char *next) {
  unsigned scheme = 0;
  while (base[scheme] != ':')
    scheme++;
  unsigned authority = scheme + 3;
  while (base[authority] && base[authority] != '/' && base[authority] != '?' &&
         base[authority] != '#')
    authority++;
  unsigned prefix = 0, path = 0;
  if (location[0] == '/' && location[1] == '/') {
    prefix = scheme + 1;
  } else if (location[0] == '/') {
    prefix = authority;
  } else {
    unsigned i = 0;
    while (location[i] && location[i] != ':' && location[i] != '/' &&
           location[i] != '?' && location[i] != '#')
      i++;
    if (location[i] != ':') {
      prefix = authority;
      if (*location == '#')
        path = len(url->path);
      else {
        while (url->path[path] && url->path[path] != '?')
          path++;
        if (*location != '?')
          while (path && url->path[path - 1] != '/')
            path--;
      }
    }
  }
  unsigned tail = 0;
  while (location[tail] && location[tail] != '#')
    tail++;
  if (prefix + path + tail > 350)
    return failure("redirect URL exceeds 350 bytes");
  for (unsigned i = 0; i < prefix; i++)
    next[i] = base[i];
  for (unsigned i = 0; i < path; i++)
    next[prefix + i] = url->path[i];
  for (unsigned i = 0; i < tail; i++)
    next[prefix + path + i] = location[i];
  next[prefix + path + tail] = 0;
  return 0;
}

/* RFC 3986 dot segments operate only on the path. Keep the query and encoded
 * separators intact. */
static void normalize_path(char *path) {
  unsigned r = 0, w = 0;
  while (path[r] && path[r] != '?') {
    if (path[r] == '/' && path[r + 1] == '.' &&
        (path[r + 2] == '/' || path[r + 2] == '?' || !path[r + 2])) {
      r += 2;
      if (path[r] != '/')
        path[w++] = '/';
    } else if (path[r] == '/' && path[r + 1] == '.' && path[r + 2] == '.' &&
               (path[r + 3] == '/' || path[r + 3] == '?' || !path[r + 3])) {
      r += 3;
      while (w && path[w - 1] != '/')
        w--;
      if (w)
        w--;
      if (path[r] != '/')
        path[w++] = '/';
    } else {
      path[w++] = path[r++];
      while (path[r] && path[r] != '/' && path[r] != '?')
        path[w++] = path[r++];
    }
  }
  while (path[r])
    path[w++] = path[r++];
  path[w] = 0;
}

static int collect_headers(struct response *r, const struct fetch_options *o,
                           unsigned long size, unsigned long *pos) {
  while (*pos < size) {
    unsigned char c = wire[(*pos)++];
    if (!c || c >= 128)
      return failure("invalid HTTP header byte");
    if (r->used >= sizeof(header) - 1)
      return failure("HTTP header exceeds 8 KiB");
    header[r->used++] = (char)c;
    if (r->used >= 4 && header[r->used - 4] == '\r' &&
        header[r->used - 3] == '\n' && header[r->used - 2] == '\r' &&
        c == '\n') {
      if (parse_headers(r, o->head, o->follow))
        return 1;
      if (r->status >= 200)
        return 0;
      if (r->status == 101 || r->chunked || r->has_length || r->interim >= 4)
        return failure("unsupported informational HTTP response");
      unsigned interim = r->interim + 1;
      *r = (struct response){.interim = interim};
    }
  }
  return 0;
}

static int write_body(long sink, const unsigned char *data,
                      unsigned long size) {
  if (sink < 0) {
    output(1, data, size);
    return 0;
  }
  while (size) {
    long n = zenith_call(502, sink, (unsigned long)data, size);
    if (n <= 0)
      return service_error(n);
    data += n;
    size -= (unsigned long)n;
  }
  return 0;
}
static int status_error(unsigned int code) {
  char msg[] = "HTTP status 000";
  msg[12] = '0' + code / 100;
  msg[13] = '0' + code / 10 % 10;
  msg[14] = '0' + code % 10;
  return failure(msg);
}
static int receive(long connection, const struct fetch_options *o,
                   struct response *r, long *sink) {
  unsigned long next_progress = 262144;
  unsigned fd = o->legacy ? 1 : 2;
  for (;;) {
    long n = zenith_call(501, connection, (unsigned long)wire, sizeof(wire));
    if (n < 0)
      return service_error(n);
    if (!n)
      return failure("truncated HTTP response");
    unsigned long pos = 0;
    if (!r->status) {
      if (collect_headers(r, o, (unsigned long)n, &pos))
        return 1;
      if (!r->status)
        continue;
      if (o->follow && redirect_status(r->status)) {
        if (!redirect_location[0])
          return failure("redirect is missing Location");
        return 2;
      }
      if (!o->head && r->status != 200 && r->status != 204)
        return status_error(r->status);
      if (r->status == 204 && !o->head) {
        if (r->chunked || r->expected)
          return failure("invalid 204 response body");
        r->has_length = 1;
      }
      if (!o->head && !r->has_length && !r->chunked)
        return failure("HTTP response needs Content-Length or chunked framing");
      if ((o->head ? r->used : r->expected) > o->limit)
        return failure(o->file ? "HTTP response exceeds byte limit"
                               : "HTTP response exceeds stdout byte limit (use "
                                 "-o FILE for large downloads)");
      if (o->file) {
        int exact = o->head || r->has_length;
        unsigned long bytes = o->head ? r->used : r->expected;
        *sink = control(5, (unsigned)o->replace | (exact ? 2u : 0u), o->file,
                        0, 0, 0, exact ? bytes : o->limit, 0);
        if (*sink < 0)
          return service_error(*sink);
      }
      if (o->head) {
        r->received = r->used;
        return write_body(*sink, (const unsigned char *)head_copy, r->received);
      }
    }
    unsigned long count = (unsigned long)n - pos;
    if (r->chunked) {
      if (chunks(r, wire + pos, count, o->limit, &count))
        return 1;
    } else if (count > r->expected - r->received)
      return failure("HTTP body exceeds declared length");
    if (write_body(*sink, wire + pos, count))
      return 1;
    r->received += count;
    if (o->file && !o->silent && r->received >= next_progress) {
      output(fd, "fetch progress ", 15);
      number_to(fd, r->received);
      if (r->has_length) {
        output(fd, "/", 1);
        number_to(fd, r->expected);
      }
      output(fd, "\n", 1);
      next_progress = r->received + 262144;
    }
    if (r->chunked ? r->phase == CHUNK_END : r->received == r->expected)
      return 0;
  }
}

static int fetch(int argc, char **argv) {
  if (argc == 3 && equal(argv[2], "--help")) {
    print(FETCH_USAGE
          "\n"
          "GET the response body on stdout, or write a file atomically.\n"
          "  -I, --head          Request and print response headers\n"
          "  -L, --location      Follow up to five redirects (no HTTPS "
          "downgrade)\n"
          "  -o, --output FILE   Write to FILE (- for stdout)\n"
          "  -O, --remote-name   Save using the filename in the original URL\n"
          "  -s, --silent        Hide file progress and completion messages\n"
          "  --replace           Permit replacing an existing regular file\n"
          "  --max-filesize N    Limit bytes (default 8 MiB; stdout limit 1 "
          "MiB)\n"
          "  --kernel            Set the file limit to 32 MiB + 4096 bytes\n");
    return 0;
  }
  struct fetch_options o = {.limit = 8UL * 1024 * 1024};
  if (fetch_options(argc, argv, &o))
    return 1;
  struct net_url url;
  if (!parse_url(o.url, &url))
    return failure("invalid bounded HTTP/HTTPS URL");
  char basename[351], next[351];
  if (o.remote_name) {
    if (!remote_filename(url.path, basename))
      return failure("URL has no usable remote filename");
    o.file = basename;
  }
  for (unsigned hops = 0;; hops++) {
    normalize_path(url.path);
    long connection =
        control(4, (unsigned int)url.secure, url.host, 0, 0, 0, url.port, 0);
    if (connection < 0)
      return service_error(connection);
    struct response r = {0};
    redirect_location[0] = 0;
    long sink = -1;
    int status = send_request(connection, &url, o.head);
    if (!status)
      status = receive(connection, &o, &r, &sink);
    if (status == 2) {
      zenith_call(503, connection, 0, 0);
      if (hops >= 5)
        return failure("fetch redirect limit exceeded");
      if (redirect(o.url, &url, redirect_location, next))
        return 1;
      int secure = url.secure;
      if (!parse_url(next, &url))
        return failure("invalid HTTP redirect URL");
      if (secure && !url.secure)
        return failure("HTTPS redirect to HTTP refused");
      o.url = next;
      continue;
    }
    if (sink >= 0) {
      if (!status) {
        long result =
            control(7, 0, 0, 0, 0, 0, (unsigned long)sink, r.received);
        if (result < 0)
          status = service_error(result);
      }
      if (status)
        zenith_call(503, sink, 0, 0);
    }
    zenith_call(503, connection, 0, 0);
    if (!status && o.file && !o.silent) {
      unsigned fd = o.legacy ? 1 : 2;
      output(fd, "fetch complete bytes=", 21);
      number_to(fd, r.received);
      const char *security = url.secure ? " (verified TLS 1.2)\n"
                                        : " (plain HTTP; unauthenticated)\n";
      output(fd, security, len(security));
    }
    return status;
  }
}
