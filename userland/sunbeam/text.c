/* Native text profile: byte-oriented, bounded memory, no host libc or shell
 * eval. Line filters accept 8 KiB lines; sort/diff have aggregate bounds. */
#include "regex.c"
#define TEXT_LINE 8192
struct text_reader {
  long fd;
  unsigned long at, size;
  unsigned char buffer[4096];
  const char *path;
};
static int text_error(const char *message) {
  error_text("sunbeam: ");
  error_text(message);
  error_text("\n");
  return 2;
}
static int text_open(struct text_reader *r, const char *path) {
  r->fd = same(path, "-") ? 0 : open_path(path, 0);
  r->at = r->size = 0;
  r->path = path;
  return r->fd < 0 ? file_error(path, r->fd) : 0;
}
static void text_close(struct text_reader *r) {
  if (r->fd >= 3)
    zenith_call(3, r->fd, 0, 0);
  r->fd = -1;
}
/* 1=line (without LF), 0=EOF, -1=error. Empty and unterminated lines retained.
 */
static int text_line(struct text_reader *r, unsigned char *line,
                     unsigned long *size) {
  *size = 0;
  for (;;) {
    if (r->at == r->size) {
      long n =
          zenith_call(0, r->fd, (unsigned long)r->buffer, sizeof(r->buffer));
      if (n < 0) {
        file_error(r->path, n);
        return -1;
      }
      if (!n)
        return *size ? 1 : 0;
      r->size = (unsigned long)n;
      r->at = 0;
    }
    unsigned char c = r->buffer[r->at++];
    if (c == '\n') {
      line[*size] = 0;
      return 1;
    }
    if (!c) {
      text_error("NUL byte is unsupported by line filters");
      return -1;
    }
    if (*size == TEXT_LINE) {
      text_error("line exceeds 8 KiB");
      return -1;
    }
    line[(*size)++] = c;
    line[*size] = 0;
  }
}
static void text_write(const void *data, unsigned long size) {
  long n = zenith_write(data, size);
  if (n < 0)
    zenith_exit(n == -32 ? 141 : 1);
}
static void text_print_line(const unsigned char *data, unsigned long size) {
  text_write(data, size);
  print("\n");
}
static unsigned char text_output[4096];
static unsigned long text_used;
static void text_flush(void) {
  if (text_used) {
    text_write(text_output, text_used);
    text_used = 0;
  }
}
static void text_byte(unsigned char c) {
  text_output[text_used++] = c;
  if (text_used == sizeof(text_output))
    text_flush();
}
static void text_bytes(const char *s) {
  while (*s)
    text_byte((unsigned char)*s++);
}
static void text_integer(long value) {
  unsigned long n =
      value < 0 ? 0UL - (unsigned long)value : (unsigned long)value;
  char b[24];
  int count = 0;
  if (value < 0)
    text_byte('-');
  do {
    b[count++] = (char)('0' + n % 10);
    n /= 10;
  } while (n);
  while (count)
    text_byte(b[--count]);
}
static int text_long(const char *s, long *out) {
  int negative = 0;
  if (*s == '-' || *s == '+')
    negative = *s++ == '-';
  if (!*s)
    return 0;
  unsigned long n = 0, limit = negative ? 9223372036854775808UL
                                        : 9223372036854775807UL;
  for (; *s; s++) {
    if (*s < '0' || *s > '9' || n > (limit - (unsigned long)(*s - '0')) / 10)
      return 0;
    n = n * 10 + (unsigned long)(*s - '0');
  }
  *out = negative ? (long)(0UL - n) : (long)n;
  return 1;
}
static __attribute__((noinline)) int text_seq(int argc, char **argv) {
  long first = 1, step = 1, last;
  if (argc < 3 || argc > 5)
    return text_error("seq [FIRST [STEP]] LAST");
  if (!text_long(argv[argc - 1], &last) ||
      (argc > 3 && !text_long(argv[2], &first)) ||
      (argc == 5 && !text_long(argv[3], &step)) || !step)
    return text_error("seq requires signed integers and a nonzero step");
  unsigned long count = 0;
  for (long n = first; step > 0 ? n <= last : n >= last;) {
    if (count++ == 10000) {
      text_flush();
      return text_error("seq exceeds 10000 values");
    }
    text_integer(n);
    text_byte('\n');
    if ((step > 0 && n > 9223372036854775807L - step) ||
        (step < 0 && n < (-9223372036854775807L - 1) - step))
      break;
    n += step;
  }
  text_flush();
  return 0;
}
static int text_escape(const char **p, unsigned char *out) {
  unsigned char c = (unsigned char)*(*p)++;
  if (!c) {
    (*p)--;
    return 0;
  }
  if (c == 'n')
    c = '\n';
  else if (c == 't')
    c = '\t';
  else if (c == 'r')
    c = '\r';
  else if (c != '\\' && c != '&' && c != '-')
    return 0;
  *out = c;
  return 1;
}
static __attribute__((noinline)) int text_printf(int argc, char **argv) {
  if (argc < 3)
    return text_error("printf FORMAT [ARGS...]");
  /* Validate before output; only %s, %d, %i, %% and bounded escapes. */
  int conversions = 0;
  for (const char *p = argv[2]; *p; p++) {
    if (*p == '\\') {
      unsigned char c;
      p++;
      if (!text_escape(&p, &c))
        return text_error("unsupported printf escape");
      p--;
    } else if (*p == '%') {
      p++;
      if (!*p || (*p != '%' && *p != 's' && *p != 'd' && *p != 'i'))
        return text_error("printf supports %s %d %i %%");
      if (*p != '%')
        conversions++;
    }
  }
  int next = 3;
  do {
    for (const char *p = argv[2]; *p; p++) {
      if (*p == '\\') {
        unsigned char c;
        p++;
        text_escape(&p, &c);
        p--;
        text_byte(c);
      } else if (*p == '%') {
        p++;
        if (*p == '%') {
          text_byte('%');
          continue;
        }
        const char *arg = next < argc ? argv[next++] : "";
        if (*p == 's')
          text_bytes(arg);
        else {
          long n = 0;
          if (*arg && !text_long(arg, &n)) {
            text_flush();
            return text_error("invalid printf integer");
          }
          text_integer(n);
        }
      } else
        text_byte((unsigned char)*p);
    }
  } while (conversions && next < argc);
  text_flush();
  return 0;
}
static int text_set(const char *s, unsigned char out[256], int *count) {
  *count = 0;
  while (*s) {
    unsigned char first = (unsigned char)*s++, last;
    if (first == '\\' && !text_escape(&s, &first))
      return 0;
    last = first;
    if (*s == '-' && s[1]) {
      s++;
      last = (unsigned char)*s++;
      if (last == '\\' && !text_escape(&s, &last))
        return 0;
      if (last < first)
        return 0;
    }
    for (unsigned int c = first; c <= last; c++) {
      if (*count == 256)
        return 0;
      out[(*count)++] = (unsigned char)c;
    }
  }
  return 1;
}
static __attribute__((noinline)) int text_tr(int argc, char **argv) {
  int del = argc > 2 && same(argv[2], "-d"), start = del ? 3 : 2;
  unsigned char a[256], b[256];
  int na, nb = 0;
  if (argc != (del ? 4 : 4) || !text_set(argv[start], a, &na) ||
      (!del && (!text_set(argv[start + 1], b, &nb) || !nb)))
    return text_error("tr [-d] SET1 [SET2]; byte ranges and \\n \\t supported");
  unsigned char data[4096];
  long n;
  while ((n = zenith_call(0, 0, (unsigned long)data, sizeof(data))) > 0)
    for (long i = 0; i < n; i++) {
      int at = -1;
      for (int j = 0; j < na; j++)
        if (a[j] == data[i]) {
          at = j;
          break;
        }
      if (at < 0)
        text_byte(data[i]);
      else if (!del)
        text_byte(b[at < nb ? at : nb - 1]);
    }
  text_flush();
  return n < 0 ? file_error("stdin", n) : 0;
}
struct text_range {
  unsigned long first, last;
};
static int text_ranges(const char *s, struct text_range *ranges, int *count) {
  *count = 0;
  do {
    if (*count == 64 || !*s)
      return 0;
    unsigned long a = 0, b = 0;
    int digits = 0;
    while (*s >= '0' && *s <= '9') {
      if (a > 8192)
        return 0;
      a = a * 10 + (unsigned long)(*s++ - '0');
      digits = 1;
    }
    if (*s == '-') {
      s++;
      if (!digits && (*s < '0' || *s > '9'))
        return 0;
      if (!digits)
        a = 1;
      if (*s >= '0' && *s <= '9') {
        do {
          if (b > 8192)
            return 0;
          b = b * 10 + (unsigned long)(*s++ - '0');
        } while (*s >= '0' && *s <= '9');
      } else
        b = 8193;
    } else {
      if (!digits)
        return 0;
      b = a;
    }
    if (!a || b < a || a > 8193 || b > 8193)
      return 0;
    ranges[(*count)++] = (struct text_range){a, b};
    if (!*s)
      return 1;
    if (*s++ != ',')
      return 0;
  } while (1);
}
static int text_selected(struct text_range *r, int count, unsigned long n) {
  for (int i = 0; i < count; i++)
    if (n >= r[i].first && n <= r[i].last)
      return 1;
  return 0;
}
static __attribute__((noinline)) int text_cut(int argc, char **argv) {
  const char *files[32], *list = 0;
  int count = 0, fields = 0, delimiter_set = 0;
  unsigned char delimiter = '\t';
  struct text_range ranges[64];
  int range_count;
  int options = 1;
  for (int i = 2; i < argc; i++) {
    if (options && same(argv[i], "--")) {
      options = 0;
      continue;
    }
    if (options && argv[i][0] == '-' && argv[i][1]) {
      char option = argv[i][1];
      const char *value = argv[i] + 2;
      if (!*value) {
        if (++i == argc)
          return text_error("cut option requires a value");
        value = argv[i];
      }
      if (option == 'd') {
        if (length(value) != 1)
          return text_error("cut delimiter must be one byte");
        delimiter = (unsigned char)*value;
        delimiter_set = 1;
      } else if ((option == 'f' || option == 'c') && !list) {
        list = value;
        fields = option == 'f';
      } else
        return text_error("cut requires one -f or -c list");
    } else
      files[count++] = argv[i];
  }
  if (!list || (!fields && delimiter_set) ||
      !text_ranges(list, ranges, &range_count))
    return text_error("invalid cut selection (1,3-5,-4,6-)");
  if (!count)
    files[count++] = "-";
  int status = 0;
  for (int f = 0; f < count; f++) {
    struct text_reader r;
    if (text_open(&r, files[f])) {
      status = 1;
      continue;
    }
    unsigned char line[TEXT_LINE + 1];
    unsigned long n;
    int got;
    while ((got = text_line(&r, line, &n)) > 0) {
      if (!fields) {
        for (unsigned long i = 0; i < n; i++)
          if (text_selected(ranges, range_count, i + 1))
            text_byte(line[i]);
      } else {
        int contains = 0;
        for (unsigned long i = 0; i < n; i++)
          if (line[i] == delimiter)
            contains = 1;
        if (!contains) {
          for (unsigned long i = 0; i < n; i++)
            text_byte(line[i]);
        } else {
          unsigned long start = 0, field = 1;
          int printed = 0;
          for (unsigned long i = 0; i <= n; i++)
            if (i == n || line[i] == delimiter) {
              if (text_selected(ranges, range_count, field)) {
                if (printed)
                  text_byte(delimiter);
                for (unsigned long j = start; j < i; j++)
                  text_byte(line[j]);
                printed = 1;
              }
              field++;
              start = i + 1;
            }
        }
      }
      text_byte('\n');
    }
    text_close(&r);
    if (got < 0)
      status = 1;
  }
  text_flush();
  return status;
}
static int text_compare(const unsigned char *a, const unsigned char *b) {
  while (*a && *a == *b) {
    a++;
    b++;
  }
  return (int)*a - (int)*b;
}
static __attribute__((noinline)) int text_grep(int argc, char **argv) {
  int invert = 0, numbered = 0, ignore = 0, extended = 0, index = 2,
      options = 1;
  for (; index < argc && options; index++) {
    const char *s = argv[index];
    if (same(s, "--")) {
      index++;
      break;
    }
    if (*s != '-' || !s[1])
      break;
    for (s++; *s; s++) {
      if (*s == 'v')
        invert = 1;
      else if (*s == 'n')
        numbered = 1;
      else if (*s == 'i')
        ignore = 1;
      else if (*s == 'E')
        extended = 1;
      else
        return text_error("grep accepts -invE and --");
    }
  }
  if (index == argc)
    return text_error("grep needs a pattern");
  const char *pattern = argv[index++];
  struct rx_program regex;
  if (extended && !rx_compile(&regex, pattern))
    return text_error("unsupported or invalid bounded regex");
  int found = 0, error = 0, files = argc - index;
  if (!files)
    files = 1;
  for (int f = 0; f < files; f++) {
    const char *path = index < argc ? argv[index + f] : "-";
    struct text_reader r;
    if (text_open(&r, path)) {
      error = 1;
      continue;
    }
    unsigned char line[TEXT_LINE + 1];
    unsigned long n, line_number = 0;
    int got;
    while ((got = text_line(&r, line, &n)) > 0) {
      line_number++;
      int match = 0;
      if (extended) {
        unsigned long begin, end;
        match = rx_search(&regex, line, n, 0, ignore, &begin, &end);
        if (match < 0) {
          text_error("regex work budget exceeded");
          error = 1;
          break;
        }
      } else {
        unsigned long size = length(pattern);
        for (unsigned long i = 0; i + size <= n; i++) {
          unsigned long j = 0;
          for (; j < size; j++)
            if ((ignore ? ascii_fold(line[i + j]) : line[i + j]) !=
                (ignore ? ascii_fold((unsigned char)pattern[j])
                        : (unsigned char)pattern[j]))
              break;
          if (j == size) {
            match = 1;
            break;
          }
        }
      }
      if (match != invert) {
        found = 1;
        if (argc - index > 1) {
          print(path);
          print(":");
        }
        if (numbered) {
          number(line_number);
          print(":");
        }
        text_print_line(line, n);
      }
    }
    text_close(&r);
    if (got < 0)
      error = 1;
  }
  return error ? 2 : found ? 0 : 1;
}
#define TEXT_POOL 65536
#define TEXT_LINES 2048
static unsigned char text_pool[TEXT_POOL];
static unsigned int text_offsets[TEXT_LINES], text_merge[TEXT_LINES];
static __attribute__((noinline)) int text_sort_uniq(int argc, char **argv,
                                                    int sorting) {
  int start = 2, reverse = 0, options = 1, status = 0;
  const char *files[32];
  int nf = 0;
  for (int i = start; i < argc; i++) {
    if (options && same(argv[i], "--")) {
      options = 0;
      continue;
    }
    if (options && sorting && same(argv[i], "-r")) {
      reverse = 1;
      continue;
    }
    if (options && argv[i][0] == '-' && argv[i][1])
      return text_error(sorting ? "sort accepts -r and --"
                                : "uniq accepts file operands and --");
    files[nf++] = argv[i];
  }
  if (!nf)
    files[nf++] = "-";
  unsigned int used = 0, count = 0;
  unsigned char previous[TEXT_LINE + 1];
  int have_previous = 0;
  for (int f = 0; f < nf; f++) {
    struct text_reader r;
    if (text_open(&r, files[f])) {
      status = 1;
      continue;
    }
    unsigned char line[TEXT_LINE + 1];
    unsigned long n;
    int got;
    while ((got = text_line(&r, line, &n)) > 0) {
      if (sorting) {
        if (count == TEXT_LINES || used + n + 1 > TEXT_POOL) {
          text_close(&r);
          return text_error("sort exceeds 64 KiB or 2048 lines");
        }
        text_offsets[count++] = used;
        for (unsigned long i = 0; i <= n; i++)
          text_pool[used++] = line[i];
      } else if (!have_previous || text_compare(line, previous)) {
        text_print_line(line, n);
        for (unsigned long i = 0; i <= n; i++)
          previous[i] = line[i];
        have_previous = 1;
      }
    }
    text_close(&r);
    if (got < 0)
      status = 1;
  }
  if (sorting) {
    if (status)
      return status;
    for (unsigned int width = 1; width < count; width *= 2) {
      for (unsigned int base = 0; base < count; base += 2 * width) {
        unsigned int a = base, b = base + width, end = b + width;
        if (b > count)
          b = count;
        if (end > count)
          end = count;
        unsigned int left_end = b;
        for (unsigned int out = base; out < end; out++) {
          int take = a < left_end &&
                     (b == end || text_compare(text_pool + text_offsets[a],
                                               text_pool + text_offsets[b]) *
                                          (reverse ? -1 : 1) <=
                                      0);
          text_merge[out] = text_offsets[take ? a++ : b++];
        }
      }
      for (unsigned int i = 0; i < count; i++)
        text_offsets[i] = text_merge[i];
    }
    for (unsigned int i = 0; i < count; i++) {
      unsigned char *s = text_pool + text_offsets[i];
      text_print_line(s, length((char *)s));
    }
  }
  return status;
}
#define SED_MAX 8
struct text_sed_command {
  long line;
  int addressed, kind, global;
  struct rx_program address, search;
  char replacement[513];
};
static struct text_sed_command sed_commands[SED_MAX];
static int sed_delimited(const char **cursor, char delimiter, char *out,
                         unsigned long capacity) {
  const char *p = *cursor;
  unsigned long n = 0;
  while (*p && *p != delimiter) {
    if (n + 2 >= capacity)
      return 0;
    if (*p == '\\') {
      out[n++] = *p++;
      if (!*p)
        return 0;
    }
    out[n++] = *p++;
  }
  if (*p != delimiter)
    return 0;
  out[n] = 0;
  *cursor = p + 1;
  return 1;
}
static int sed_compile(const char *script, int *count) {
  *count = 0;
  const char *p = script;
  while (*p) {
    while (*p == ';' || *p == ' ')
      p++;
    if (!*p)
      break;
    if (*count == SED_MAX)
      return 0;
    struct text_sed_command *c = &sed_commands[(*count)++];
    c->line = 0;
    c->addressed = c->global = 0;
    if (*p >= '0' && *p <= '9') {
      while (*p >= '0' && *p <= '9') {
        if (c->line > 1000000)
          return 0;
        c->line = c->line * 10 + (*p++ - '0');
      }
      if (!c->line)
        return 0;
    } else if (*p == '/') {
      char pattern[513];
      p++;
      if (!sed_delimited(&p, '/', pattern, sizeof(pattern)) ||
          !rx_compile(&c->address, pattern))
        return 0;
      c->addressed = 1;
    }
    c->kind = *p;
    if (!*p)
      return 0;
    p++;
    if (c->kind == 's') {
      char pattern[513];
      char delimiter = *p;
      if (!delimiter || delimiter == '\\' || delimiter == '\n')
        return 0;
      p++;
      if (!sed_delimited(&p, delimiter, pattern, sizeof(pattern)) ||
          !rx_compile(&c->search, pattern) ||
          !sed_delimited(&p, delimiter, c->replacement, sizeof(c->replacement)))
        return 0;
      /* Literal escaped delimiters, ampersands and backslashes only. */
      for (const char *r = c->replacement; *r; r++)
        if (*r == '\\') {
          r++;
          if (!*r || (*r != '&' && *r != '\\' && *r != delimiter))
            return 0;
        }
      if (*p == 'g') {
        c->global = 1;
        p++;
      }
    } else if (c->kind != 'd' && c->kind != 'p')
      return 0;
    while (*p == ' ')
      p++;
    if (*p && *p != ';')
      return 0;
  }
  return *count > 0;
}
static int sed_append(unsigned char *out, unsigned long *used,
                      const unsigned char *s, unsigned long count) {
  if (*used + count > TEXT_LINE)
    return 0;
  for (unsigned long i = 0; i < count; i++)
    out[(*used)++] = s[i];
  return 1;
}
static int sed_substitute(struct text_sed_command *c, unsigned char *line,
                          unsigned long *size) {
  unsigned char out[TEXT_LINE + 1];
  unsigned long used = 0, from = 0, position = 0, lastend = 0;
  int prior_nonempty = 0;
  for (;;) {
    unsigned long begin, end;
    int found = rx_search(&c->search, line, *size, position, 0, &begin, &end);
    if (found < 0)
      return 0;
    if (!found)
      break;
    if (begin == end && prior_nonempty && begin == lastend) {
      if (end == *size)
        break;
      position = end + 1;
      prior_nonempty = 0;
      continue;
    }
    if (!sed_append(out, &used, line + from, begin - from))
      return 0;
    for (const char *r = c->replacement; *r; r++) {
      if (*r == '&') {
        if (!sed_append(out, &used, line + begin, end - begin))
          return 0;
      } else {
        unsigned char ch = (unsigned char)*r;
        if (ch == '\\')
          ch = (unsigned char)*++r;
        if (!sed_append(out, &used, &ch, 1))
          return 0;
      }
    }
    from = end;
    lastend = end;
    prior_nonempty = end > begin;
    if (!c->global)
      break;
    if (end == begin) {
      if (end == *size)
        break;
      if (!sed_append(out, &used, line + end, 1))
        return 0;
      from = position = end + 1;
    } else
      position = end;
  }
  if (!sed_append(out, &used, line + from, *size - from))
    return 0;
  out[used] = 0;
  for (unsigned long i = 0; i <= used; i++)
    line[i] = out[i];
  *size = used;
  return 1;
}
static __attribute__((noinline)) int text_sed(int argc, char **argv) {
  int at = 2, quiet = 0, count;
  if (at < argc && same(argv[at], "-n")) {
    quiet = 1;
    at++;
  }
  if (at < argc && same(argv[at], "--"))
    at++;
  if (at == argc || !sed_compile(argv[at++], &count))
    return text_error("invalid/unsupported sed script (8 commands; d, p, "
                      "s/PATTERN/REPLACEMENT/g; no capture replacement)");
  unsigned long lineno = 0;
  int files = argc - at, status = 0;
  if (!files)
    files = 1;
  for (int f = 0; f < files; f++) {
    const char *path = at < argc ? argv[at + f] : "-";
    struct text_reader r;
    if (text_open(&r, path)) {
      status = 1;
      continue;
    }
    unsigned char line[TEXT_LINE + 1];
    unsigned long n;
    int got;
    while ((got = text_line(&r, line, &n)) > 0) {
      lineno++;
      int deleted = 0;
      for (int i = 0; i < count; i++) {
        struct text_sed_command *c = &sed_commands[i];
        if (c->line && (unsigned long)c->line != lineno)
          continue;
        if (c->addressed) {
          unsigned long begin, end;
          int match = rx_search(&c->address, line, n, 0, 0, &begin, &end);
          if (match < 0) {
            status = 2;
            break;
          }
          if (!match)
            continue;
        }
        if (c->kind == 'd') {
          deleted = 1;
          break;
        }
        if (c->kind == 'p')
          text_print_line(line, n);
        else if (!sed_substitute(c, line, &n)) {
          text_error("sed output/work limit exceeded");
          status = 2;
          break;
        }
      }
      if (status == 2)
        break;
      if (!deleted && !quiet)
        text_print_line(line, n);
    }
    text_close(&r);
    if (got < 0)
      status = 2;
    if (status == 2)
      break;
  }
  return status;
}
static unsigned short diff_lcs[257][257];
static unsigned int diff_offsets[2][256];
static void diff_range(unsigned int from, unsigned int to) {
  number(from + 1);
  if (to - from > 1) {
    print(",");
    number(to);
  }
}
static __attribute__((noinline)) int text_diff(int argc, char **argv) {
  if (argc != 4)
    return text_error("diff FILE1 FILE2");
  unsigned int count[2] = {0, 0}, used[2] = {0, 0};
  int terminated[2] = {1, 1};
  for (int f = 0; f < 2; f++) {
    struct text_reader r;
    if (text_open(&r, argv[f + 2]))
      return 2;
    unsigned char line[TEXT_LINE + 1];
    unsigned long n;
    int got;
    while ((got = text_line(&r, line, &n)) > 0) {
      if (count[f] == 256 || used[f] + n + 1 > 32768) {
        text_close(&r);
        return text_error("diff exceeds 32 KiB or 256 lines per file");
      }
      diff_offsets[f][count[f]++] = f * 32768 + used[f];
      for (unsigned long i = 0; i <= n; i++)
        text_pool[f * 32768 + used[f]++] = line[i];
      terminated[f] = r.at > 0 && r.buffer[r.at - 1] == '\n';
    }
    text_close(&r);
    if (got < 0)
      return 2;
  }
  unsigned int a = count[0], b = count[1];
  for (unsigned int i = 0; i <= a; i++)
    diff_lcs[i][b] = 0;
  for (unsigned int j = 0; j <= b; j++)
    diff_lcs[a][j] = 0;
  for (int i = (int)a - 1; i >= 0; i--)
    for (int j = (int)b - 1; j >= 0; j--) {
      int equal = !text_compare(text_pool + diff_offsets[0][i],
                                text_pool + diff_offsets[1][j]);
      if ((unsigned int)i == a - 1 && (unsigned int)j == b - 1 &&
          terminated[0] != terminated[1])
        equal = 0;
      diff_lcs[i][j] = equal ? diff_lcs[i + 1][j + 1] + 1
                       : diff_lcs[i + 1][j] >= diff_lcs[i][j + 1]
                           ? diff_lcs[i + 1][j]
                           : diff_lcs[i][j + 1];
    }
  unsigned int x = 0, y = 0;
  int different = 0;
  while (x < a || y < b) {
    int equal = x < a && y < b &&
                !text_compare(text_pool + diff_offsets[0][x],
                              text_pool + diff_offsets[1][y]) &&
                !(x == a - 1 && y == b - 1 && terminated[0] != terminated[1]);
    if (equal) {
      x++;
      y++;
      continue;
    }
    unsigned int x0 = x, y0 = y;
    do {
      if (y >= b || (x < a && diff_lcs[x + 1][y] >= diff_lcs[x][y + 1]))
        x++;
      else
        y++;
      equal = x < a && y < b &&
              !text_compare(text_pool + diff_offsets[0][x],
                            text_pool + diff_offsets[1][y]) &&
              !(x == a - 1 && y == b - 1 && terminated[0] != terminated[1]);
    } while ((x < a || y < b) && !equal);
    different = 1;
    if (x > x0 && y > y0) {
      diff_range(x0, x);
      print("c");
      diff_range(y0, y);
    } else if (x > x0) {
      diff_range(x0, x);
      print("d");
      number(y0);
    } else {
      number(x0);
      print("a");
      diff_range(y0, y);
    }
    print("\n");
    for (unsigned int i = x0; i < x; i++) {
      print("< ");
      unsigned char *s = text_pool + diff_offsets[0][i];
      text_print_line(s, length((char *)s));
      if (i == a - 1 && !terminated[0])
        print("\\ No newline at end of file\n");
    }
    if (x > x0 && y > y0)
      print("---\n");
    for (unsigned int i = y0; i < y; i++) {
      print("> ");
      unsigned char *s = text_pool + diff_offsets[1][i];
      text_print_line(s, length((char *)s));
      if (i == b - 1 && !terminated[1])
        print("\\ No newline at end of file\n");
    }
  }
  return different;
}
static __attribute__((noinline)) int native_text_command(int argc,
                                                         char **argv) {
  if (same(argv[1], "seq"))
    return text_seq(argc, argv);
  if (same(argv[1], "printf"))
    return text_printf(argc, argv);
  if (same(argv[1], "tr"))
    return text_tr(argc, argv);
  if (same(argv[1], "cut"))
    return text_cut(argc, argv);
  if (same(argv[1], "grep"))
    return text_grep(argc, argv);
  if (same(argv[1], "sort"))
    return text_sort_uniq(argc, argv, 1);
  if (same(argv[1], "uniq"))
    return text_sort_uniq(argc, argv, 0);
  if (same(argv[1], "sed"))
    return text_sed(argc, argv);
  if (same(argv[1], "diff"))
    return text_diff(argc, argv);
  return -1;
}
