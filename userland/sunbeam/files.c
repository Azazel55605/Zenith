/* Bounded namespace/file algorithms run in the signed user executable. VFS
 * policy stays in the broker; recursive traversal has explicit depth/request
 * limits. */
static long path_call(unsigned int op, const char *path, const char *target,
                      unsigned long value, unsigned long extra) {
  return zenith_path(op, path, length(path), target,
                     target ? length(target) : 0, value, extra);
}
static int file_stat(const char *path, struct zenith_stat *st) {
  return (int)zenith_call(408, (unsigned long)path, length(path),
                          (unsigned long)st);
}
static int join_path(char *out, const char *directory, const char *name) {
  unsigned long a = length(directory), b = length(name);
  int slash = a && directory[a - 1] != '/';
  if (a + b + (unsigned long)slash > 1024)
    return -36;
  for (unsigned long i = 0; i < a; i++)
    out[i] = directory[i];
  if (slash)
    out[a++] = '/';
  for (unsigned long i = 0; i <= b; i++)
    out[a + i] = name[i];
  return 0;
}
static const char *leaf_name(const char *path) {
  const char *name = path;
  for (unsigned long i = 0; path[i]; i++)
    if (path[i] == '/' && path[i + 1])
      name = path + i + 1;
  return name;
}
static int mkdir_path(const char *path, int parents) {
  if (!parents) {
    long n = path_call(1, path, 0, 0755, 0);
    return n < 0 ? file_error(path, n) : 0;
  }
  unsigned long size = length(path);
  if (!size || size > 1024)
    return file_error(path, -36);
  char copy[1025];
  for (unsigned long i = 0; i <= size; i++)
    copy[i] = path[i];
  for (unsigned long i = 1; i <= size; i++)
    if (!copy[i] || copy[i] == '/') {
      char old = copy[i];
      copy[i] = 0;
      if (!copy[i - 1]) {
        copy[i] = old;
        continue;
      }
      long n = path_call(1, copy, 0, 0755, 0);
      if (n < 0) {
        struct zenith_stat st;
        if (file_stat(copy, &st) < 0 || (st.mode & 0170000) != 0040000)
          return file_error(copy, n);
      }
      copy[i] = old;
    }
  return 0;
}
static int remove_path(const char *path, int recursive, int force, int depth) {
  struct zenith_stat st;
  long result = file_stat(path, &st);
  if (result < 0)
    return force && result == -2 ? 0 : file_error(path, result);
  if ((st.mode & 0170000) != 0040000) {
    result = path_call(2, path, 0, 0, 0);
    return result < 0 ? file_error(path, result) : 0;
  }
  if (!recursive)
    return file_error(path, -21);
  result = path_call(9, path, 0, 0, 0);
  if (result < 0)
    return file_error(path, result);
  if (depth >= 16)
    return file_error("directory nesting exceeds 16", -36);
  struct zenith_dirent entry;
  long fd = open_path(path, 1);
  if (fd < 0)
    return file_error(path, fd);
  int status = 0;
  while ((result = zenith_call(217, fd, (unsigned long)&entry, sizeof(entry))) >
         0) {
    if (same(entry.name, ".") || same(entry.name, ".."))
      continue;
    char child[1025];
    if (join_path(child, path, entry.name) < 0) {
      status = 1;
      break;
    }
    /* Never recurse through a child symlink, even when it points to a
     * directory. */
    if ((entry.stat.mode & 0170000) == 0120000) {
      long n = path_call(2, child, 0, 0, 0);
      if (n < 0)
        status |= file_error(child, n);
    } else
      status |= remove_path(child, 1, force, depth + 1);
  }
  zenith_call(3, fd, 0, 0);
  if (result < 0)
    status |= file_error(path, result);
  if (!status) {
    result = path_call(3, path, 0, 0, 0);
    if (result < 0)
      status = file_error(path, result);
  }
  return status;
}
static int copy_path(const char *source, const char *target, int recursive,
                     int depth) {
  if (same(source, target))
    return file_error("source and destination are the same", -22);
  struct zenith_stat st;
  long n = file_stat(source, &st);
  if (n < 0)
    return file_error(source, n);
  if ((st.mode & 0170000) == 0040000) {
    if (!recursive)
      return file_error(source, -21);
    if (depth >= 16)
      return file_error("directory nesting exceeds 16", -36);
    n = path_call(8, source, target, 0, 0);
    if (n < 0)
      return file_error(target, n);
    if (mkdir_path(target, 1))
      return 1;
    struct zenith_dirent entry;
    long fd = open_path(source, 1);
    if (fd < 0)
      return file_error(source, fd);
    int status = 0;
    while ((n = zenith_call(217, fd, (unsigned long)&entry, sizeof(entry))) >
           0) {
      if (same(entry.name, ".") || same(entry.name, ".."))
        continue;
      if ((entry.stat.mode & 0170000) == 0120000) {
        status |= file_error(
            "recursive symlink copies require explicit support", -22);
        continue;
      }
      char from[1025], to[1025];
      if (join_path(from, source, entry.name) < 0 ||
          join_path(to, target, entry.name) < 0) {
        status = 1;
        break;
      }
      status |= copy_path(from, to, 1, depth + 1);
    }
    zenith_call(3, fd, 0, 0);
    return n < 0 ? file_error(source, n) : status;
  }
  if ((st.mode & 0170000) != 0100000)
    return file_error(source, -22);
  if (st.size > 1048576)
    return file_error("copy exceeds bounded transfer profile", -28);
  long from = open_path(source, 0);
  if (from < 0)
    return file_error(source, from);
  long to = zenith_call(407, from, (unsigned long)target, length(target));
  if (to < 0) {
    zenith_call(3, from, 0, 0);
    return file_error(target, to);
  }
  char bytes[4096];
  int status = 0;
  while ((n = zenith_call(0, from, (unsigned long)bytes, sizeof(bytes))) > 0) {
    long offset = 0;
    while (offset < n) {
      long written =
          zenith_call(1, to, (unsigned long)(bytes + offset), n - offset);
      if (written <= 0) {
        status = file_error(target, written ? written : -5);
        break;
      }
      offset += written;
    }
    if (status)
      break;
  }
  if (n < 0)
    status |= file_error(source, n);
  long flushed = zenith_call(74, to, 0, 0), closed = zenith_call(3, to, 0, 0);
  zenith_call(3, from, 0, 0);
  if (flushed < 0 || closed < 0)
    status |= file_error(target, flushed < 0 ? flushed : closed);
  return status;
}
static int native_files_command(int argc, char **argv) {
  if (same(argv[1], "mkdir")) {
    int start = 2, parents = 0, status = 0;
    if (start < argc && same(argv[start], "-p")) {
      parents = 1;
      start++;
    }
    if (start == argc)
      return file_error("mkdir needs a path", -22);
    for (int i = start; i < argc; i++)
      status |= mkdir_path(argv[i], parents);
    return status;
  }
  if (same(argv[1], "touch")) {
    if (argc < 3)
      return file_error("touch needs a path", -22);
    int status = 0;
    for (int i = 2; i < argc; i++) {
      struct zenith_stat existing;
      if (file_stat(argv[i], &existing) == 0) {
        long updated = path_call(10, argv[i], 0, 0, 0);
        if (updated < 0)
          status |= file_error(argv[i], updated);
        continue;
      }
      long fd = open_path(argv[i], Z_OPEN_WRITE | Z_OPEN_CREATE);
      if (fd < 0)
        status |= file_error(argv[i], fd);
      else if (zenith_call(3, fd, 0, 0) < 0)
        status = 1;
    }
    return status;
  }
  if (same(argv[1], "rm")) {
    int start = 2, recursive = 0, force = 0, status = 0;
    while (start < argc && argv[start][0] == '-' && argv[start][1]) {
      if (same(argv[start], "--")) {
        start++;
        break;
      }
      for (int j = 1; argv[start][j]; j++) {
        if (argv[start][j] == 'r')
          recursive = 1;
        else if (argv[start][j] == 'f')
          force = 1;
        else
          return file_error("rm accepts -rf", -22);
      }
      start++;
    }
    if (start == argc)
      return force ? 0 : file_error("rm needs a path", -22);
    for (int i = start; i < argc; i++)
      status |= remove_path(argv[i], recursive, force, 0);
    return status;
  }
  if (same(argv[1], "rmdir")) {
    if (argc < 3)
      return file_error("rmdir needs a path", -22);
    int status = 0;
    for (int i = 2; i < argc; i++) {
      long n = path_call(3, argv[i], 0, 0, 0);
      if (n < 0)
        status |= file_error(argv[i], n);
    }
    return status;
  }
  if (same(argv[1], "cp") || same(argv[1], "mv")) {
    int start = 2, recursive = 0, status = 0, moving = same(argv[1], "mv");
    if (!moving && start < argc && same(argv[start], "-r")) {
      recursive = 1;
      start++;
    }
    if (argc - start < 2)
      return file_error("expected source and destination", -22);
    struct zenith_stat dst;
    int directory =
        file_stat(argv[argc - 1], &dst) == 0 && (dst.mode & 0170000) == 0040000;
    if (argc - start > 2 && !directory)
      return file_error("multiple sources require a directory", -22);
    for (int i = start; i < argc - 1; i++) {
      char joined[1025];
      const char *target = argv[argc - 1];
      if (directory) {
        if (join_path(joined, target, leaf_name(argv[i])) < 0) {
          status = 1;
          continue;
        }
        target = joined;
      }
      if (moving) {
        long n = path_call(4, argv[i], target, 0, 0);
        if (n < 0)
          status |= file_error(argv[i], n);
      } else
        status |= copy_path(argv[i], target, recursive, 0);
    }
    return status;
  }
  if (same(argv[1], "chmod")) {
    if (argc < 4)
      return file_error("chmod requires octal mode and paths", -22);
    unsigned long mode = 0;
    unsigned long len = length(argv[2]);
    if (!len || len > 4)
      return file_error("invalid mode", -22);
    for (unsigned long i = 0; i < len; i++) {
      if (argv[2][i] < '0' || argv[2][i] > '7')
        return file_error("invalid mode", -22);
      mode = mode * 8 + (unsigned long)(argv[2][i] - '0');
    }
    int status = 0;
    for (int i = 3; i < argc; i++) {
      long n = path_call(5, argv[i], 0, mode, 0);
      if (n < 0)
        status |= file_error(argv[i], n);
    }
    return status;
  }
  if (same(argv[1], "chown")) {
    if (argc < 4)
      return file_error("chown UID[:GID] paths", -22);
    unsigned long ids[2] = {0, ~0UL};
    int part = 0, digits = 0;
    for (const char *p = argv[2];; p++) {
      if (!*p || *p == ':') {
        if (!digits || (*p == ':' && part))
          return file_error("numeric UID[:GID] required", -22);
        if (!*p)
          break;
        part = 1;
        ids[1] = 0;
        digits = 0;
        continue;
      }
      if (*p < '0' || *p > '9' ||
          ids[part] > (4294967295UL - (unsigned long)(*p - '0')) / 10)
        return file_error("numeric UID[:GID] required", -22);
      ids[part] = ids[part] * 10 + (unsigned long)(*p - '0');
      digits++;
    }
    int status = 0;
    for (int i = 3; i < argc; i++) {
      long n = path_call(6, argv[i], 0, ids[0], ids[1]);
      if (n < 0)
        status |= file_error(argv[i], n);
    }
    return status;
  }
  if (same(argv[1], "ln")) {
    if (argc != 5 || !same(argv[2], "-s"))
      return file_error("ln -s TARGET LINK", -22);
    long n = path_call(7, argv[4], argv[3], 0, 0);
    return n < 0 ? file_error(argv[4], n) : 0;
  }
  return -1;
}
