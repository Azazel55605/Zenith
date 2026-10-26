/* Bounded byte-oriented Thompson NFA. No libc, backtracking, backreferences or
 * captures. Search is leftmost-longest; work and compiled states are capped. */
#define RX_MAX 256
struct rx_node {
  int kind, a, b;
  unsigned char bits[32];
};
struct rx_program {
  struct rx_node nodes[RX_MAX];
  int count, start, error, depth;
  const unsigned char *parse;
};
static int rx_new(struct rx_program *r, int kind, int a, int b) {
  if (r->count == RX_MAX) {
    r->error = 1;
    return 0;
  }
  int n = r->count++;
  r->nodes[n].kind = kind;
  r->nodes[n].a = a;
  r->nodes[n].b = b;
  for (int i = 0; i < 32; i++)
    r->nodes[n].bits[i] = 0;
  return n;
}
static unsigned char ascii_fold(unsigned char c) {
  return c >= 'A' && c <= 'Z' ? c + 32 : c;
}
static void rx_bit(struct rx_node *n, unsigned char c) {
  n->bits[c / 8] |= (unsigned char)(1U << (c % 8));
}
static int rx_escape(struct rx_program *r, unsigned char *out) {
  unsigned char c = *r->parse++;
  if (!c) {
    r->parse--;
    r->error = 1;
    return 0;
  }
  if (c == 'n')
    c = '\n';
  else if (c == 't')
    c = '\t';
  else if (c == 'r')
    c = '\r';
  else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
           (c >= '0' && c <= '9')) {
    r->error = 1;
    return 0;
  }
  *out = c;
  return 1;
}
static int rx_expression(struct rx_program *r);
static int rx_atom(struct rx_program *r) {
  unsigned char c = *r->parse++;
  int n;
  if (c == '(') {
    if (++r->depth > 24) {
      r->error = 1;
      return 0;
    }
    n = rx_expression(r);
    r->depth--;
    if (*r->parse != ')')
      r->error = 1;
    else
      r->parse++;
    return n;
  }
  if (c == '^' || c == '$')
    return rx_new(r, c, 0, 0);
  if (c == '.')
    return rx_new(r, '.', 0, 0);
  n = rx_new(r, 'c', 0, 0);
  struct rx_node *node = &r->nodes[n];
  if (c == '[') {
    int negate = *r->parse == '^';
    if (negate)
      r->parse++;
    int any = 0;
    while (*r->parse && (*r->parse != ']' || !any)) {
      unsigned char first = *r->parse++;
      if (first == '[' &&
          (*r->parse == ':' || *r->parse == '.' || *r->parse == '=')) {
        r->error = 1;
        break;
      }
      if (first == '\\' && !rx_escape(r, &first))
        break;
      unsigned char last = first;
      if (*r->parse == '-' && r->parse[1] && r->parse[1] != ']') {
        r->parse++;
        last = *r->parse++;
        if (last == '\\' && !rx_escape(r, &last))
          break;
        if (last < first) {
          r->error = 1;
          break;
        }
      }
      for (unsigned int i = first; i <= last; i++)
        rx_bit(node, (unsigned char)i);
      any = 1;
    }
    if (*r->parse != ']' || !any)
      r->error = 1;
    else
      r->parse++;
    node->b = negate;
  } else {
    if (c == '\\') {
      if (!rx_escape(r, &c))
        return n;
    } else if (!c || c == '*' || c == '+' || c == '?' || c == '{' || c == '}' ||
               c == ')') {
      r->error = 1;
      return n;
    }
    rx_bit(node, c);
  }
  return n;
}
static int rx_sequence(struct rx_program *r) {
  int result = -1;
  while (*r->parse && *r->parse != ')' && *r->parse != '|' && !r->error) {
    int n = rx_atom(r);
    unsigned char q = *r->parse;
    if (q == '*' || q == '+' || q == '?') {
      r->parse++;
      n = rx_new(r, q, n, 0);
    }
    result = result < 0 ? n : rx_new(r, '&', result, n);
  }
  return result < 0 ? rx_new(r, 'e', 0, 0) : result;
}
static int rx_expression(struct rx_program *r) {
  int n = rx_sequence(r);
  while (*r->parse == '|' && !r->error) {
    r->parse++;
    int right = rx_sequence(r);
    n = rx_new(r, '|', n, right);
  }
  return n;
}
/* Compile AST into a separate fixed program, backwards from the continuation.
 */
static int rx_emit(struct rx_program *out, struct rx_program *ast, int at,
                   int next) {
  struct rx_node *n = &ast->nodes[at];
  int a, b, s;
  if (out->error)
    return 0;
  if (n->kind == '&') {
    b = rx_emit(out, ast, n->b, next);
    return rx_emit(out, ast, n->a, b);
  }
  if (n->kind == '|') {
    a = rx_emit(out, ast, n->a, next);
    b = rx_emit(out, ast, n->b, next);
    return rx_new(out, 's', a, b);
  }
  if (n->kind == '*' || n->kind == '+') {
    s = rx_new(out, 's', 0, next);
    a = rx_emit(out, ast, n->a, s);
    out->nodes[s].a = a;
    return n->kind == '*' ? s : a;
  }
  if (n->kind == '?') {
    a = rx_emit(out, ast, n->a, next);
    return rx_new(out, 's', a, next);
  }
  s = rx_new(out, n->kind, next, 0);
  for (int i = 0; i < 32; i++)
    out->nodes[s].bits[i] = n->bits[i];
  out->nodes[s].b = n->b;
  return s;
}
static int rx_compile(struct rx_program *out, const char *pattern) {
  struct rx_program ast;
  ast.count = ast.error = ast.depth = 0;
  ast.parse = (const unsigned char *)pattern;
  if (length(pattern) > 256)
    return 0;
  int root = rx_expression(&ast);
  if (ast.error || *ast.parse)
    return 0;
  out->count = 0;
  out->error = 0;
  int end = rx_new(out, 'm', 0, 0);
  out->start = rx_emit(out, &ast, root, end);
  return !out->error;
}
static void rx_add(struct rx_program *r, int *states, int n, int start,
                   unsigned long pos, unsigned long size) {
  if (states[n] >= 0 && states[n] <= start)
    return;
  states[n] = start;
  struct rx_node *node = &r->nodes[n];
  if (node->kind == 's') {
    rx_add(r, states, node->a, start, pos, size);
    rx_add(r, states, node->b, start, pos, size);
  } else if (node->kind == 'e' || (node->kind == '^' && pos == 0) ||
             (node->kind == '$' && pos == size))
    rx_add(r, states, node->a, start, pos, size);
}
static int rx_search(struct rx_program *r, const unsigned char *text,
                     unsigned long size, unsigned long from, int insensitive,
                     unsigned long *begin, unsigned long *end) {
  int current[RX_MAX], next[RX_MAX];
  for (int i = 0; i < r->count; i++)
    current[i] = -1;
  int best = -1;
  unsigned long bestend = 0, work = 0;
  for (unsigned long pos = from; pos <= size; pos++) {
    if (best < 0)
      rx_add(r, current, r->start, (int)pos, pos, size);
    for (int i = 0; i < r->count; i++)
      if (current[i] >= 0 && r->nodes[i].kind == 'm') {
        if (best < 0 || current[i] < best) {
          best = current[i];
          bestend = pos;
        } else if (current[i] == best)
          bestend = pos;
      }
    if (pos == size)
      break;
    for (int i = 0; i < r->count; i++)
      next[i] = -1;
    int live = 0;
    for (int i = 0; i < r->count; i++)
      if (current[i] >= 0 && (best < 0 || current[i] <= best)) {
        if (++work > 4000000)
          return -1;
        struct rx_node *n = &r->nodes[i];
        unsigned char c = text[pos];
        int yes = n->kind == '.';
        if (n->kind == 'c') {
          yes = (n->bits[c / 8] >> (c % 8)) & 1;
          if (insensitive) {
            unsigned char low = ascii_fold(c),
                          up = low >= 'a' && low <= 'z' ? low - 32 : low;
            yes |= ((n->bits[low / 8] >> (low % 8)) |
                    (n->bits[up / 8] >> (up % 8))) &
                   1;
          }
          if (n->b)
            yes = !yes;
        }
        if (yes) {
          rx_add(r, next, n->a, current[i], pos + 1, size);
          live = 1;
        }
      }
    for (int i = 0; i < r->count; i++)
      current[i] = next[i];
    if (best >= 0 && !live)
      break;
  }
  if (best < 0)
    return 0;
  *begin = (unsigned long)best;
  *end = bestend;
  return 1;
}
