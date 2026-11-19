#include "../aurora/buffer.h"
#include "../sunbeam/zenith.h"

enum { CAPACITY = 256 * 1024, IO = 4096, WIDTH = 48, ROWS = 8, TAB = 4 };
enum { EDIT, SEARCH, CONFIRM_EXIT };
#define HASH_START 14695981039346656037UL
enum { LEFT = 256, RIGHT, UP, DOWN, HOME, END, PAGE_UP, PAGE_DOWN, DELETE };

static struct au_text text;
static struct au_history history;
static char data[CAPACITY + 1], disk[CAPACITY + 1], undo_data[32768];
static unsigned int lines[32768];
static struct au_change changes[512];

static const char *path, *message;
static unsigned long path_length, fingerprint;
static struct zenith_stat original;
static int exists, crlf, bom;
static unsigned int caret, top, left, goal;
static int prompt, quit;
static char query[128], search[128];
static unsigned int query_length, search_length;
/* Compose the view before sending only the changed rows to the terminal. */
enum { MAX_ROWS = 128, MAX_COLUMNS = 512, VIEW_BYTES = MAX_ROWS * (MAX_COLUMNS * 2 + 24) + IO };
static char view[VIEW_BYTES], previous[VIEW_BYTES], output[VIEW_BYTES];
static char *frame = view;
static unsigned int previous_start[MAX_ROWS], previous_length[MAX_ROWS];
static int clear_screen;
static unsigned int columns = WIDTH, rows = ROWS, height;
static int fullscreen;
static unsigned int used;

static unsigned long length(const char *s)
{
    unsigned long n = 0;
    while (s[n]) ++n;
    return n;
}

static int equal(const char *a, const char *b)
{
    while (*a && *a == *b) { ++a; ++b; }
    return *a == *b;
}

static long open_file(const char *name, unsigned long flags)
{
    return zenith_call(2, (unsigned long)name, length(name), flags);
}

static long stat_file(const char *name, struct zenith_stat *info)
{
    return zenith_call(408, (unsigned long)name, length(name), (unsigned long)info);
}

static long mutate(unsigned int op, const char *name, const char *target, unsigned long value, unsigned long extra)
{
    return zenith_path(op, name, length(name), target, target ? length(target) : 0, value, extra);
}

static int write_bytes(long fd, const char *s, unsigned int count)
{
    while (count) {
        long n = zenith_call(1, fd, (unsigned long)s, count);
        if (n <= 0 || (unsigned long)n > count) return 0;
        count -= (unsigned int)n;
        s += n;
    }
    return 1;
}

/* The extra byte detects growth beyond the edit limit during the read. */
static long read_file(void)
{
    long fd = open_file(path, Z_OPEN_READ);
    if (fd < 0) return fd;
    unsigned int count = 0;
    long n = 0;
    while (count < sizeof(disk)) {
        unsigned int room = sizeof(disk) - count;
        n = zenith_call(0, fd, (unsigned long)(disk + count), room < IO ? room : IO);
        if (n <= 0) break;
        count += (unsigned int)n;
    }
    long closed = zenith_call(3, fd, 0, 0);
    return n < 0 ? n : closed < 0 ? closed : (long)count;
}

static unsigned long hash(unsigned long value, const char *bytes, unsigned int count)
{
    for (unsigned int i = 0; i < count; ++i)
        value = (value ^ (unsigned char)bytes[i]) * 1099511628211UL;
    return value;
}

static const char *load(void)
{
    long rc = stat_file(path, &original);
    if (rc == -2) { message = "New file"; return 0; }
    if (rc < 0) return "Cannot inspect file";
    if ((original.mode & 0xf000) != 0x8000) return "A regular file is required";
    if (original.size > CAPACITY) return "File exceeds 256 KiB";
    long size = read_file();
    if (size < 0) return "Cannot read file";
    if ((unsigned long)size != original.size) return "File changed while opening";
    if (au_check(disk, (unsigned int)size)) return "File is not UTF-8 text";
    fingerprint = hash(HASH_START, disk, (unsigned int)size);
    exists = 1;
    unsigned int bytes = au_normalize(disk, (unsigned int)size, &crlf, &bom);
    return au_load(&text, disk, bytes) ? "File has too many lines" : 0;
}

static unsigned int decimal(char *out, unsigned long value)
{
    char reversed[20];
    unsigned int n = 0;
    do {
        reversed[n++] = (char)('0' + value % 10);
        value /= 10;
    } while (value);
    for (unsigned int i = 0; i < n; ++i) out[i] = reversed[n - i - 1];
    return n;
}

/* Preserve the live destination until a fully written, flushed replacement is ready.
 * A fresh mode-0700 sibling directory keeps staging away from unrelated files. */
static int stage(const char *name, struct zenith_stat *staged, unsigned long *saved_hash)
{
    long fd = open_file(name, Z_OPEN_WRITE | Z_OPEN_CREATE);
    if (fd < 0) return 0;
    const char signature[] = "\xef\xbb\xbf";
    int ok = !bom || write_bytes(fd, signature, 3);
    unsigned int position = 0, size = bom ? 3 : 0;
    *saved_hash = bom ? hash(HASH_START, signature, 3) : HASH_START;
    while (ok && position < au_length(&text)) {
        unsigned int bytes = au_encode(&text, &position, crlf, disk, IO);
        size += bytes;
        ok = size <= CAPACITY && write_bytes(fd, disk, bytes);
        if (ok) *saved_hash = hash(*saved_hash, disk, bytes);
    }
    if (ok) ok = zenith_call(74, fd, 0, 0) == 0;
    if (zenith_call(3, fd, 0, 0) != 0) ok = 0;
    if (!ok || stat_file(name, staged) != 0) return 0;
    if (exists) {
        if ((staged->uid != original.uid || staged->gid != original.gid) &&
            mutate(6, name, 0, original.uid, original.gid) != 0) return 0;
        if (mutate(5, name, 0, original.mode & 0777, 0) != 0) return 0;
        return stat_file(name, staged) == 0;
    }
    return 1;
}

static int unchanged(void)
{
    struct zenith_stat current;
    long rc = stat_file(path, &current);
    if (!exists) return rc == -2;
    return rc == 0 && current.mode == original.mode && current.size == original.size &&
        current.uid == original.uid && current.gid == original.gid &&
        read_file() == (long)original.size &&
        hash(HASH_START, disk, (unsigned int)original.size) == fingerprint;
}

static int save(void)
{
    if (exists && !au_modified(&history)) {
        message = "No changes to save";
        return 1;
    }
    if (!unchanged()) {
        message = "File changed on disk; save cancelled";
        return 0;
    }
    if (exists) {
        long fd = open_file(path, Z_OPEN_WRITE);
        if (fd < 0 || zenith_call(3, fd, 0, 0) != 0) {
            message = "File is not writable";
            return 0;
        }
    }
    long rc = -17;

    char directory[1025], temporary[1025];
    unsigned int parent = 0;
    for (unsigned int i = 0; i < path_length; ++i)
        if (path[i] == '/') parent = i + 1;
    if (parent + 45 >= sizeof(directory)) { message = "Parent path is too long"; return 0; }
    for (unsigned int i = 0; i < parent; ++i) directory[i] = path[i];
    const char prefix[] = ".quill-save-";
    for (unsigned int i = 0; i < sizeof(prefix) - 1; ++i) directory[parent++] = prefix[i];
    parent += decimal(directory + parent, (unsigned long)zenith_identity(39));
    directory[parent++] = '-';
    for (unsigned int i = 0; i < 8; ++i) {
        directory[parent] = (char)('0' + i);
        directory[parent + 1] = 0;
        rc = mutate(1, directory, 0, 0700, 0);
        if (rc != -17) break;
    }
    if (rc != 0) { message = "Cannot create save directory"; return 0; }
    for (unsigned int i = 0; i <= parent; ++i) temporary[i] = directory[i];
    const char suffix[] = "/text";
    for (unsigned int i = 0; i < sizeof(suffix); ++i) temporary[parent + 1 + i] = suffix[i];

    struct zenith_stat staged;
    unsigned long saved_hash;
    int ok = stage(temporary, &staged, &saved_hash);
    /* Best-effort conflict detection while staging. Rename itself is atomic. */
    if (ok) ok = unchanged() && mutate(4, temporary, path, 0, 0) == 0;
    if (!ok) mutate(2, temporary, 0, 0, 0);
    mutate(3, directory, 0, 0, 0);
    if (!ok) { message = "Save failed; your edits are still open"; return 0; }

    original = staged;
    fingerprint = saved_hash;
    exists = 1;
    au_mark_saved(&history);
    message = "Saved";
    return 1;
}

static void put(char c) { frame[used++] = c; }
static void add(const char *s) { while (*s) put(*s++); }
static void number(unsigned long value) { used += decimal(frame + used, value); }
static void glyph(unsigned char first, unsigned char second)
{
    if (first >= ' ' && first < 127) put((char)first);
    else if ((first == 0xc2 && second >= 0xa0 && second <= 0xbf) ||
             (first == 0xc3 && second >= 0x80 && second <= 0xbf)) {
        put((char)first);
        put((char)second);
    } else put('?');
}

static void print_text(const char *s, unsigned int bytes, unsigned int width)
{
    for (unsigned int i = 0; i < bytes && width; ++i) {
        if (((unsigned char)s[i] & 192) == 128) continue;
        glyph((unsigned char)s[i], i + 1 < bytes ? (unsigned char)s[i + 1] : 0);
        --width;
    }
}

static void paint_line(unsigned int line)
{
    unsigned int position = au_line_start(&text, line), end = au_line_end(&text, line);
    unsigned int column = 0;
    while (position <= end && column < left + columns) {
        unsigned char c = position == end ? ' ' : (unsigned char)au_at(&text, position);
        unsigned int width = c == '\t' ? TAB - column % TAB : 1;
        unsigned char next = position + 1 < end ? (unsigned char)au_at(&text, position + 1) : 0;
        for (unsigned int i = 0; i < width && column < left + columns; ++i, ++column) {
            if (column < left) continue;
            int marked = !fullscreen && caret == position && i == 0;
            if (marked) add("\x1b[1;96m");
            if (marked && (position == end || c == '\t')) put('|');
            else glyph(c == '\t' ? ' ' : c, next);
            if (marked) add("\x1b[0m");
        }
        if (position == end) break;
        position = au_next(&text, position);
    }
}

static int refresh_size(void)
{
    long size = zenith_call(410, 0, 0, 0);
    if (size <= 0) return 0;
    height = (unsigned int)size >> 16;
    columns = (unsigned int)size & 65535;
    if (columns < 1 || columns > MAX_COLUMNS) return 0;
    if (height > MAX_ROWS) height = MAX_ROWS;
    clear_screen = 1;
    rows = height - (height >= 4 ? 3 : height == 3 ? 2 : height == 2 ? 1 : 0);
    return 1;
}

static int flush_frame(void)
{
    for (unsigned int offset = 0; offset < used;) {
        unsigned int chunk = used - offset;
        if (chunk > IO) chunk = IO;
        if (!write_bytes(1, frame + offset, chunk)) return 0;
        offset += chunk;
    }
    return 1;
}

static void position(unsigned int row, unsigned int column)
{
    add("\x1b["); number(row + 1); put(';'); number(column + 1); put('H');
}

static void footer(unsigned int line, unsigned int col)
{
    unsigned int start = used;
    if (height >= 3) {
        add("Line "); number(line + 1); add("  Col "); number(col + 1);
        if (message) { add("  "); add(message); }
        if (used - start > columns) used = start + columns;
        add("\r\n");
    }
    add("\x1b[7m");
    start = used;
    if (prompt == CONFIRM_EXIT) {
        add(columns >= 42 ? "Save changes? Y Save  N Discard  ^C Cancel" : "Save? Y Yes N No ^C");
    } else if (prompt == SEARCH) {
        add("Find: ");
        if (columns > 6) print_text(query, query_length, columns - 6);
    } else {
        add(columns >= 49 ? "^O Save  ^X Exit  ^W Find  ^N Next  ^Z Undo  ^Y Redo" :
            columns >= 32 ? "^O Save  ^X Exit  ^W Find" : "^O Save ^X Exit");
    }
    if (prompt == SEARCH) {
        unsigned int cells = 6;
        for (unsigned int i = 0; i < query_length && cells < columns; ++i)
            if (((unsigned char)query[i] & 192) != 128) ++cells;
        while (cells++ < columns) put(' ');
    } else {
        if (used - start > columns) used = start + columns;
        while (used - start < columns) put(' ');
    }
    add("\x1b[0m");
}

static int different(const char *a, const char *b, unsigned int count)
{
    for (unsigned int i = 0; i < count; ++i)
        if (a[i] != b[i]) return 1;
    return 0;
}

static int draw_view(unsigned int caret_row, unsigned int caret_column)
{
    unsigned int start = 0, size = used;
    frame = output;
    used = 0;
    if (clear_screen) add("\x1b[2J");
    for (unsigned int row = 0; row < height; ++row) {
        unsigned int end = start;
        while (end < size && view[end] != '\r') ++end;
        unsigned int count = end - start;
        if ((clear_screen && count) || (!clear_screen &&
            (count != previous_length[row] || different(view + start, previous + previous_start[row], count)))) {
            position(row, 0);
            add("\x1b[2K");
            for (unsigned int i = start; i < end; ++i) put(view[i]);
        }
        previous_start[row] = start;
        previous_length[row] = count;
        start = end < size ? end + 2 : size;
    }
    for (unsigned int i = 0; i < size; ++i) previous[i] = view[i];
    clear_screen = 0;
    position(caret_row, caret_column);
    int ok = flush_frame();
    frame = view;
    return ok;
}

static int paint(void)
{
    unsigned int line = au_line_of(&text, caret), col = au_column(&text, caret, TAB);
    if (line < top) top = line;
    if (line >= top + rows) top = line - rows + 1;
    if (col < left) left = col;
    if (col >= left + columns) left = col - columns + 1;
    used = 0;
    if (!fullscreen) add("\x1b[2J\x1b[H");
    unsigned int header = !fullscreen || height >= 4;
    if (header) {
        add("Quill  ");
        print_text(path, (unsigned int)path_length, columns > 9 ? columns - 9 : 0);
        if (au_modified(&history)) add(" *");
        add("\r\n");
    }
    for (unsigned int row = 0; row < rows; ++row) {
        if (top + row < text.line_count) paint_line(top + row);
        if (!fullscreen || height > 1) add("\r\n");
    }
    if (fullscreen) {
        if (height > 1) footer(line, col);
        if (prompt == SEARCH && height > 1) {
            unsigned int cell = 6;
            for (unsigned int i = 0; i < query_length; ++i) if (((unsigned char)query[i] & 192) != 128) ++cell;
            return draw_view(height - 1, cell < columns ? cell : columns - 1);
        }
        return draw_view(line - top + header, col - left);
    }
    add("Line "); number(line + 1); add("  Col "); number(col + 1);
    if (message) { add("  "); add(message); }
    add("\r\n");
    if (prompt == CONFIRM_EXIT) add("Save changes? Y Save  N Discard  ^C Cancel");
    else if (prompt == SEARCH) {
        add("Find: ");
        print_text(query, query_length, WIDTH - 6);
    } else {
        add("^O Save  ^X Exit  ^W Find  ^N Next\r\n");
        add("^Z Undo  ^Y Redo");
    }
    return write_bytes(1, frame, used);
}

static void edit(unsigned int at, unsigned int remove, const char *s, unsigned int bytes, int typing)
{
    if (au_edit(&text, &history, at, remove, s, bytes, caret, at + bytes, typing))
        message = "Edit exceeds the file or line limit";
    else caret = at + bytes;
}

static void move_vertical(int delta)
{
    int line = (int)au_line_of(&text, caret) + delta;
    if (line < 0) line = 0;
    if ((unsigned int)line >= text.line_count) line = (int)text.line_count - 1;
    caret = au_at_column(&text, (unsigned int)line, goal, TAB);
}

static void find_next(unsigned int from)
{
    if (!search_length) { message = "Use ^W to enter search text"; return; }
    long hit = au_find(&text, search, search_length, from, 1, AU_CASE);
    if (hit < 0 && from) hit = au_find(&text, search, search_length, 0, 1, AU_CASE);
    if (hit < 0) message = "No match";
    else caret = (unsigned int)hit;
}

static void key(int k, const char *s, unsigned int bytes)
{
    message = 0;
    if (prompt == CONFIRM_EXIT) {
        if (k == 'y' || k == 'Y') { prompt = EDIT; if (save()) quit = 1; }
        else if (k == 'n' || k == 'N') quit = 1;
        else if (k == 3) prompt = EDIT;
        return;
    }
    if (prompt == SEARCH) {
        if (k == 3) prompt = EDIT;
        else if (k == 8 || k == 127) {
            if (query_length) {
                do { --query_length; }
                while (query_length && ((unsigned char)query[query_length] & 192) == 128);
            }
        } else if (k == '\r' || k == '\n') {
            prompt = EDIT;
            if (query_length) {
                for (unsigned int i = 0; i < query_length; ++i) search[i] = query[i];
                search_length = query_length;
                find_next(caret);
            }
        } else if (bytes && query_length + bytes <= sizeof(query)) {
            for (unsigned int i = 0; i < bytes; ++i) query[query_length++] = s[i];
        }
        goal = au_column(&text, caret, TAB);
        return;
    }

    if (!bytes) au_history_break(&history);
    switch (k) {
    case 15: save(); break;
    case 24:
        if (au_modified(&history)) prompt = CONFIRM_EXIT;
        else quit = 1;
        break;
    case 23: prompt = SEARCH; query_length = 0; break;
    case 14:
        if (caret < au_length(&text)) find_next(au_next(&text, caret));
        else find_next(0);
        break;
    case 26: if (!au_undo(&text, &history, &caret)) message = "Nothing to undo"; break;
    case 25: if (!au_redo(&text, &history, &caret)) message = "Nothing to redo"; break;
    case 8: case 127:
        if (caret) { unsigned int prev = au_previous(&text, caret); edit(prev, caret - prev, 0, 0, 0); }
        break;
    case DELETE:
        if (caret < au_length(&text)) edit(caret, au_next(&text, caret) - caret, 0, 0, 0);
        break;
    case LEFT: caret = au_previous(&text, caret); break;
    case RIGHT: caret = au_next(&text, caret); break;
    case HOME: caret = au_line_start(&text, au_line_of(&text, caret)); break;
    case END: caret = au_line_end(&text, au_line_of(&text, caret)); break;
    case UP: move_vertical(-1); return;
    case DOWN: move_vertical(1); return;
    case PAGE_UP: move_vertical(-(int)rows); return;
    case PAGE_DOWN: move_vertical((int)rows); return;
    case '\r': case '\n': edit(caret, 0, "\n", 1, 0); break;
    case '\t': edit(caret, 0, "\t", 1, 0); break;
    default: if (bytes) edit(caret, 0, s, bytes, 1); break;
    }
    goal = au_column(&text, caret, TAB);
}

/* Decode only the navigation sequences supported by Zenith and common serial terminals. */
static void input(unsigned char c)
{
    static unsigned int escape, parameter, utf_bytes, utf_expected;
    static char utf[4];
    if (c == 27) { escape = 1; parameter = utf_bytes = 0; return; }
    if (escape == 1) {
        escape = c == '[' || c == 'O' ? 2 : 0;
        if (escape) return;
        /* Standalone Escape must not swallow the next ordinary key. */
    } else if (escape) {
        if (c >= '0' && c <= '9') {
            if (parameter < 1000) parameter = parameter * 10 + c - '0';
            return;
        }
        if (c >= 0x20 && c < 0x40) { escape = 3; return; }
        unsigned int sequence = escape;
        escape = 0;
        if (sequence == 3) return;
        switch (c) {
        case 'A': key(UP, 0, 0); return;
        case 'B': key(DOWN, 0, 0); return;
        case 'C': key(RIGHT, 0, 0); return;
        case 'D': key(LEFT, 0, 0); return;
        case 'H': key(HOME, 0, 0); return;
        case 'F': key(END, 0, 0); return;
        case '~':
            if (parameter == 3) key(DELETE, 0, 0);
            if (parameter == 5) key(PAGE_UP, 0, 0);
            if (parameter == 6) key(PAGE_DOWN, 0, 0);
            return;
        default: return;
        }
    }

    if (utf_bytes && (c & 192) == 128) {
        utf[utf_bytes++] = (char)c;
        if (utf_bytes == utf_expected) {
            if (!au_check(utf, utf_bytes)) key(0, utf, utf_bytes);
            utf_bytes = 0;
        }
        return;
    }
    utf_bytes = 0;
    if (c < 128) {
        char ascii = (char)c;
        key(c, &ascii, c >= 32 && c < 127 ? 1 : 0);
    } else if (c >= 0xc2 && c < 0xf5) {
        utf_expected = c < 0xe0 ? 2 : c < 0xf0 ? 3 : 4;
        utf[utf_bytes++] = (char)c;
    }
}

int main(int argc, char **argv)
{
    const char *usage = "Usage: quill FILE\n";
    if (argc == 2 && (equal(argv[1], "--help") || equal(argv[1], "--version"))) {
        const char *reply = equal(argv[1], "--version") ? "quill " SUNBEAM_VERSION "\n" : usage;
        return write_bytes(1, reply, (unsigned int)length(reply)) ? 0 : 1;
    }
    if (argc != 2 || !argv[1][0] || length(argv[1]) > 1024) {
        write_bytes(2, usage, (unsigned int)length(usage));
        return 2;
    }

    path = argv[1];
    path_length = length(path);
    if (path[path_length - 1] == '/') {
        const char *reply = "quill: a file name is required\n";
        write_bytes(2, reply, (unsigned int)length(reply));
        return 2;
    }
    au_text_init(&text, data, sizeof(data), lines, sizeof(lines) / sizeof(lines[0]));
    au_history_init(&history, changes, sizeof(changes) / sizeof(changes[0]), undo_data, sizeof(undo_data));
    long mode = zenith_call(401, 0, 0, 0);
    const char *error = mode < 0 || zenith_isatty(1) != 1 ? "A foreground terminal is required" : load();
    if (!error && zenith_call(402, 0, 0, 0) != 0) error = "Cannot acquire raw keyboard input";
    if (error) {
        write_bytes(2, "quill: ", 7);
        write_bytes(2, error, (unsigned int)length(error));
        write_bytes(2, "\n", 1);
        return 1;
    }

    char keys[64];
    fullscreen = refresh_size();
    const char *enter = "\x1b[?1049h\x1b[6 q";
    const char *leave = "\x1b[0m\x1b[0 q\x1b[?1049l";
    int ok = (!fullscreen || write_bytes(1, enter, (unsigned int)length(enter))) && paint();
    while (ok && !quit) {
        long count = zenith_call(0, 0, (unsigned long)keys, sizeof(keys));
        if (fullscreen && count == -11) {
            if (refresh_size()) ok = paint();
            else ok = 0;
            continue;
        }
        if (count <= 0) { ok = count == 0 && !au_modified(&history); break; }
        for (long i = 0; i < count && !quit; ++i) input((unsigned char)keys[i]);
        if (!quit) ok = paint();
    }
    if (fullscreen) write_bytes(1, leave, (unsigned int)length(leave));
    else write_bytes(1, "\x1b[0m\r\n", 6);
    zenith_call(402, 0, (unsigned long)mode, 0);
    if (!ok) write_bytes(2, "quill: terminal interrupted\n", sizeof("quill: terminal interrupted\n") - 1);
    return ok ? 0 : 1;
}
