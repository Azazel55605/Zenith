/* HTTP/1 response framing for fetch. Parsing is incremental across transport
 * reads. */
enum chunk_phase {
  CHUNK_SIZE,
  CHUNK_DATA,
  CHUNK_CR,
  CHUNK_LF,
  CHUNK_TRAILER,
  CHUNK_END
};
struct response {
  unsigned long expected, received, remaining, trailer_bytes;
  unsigned int used, line_used, status, interim;
  int chunked, has_length, line_cr;
  enum chunk_phase phase;
};
static char header[8193], head_copy[8193], chunk_line[1024],
    redirect_location[351];

static int token_char(unsigned char c) {
  if (c >= '0' && c <= '9')
    return 1;
  if (lower(c) >= 'a' && lower(c) <= 'z')
    return 1;
  for (const char *p = "!#$%&'*+-.^_`|~"; *p; p++)
    if (*p == c)
      return 1;
  return 0;
}
static char *field_value(char *line) {
  char *p = line;
  while (token_char((unsigned char)*p))
    p++;
  if (p == line || *p != ':')
    return 0;
  *p++ = 0;
  while (*p == ' ' || *p == '\t')
    p++;
  char *end = p;
  while (*end) {
    unsigned char c = (unsigned char)*end++;
    if ((c < 32 && c != '\t') || c == 127)
      return 0;
  }
  while (end > p && (end[-1] == ' ' || end[-1] == '\t'))
    *--end = 0;
  return p;
}
static char *cut_line(char *s) {
  while (*s && *s != '\r')
    s++;
  if (!*s || s[1] != '\n')
    return 0;
  *s = 0;
  return s + 2;
}
static int redirect_status(unsigned code) {
  return code == 301 || code == 302 || code == 303 || code == 307 ||
         code == 308;
}
static int parse_headers(struct response *r, int head, int follow) {
  header[r->used] = 0;
  if (head)
    for (unsigned i = 0; i < r->used; i++)
      head_copy[i] = header[i];
  char *line = cut_line(header);
  if (!line || len(header) < 12 || header[0] != 'H' || header[1] != 'T' ||
      header[2] != 'T' || header[3] != 'P' || header[4] != '/' ||
      header[5] != '1' || header[6] != '.' ||
      (header[7] != '0' && header[7] != '1') || header[8] != ' ' ||
      header[9] < '1' || header[9] > '5' || header[10] < '0' ||
      header[10] > '9' || header[11] < '0' || header[11] > '9' ||
      (header[12] && header[12] != ' '))
    return failure("invalid HTTP status line");
  for (char *p = header; *p; p++)
    if ((unsigned char)*p < 32 || (unsigned char)*p == 127)
      return failure("invalid HTTP status byte");
  r->status =
      (header[9] - '0') * 100 + (header[10] - '0') * 10 + header[11] - '0';
  while (*line) {
    char *next = cut_line(line);
    if (!next)
      return failure("invalid HTTP header line");
    if (!*line)
      break;
    char *v = field_value(line);
    if (!v)
      return failure("invalid HTTP header field");
    if (equal_i(line, "Content-Length")) {
      if (r->has_length || !decimal(v, ~0UL, &r->expected))
        return failure("duplicate or invalid Content-Length");
      r->has_length = 1;
    } else if (equal_i(line, "Transfer-Encoding")) {
      if (r->chunked || !equal_i(v, "chunked"))
        return failure("unsupported Transfer-Encoding");
      r->chunked = 1;
    } else if (equal_i(line, "Content-Encoding") && !head && r->status == 200 &&
               !equal_i(v, "identity")) {
      return failure("compressed HTTP bodies are unsupported");
    } else if (follow && redirect_status(r->status) &&
               equal_i(line, "Location")) {
      unsigned long n = len(v);
      if (!n || n > 350 || redirect_location[0])
        return failure("invalid or duplicate redirect Location");
      for (unsigned long i = 0; i <= n; i++)
        redirect_location[i] = v[i];
    }
    line = next;
  }
  if (r->has_length && r->chunked)
    return failure("ambiguous response length");
  return 0;
}

static int hex_digit(unsigned char c) {
  if (c >= '0' && c <= '9')
    return c - '0';
  c = lower(c);
  return c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1;
}
static int chunk_size(struct response *r, unsigned long left) {
  const char *p = chunk_line;
  unsigned long size = 0;
  if (hex_digit(*p) < 0)
    return failure("invalid HTTP chunk size");
  while (hex_digit(*p) >= 0) {
    unsigned int digit = (unsigned int)hex_digit(*p++);
    if (size > left / 16 || (size == left / 16 && digit > left % 16))
      return failure("HTTP body exceeds byte limit");
    size = size * 16 + digit;
  }
  /* Extensions are optional metadata. Accept token or quoted values without
   * interpreting them. */
  while (*p) {
    while (*p == ' ' || *p == '\t')
      p++;
    if (*p++ != ';')
      return failure("invalid HTTP chunk extension");
    while (*p == ' ' || *p == '\t')
      p++;
    if (!token_char(*p))
      return failure("invalid HTTP chunk extension");
    while (token_char(*p))
      p++;
    while (*p == ' ' || *p == '\t')
      p++;
    if (*p == '=') {
      p++;
      while (*p == ' ' || *p == '\t')
        p++;
      if (*p == '"') {
        p++;
        while (*p && *p != '"') {
          if (*p == '\\' && !*++p)
            return failure("invalid quoted chunk extension");
          p++;
        }
        if (*p++ != '"')
          return failure("invalid quoted chunk extension");
      } else {
        if (!token_char(*p))
          return failure("invalid HTTP chunk extension");
        while (token_char(*p))
          p++;
      }
    }
  }
  r->remaining = size;
  r->phase = size ? CHUNK_DATA : CHUNK_TRAILER;
  return 0;
}

/* Remove chunk framing in place and return contiguous payload bytes from this
 * read. */
static int chunks(struct response *r, unsigned char *bytes, unsigned long count,
                  unsigned long limit, unsigned long *body_count) {
  unsigned long pos = 0, out = 0;
  while (pos < count) {
    if (r->phase == CHUNK_DATA) {
      unsigned long n = count - pos;
      if (n > r->remaining)
        n = r->remaining;
      if (out != pos)
        for (unsigned long i = 0; i < n; i++)
          bytes[out + i] = bytes[pos + i];
      out += n;
      pos += n;
      r->remaining -= n;
      if (!r->remaining)
        r->phase = CHUNK_CR;
      continue;
    }
    unsigned char c = bytes[pos++];
    switch (r->phase) {
    case CHUNK_END:
      return failure("data after final HTTP chunk");
    case CHUNK_CR:
      if (c != '\r')
        return failure("missing HTTP chunk terminator");
      r->phase = CHUNK_LF;
      break;
    case CHUNK_LF:
      if (c != '\n')
        return failure("missing HTTP chunk terminator");
      r->phase = CHUNK_SIZE;
      break;
    default:
      if (r->phase == CHUNK_TRAILER && ++r->trailer_bytes > 8192)
        return failure("HTTP trailers exceed 8 KiB");
      if (r->line_cr) {
        if (c != '\n')
          return failure("invalid chunk line ending");
        chunk_line[r->line_used] = 0;
        if (r->phase == CHUNK_SIZE) {
          if (chunk_size(r, limit - r->received - out))
            return 1;
        } else if (!r->line_used) {
          r->phase = CHUNK_END;
        } else if (!field_value(chunk_line) ||
                   equal_i(chunk_line, "Content-Length") ||
                   equal_i(chunk_line, "Transfer-Encoding")) {
          return failure("invalid HTTP trailer");
        }
        r->line_used = 0;
        r->line_cr = 0;
      } else if (c == '\r') {
        r->line_cr = 1;
      } else {
        if ((c < 32 && c != '\t') || c >= 127 ||
            r->line_used >= sizeof(chunk_line) - 1)
          return failure("invalid or oversized HTTP chunk line");
        chunk_line[r->line_used++] = (char)c;
      }
    }
  }
  *body_count = out;
  return 0;
}
