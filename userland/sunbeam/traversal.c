/* Read-only traversal: never follow a final symlink. Natural VFS entry order,
 * at most 1024 nodes, depth 15 and 1024-byte paths. Every exit closes handles.
 */
struct walk_options {
  int kind, summary;
  char type;
  const char *pattern;
  unsigned long visited, dirs, files;
};
static int walk_glob(const char *p, const char *s) {
  const char *star = 0, *retry = 0;
  while (*s) {
    if (*p == '?' || *p == *s) {
      p++;
      s++;
    } else if (*p == '*') {
      star = p++;
      retry = s;
    } else if (star) {
      p = star + 1;
      s = ++retry;
    } else
      return 0;
  }
  while (*p == '*')
    p++;
  return !*p;
}
static int walk_path(const char *path, int depth, struct walk_options *o,
                     unsigned long *total) {
  if (++o->visited > 1024)
    return text_error("traversal exceeds 1024 nodes");
  struct zenith_stat st;
  long result = file_stat(path, &st);
  if (result < 0)
    return file_error(path, result);
  unsigned int type = st.mode & 0170000;
  int directory = type == 0040000;
  *total = type == 0100000 ? st.size : 0;
  if (o->kind == 0) {
    int type_ok = !o->type || (o->type == 'd' && directory) ||
                  (o->type == 'f' && type == 0100000) ||
                  (o->type == 'l' && type == 0120000);
    if (type_ok && (!o->pattern || walk_glob(o->pattern, leaf_name(path)))) {
      print(path);
      print("\n");
    }
  } else if (o->kind == 1) {
    if (!depth) {
      print(path);
      print("\n");
    } else {
      for (int i = 1; i < depth; i++)
        print("|   ");
      print("|-- ");
      print(leaf_name(path));
      print("\n");
    }
    if (depth) {
      if (directory)
        o->dirs++;
      else
        o->files++;
    }
  }
  if (directory) {
    if (depth >= 15)
      return text_error("traversal exceeds depth 15");
    long fd = open_path(path, 1);
    if (fd < 0)
      return file_error(path, fd);
    struct zenith_dirent entry;
    int status = 0;
    while ((result = zenith_call(217, fd, (unsigned long)&entry,
                                 sizeof(entry))) > 0) {
      if (same(entry.name, ".") || same(entry.name, ".."))
        continue;
      char child[1025];
      unsigned long child_size = 0;
      if (join_path(child, path, entry.name) < 0) {
        status = text_error("traversal path exceeds 1024 bytes");
        break;
      }
      status = walk_path(child, depth + 1, o, &child_size);
      if (status)
        break;
      if (*total > ~0UL - child_size) {
        status = text_error("traversal size overflow");
        break;
      }
      *total += child_size;
    }
    zenith_call(3, fd, 0, 0);
    if (result < 0)
      return file_error(path, result);
    if (status)
      return status;
  }
  if (o->kind == 2 && (!o->summary || !depth) && (directory || !depth)) {
    number(*total / 1024 + (*total % 1024 != 0));
    print("\t");
    print(path);
    print("\n");
  }
  return 0;
}
static int native_traversal_command(int argc, char **argv) {
  struct walk_options o = {0};
  const char *paths[32];
  int count = 0;
  if (same(argv[1], "find"))
    o.kind = 0;
  else if (same(argv[1], "tree"))
    o.kind = 1;
  else if (same(argv[1], "du"))
    o.kind = 2;
  else
    return -1;
  int options = 1;
  for (int i = 2; i < argc; i++) {
    if (options && same(argv[i], "--")) {
      options = 0;
      continue;
    }
    if (options && argv[i][0] == '-' && argv[i][1]) {
      if (o.kind == 0 && same(argv[i], "-name") && i + 1 < argc) {
        o.pattern = argv[++i];
        if (length(o.pattern) > 256)
          return text_error("find pattern exceeds 256 bytes");
      } else if (o.kind == 0 && same(argv[i], "-type") && i + 1 < argc) {
        const char *s = argv[++i];
        if (length(s) != 1 || (*s != 'f' && *s != 'd' && *s != 'l'))
          return text_error("find -type f|d|l");
        o.type = *s;
      } else if (o.kind == 2 && (same(argv[i], "-s") || same(argv[i], "-sh") ||
                                 same(argv[i], "-hs")))
        o.summary = 1;
      else if (o.kind == 2 && same(argv[i], "-h")) {
      } /* Existing Zenith profile reports KiB. */
      else
        return text_error("unsupported traversal option");
    } else
      paths[count++] = argv[i];
  }
  if (o.kind == 1 && count > 1)
    return text_error("tree accepts one path");
  if (!count)
    paths[count++] = ".";
  int status = 0;
  for (int i = 0; i < count; i++) {
    unsigned long total;
    char root[1025];
    unsigned long size = length(paths[i]);
    if (!size || size > 1024) {
      status = text_error("invalid traversal path");
      continue;
    }
    for (unsigned long j = 0; j <= size; j++)
      root[j] = paths[i][j];
    while (size > 1 && root[size - 1] == '/')
      root[--size] = 0;
    int next = walk_path(root, 0, &o, &total);
    if (next)
      status = next;
  }
  if (o.kind == 1 && !status) {
    print("\n");
    number(o.dirs);
    print(" directories, ");
    number(o.files);
    print(" files\n");
  }
  return status;
}
