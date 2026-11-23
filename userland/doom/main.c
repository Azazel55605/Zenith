/* SPDX-License-Identifier: GPL-2.0-only */
#include "../include/zenith_graphics.h"
#include "engine.h"
#include "launcher.h"

enum { WIDTH = 320, HEIGHT = 200, MIB = 1024 * 1024, GUI_ESCAPE = 1 };
static unsigned char wad[30 * MIB];
static unsigned int wad_size;
enum { EPISODE_ONE = 1, EPISODE_TWO = 2, EPISODE_FOUR = 4, MAP_ONE = 8 };
static int wad_maps;
static unsigned int pixels[DOOM_WINDOW_PIXELS];
static struct zui ui;
static long surface = -1;
static long start_time;
static const char *engine_wad;

static unsigned long length(const char *text)
{
    unsigned long n = 0;
    while (text[n]) n++;
    return n;
}

static int equal(const char *left, const char *right)
{
    while (*left && *left == *right) { left++; right++; }
    return *left == *right;
}

static void print(const char *text)
{
    unsigned long remaining = length(text);
    while (remaining) {
        unsigned long count = remaining > 4096 ? 4096 : remaining;
        long sent = zenith_write(text, count);
        if (sent <= 0) return;
        text += sent;
        remaining -= (unsigned long)sent;
    }
}

static _Noreturn void finish(int code)
{
    if (surface >= 0) zenith_gui_close(surface);
    zenith_exit(code);
}

static unsigned int little32(const unsigned char *bytes)
{
    return bytes[0] | ((unsigned int)bytes[1] << 8) |
        ((unsigned int)bytes[2] << 16) | ((unsigned int)bytes[3] << 24);
}

static int lump_is(const unsigned char *entry, const char *name)
{
    for (unsigned int i = 0; i < 8; i++) {
        if (entry[8 + i] != (unsigned char)*name) return 0;
        if (*name) name++;
    }
    return *name == 0;
}

static const char *validate_wad(void)
{
    if ((wad[0] != 'I' && wad[0] != 'P') || wad[1] != 'W' || wad[2] != 'A' || wad[3] != 'D')
        return "Unrecognized WAD header.";
    unsigned int count = little32(wad + 4);
    unsigned int offset = little32(wad + 8);
    const char *invalid = "Invalid WAD lump directory.";
    if (!count || offset < 12 || offset > wad_size || count > (wad_size - offset) / 16) return invalid;
    const char *base_lumps[] = {"PLAYPAL", "COLORMAP", "PNAMES", "TEXTURE1", "DMENUPIC"};
    unsigned int required = (unsigned int)(sizeof(base_lumps) / sizeof(*base_lumps));
    unsigned int found = 0;
    wad_maps = 0;
    for (unsigned int i = 0; i < count; i++) {
        const unsigned char *entry = wad + offset + 16 * i;
        unsigned int position = little32(entry);
        unsigned int size = little32(entry + 4);
        if (position > wad_size || size > wad_size - position) return invalid;
        if (count - i >= 3 && lump_is(entry + 16, "THINGS") && lump_is(entry + 32, "LINEDEFS")) {
            if (lump_is(entry, "E1M1")) wad_maps |= EPISODE_ONE;
            if (lump_is(entry, "E2M1")) wad_maps |= EPISODE_TWO;
            if (lump_is(entry, "E4M1")) wad_maps |= EPISODE_FOUR;
            if (lump_is(entry, "MAP01")) wad_maps |= MAP_ONE;
        }
        if (wad[0] == 'P' && size) {
            for (unsigned int n = 0; n < required; n++) {
                if (lump_is(entry, base_lumps[n])) found |= 1u << n;
            }
        }
    }
    if (wad[0] == 'P' && (found != (1u << required) - 1 || !(wad_maps & (EPISODE_ONE | MAP_ONE))))
        return "This add-on needs a base-game WAD.";
    return 0;
}

static const char *load_wad(const char *path)
{
    long fd = zenith_call(2, (unsigned long)path, length(path), Z_OPEN_READ);
    if (fd < 0) return fd == -13 ? "Permission denied for this WAD." : "Could not open the WAD.";

    long size = zenith_call(406, fd, 0, 2);
    if (size < 12 || size > (long)sizeof(wad) || zenith_call(406, fd, 0, 0) != 0) {
        zenith_call(3, fd, 0, 0);
        return "Choose a readable WAD of at most 30 MiB.";
    }
    wad_size = 0;
    long got = 0;
    while (wad_size < (unsigned long)size) {
        unsigned long remaining = (unsigned long)size - wad_size;
        got = zenith_call(0, fd, (unsigned long)(wad + wad_size), remaining > 4096 ? 4096 : remaining);
        if (got <= 0) break;
        wad_size += (unsigned int)got;
    }
    zenith_call(3, fd, 0, 0);
    if (got == -28) return "Read limit reached. Wait a minute and retry.";
    if (wad_size != (unsigned long)size) return "Could not read the complete WAD.";
    return validate_wad();
}

static const char *load_game(const char *path)
{
    const char *error = load_wad(path);
    if (error) return error;
    if (wad_maps & MAP_ONE) engine_wad = "./doom2.wad";
    else if ((wad_maps & EPISODE_ONE) && (wad_maps & EPISODE_FOUR)) engine_wad = "./doomu.wad";
    else if ((wad_maps & EPISODE_ONE) && (wad_maps & EPISODE_TWO)) engine_wad = "./doom.wad";
    else if (wad_maps & EPISODE_ONE) engine_wad = "./doom1.wad";
    else return "This WAD has no supported Doom or Doom II first map.";
    return 0;
}

/* The engine has a small number of persistent allocations plus its 12 MiB
 * zone allocator. Reuse temporary blocks and keep every payload 16-byte aligned. */
struct block { struct block *next; unsigned int capacity, available; };
_Static_assert(sizeof(struct block) == 16, "allocation alignment");
static _Alignas(16) unsigned char heap[16 * MIB];
static unsigned int used;
static struct block *blocks;

static void *allocate(int size)
{
    if (size <= 0 || (unsigned int)size > sizeof(heap) - sizeof(struct block)) {
        print("doom: invalid engine allocation\n");
        finish(1);
    }
    unsigned int capacity = ((unsigned int)size + 15u) & ~15u;
    for (struct block *b = blocks; b; b = b->next) {
        if (b->available && b->capacity >= capacity) {
            b->available = 0;
            return b + 1;
        }
    }
    if (sizeof(struct block) + capacity > sizeof(heap) - used) {
        print("doom: engine memory exhausted\n");
        finish(1);
    }
    struct block *b = (struct block *)(heap + used);
    used += sizeof(*b) + capacity;
    *b = (struct block){blocks, capacity, 0};
    blocks = b;
    return b + 1;
}

static void release(void *ptr)
{
    if (ptr) ((struct block *)ptr - 1)->available = 1;
}

/* Resolve only the selected IWAD. The engine never reads configuration or
 * writes to the guest filesystem. Each engine open owns its own cursor. */
struct cursor { unsigned int position; int in_use; };
static struct cursor cursors[4];

static void *open_wad(const char *name, const char *mode)
{
    if (!equal(mode, "rb") || !equal(name, engine_wad)) return 0;
    for (unsigned int i = 0; i < sizeof(cursors) / sizeof(cursors[0]); i++) {
        if (!cursors[i].in_use) {
            cursors[i] = (struct cursor){0, 1};
            return &cursors[i];
        }
    }
    return 0;
}

static void close_wad(void *handle) { ((struct cursor *)handle)->in_use = 0; }

static int read_wad(void *handle, void *buffer, int count)
{
    struct cursor *cursor = handle;
    if (count <= 0) return 0;
    unsigned int total = wad_size - cursor->position;
    if (total > (unsigned int)count) total = (unsigned int)count;
    unsigned char *out = buffer;
    for (unsigned int i = 0; i < total; i++) out[i] = wad[cursor->position + i];
    cursor->position += total;
    return (int)total;
}

static int write_disabled(void *handle, const void *buffer, int count)
{
    (void)handle; (void)buffer; (void)count;
    return 0;
}

static int seek_wad(void *handle, int offset, doom_seek_t origin)
{
    struct cursor *cursor = handle;
    long base = origin == DOOM_SEEK_CUR ? (long)cursor->position : origin == DOOM_SEEK_END ? (long)wad_size : 0;
    long target = base + offset;
    if (target < 0 || target > wad_size) return -1;
    cursor->position = (unsigned int)target;
    return 0;
}

static int tell_wad(void *handle) { return (int)((struct cursor *)handle)->position; }
static int eof_wad(void *handle) { return ((struct cursor *)handle)->position == wad_size; }

static char *environment(const char *name)
{
    static char cwd[] = ".";
    return equal(name, "HOME") ? cwd : 0;
}

static void get_time(int *seconds, int *microseconds)
{
    long now = zenith_call(628, 0, 0, 0);
    if (now < start_time) finish(1);
    long elapsed = now - start_time;
    *seconds = 1 + (int)(elapsed / 1000);
    *microseconds = (int)(elapsed % 1000) * 1000;
}

static const unsigned char scan_keys[256] = {
    [0x01] = DOOM_KEY_ESCAPE,
    [0x02] = '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '=',
    [0x0e] = DOOM_KEY_BACKSPACE, DOOM_KEY_TAB,
    [0x10] = 'q', 'w', 'e', 'r', 't', 'y', 'u', 'i', 'o', 'p', '[', ']',
    [0x1c] = DOOM_KEY_ENTER, DOOM_KEY_CTRL,
    [0x1e] = 'a', 's', 'd', 'f', 'g', 'h', 'j', 'k', 'l', ';', '\'',
    [0x2a] = DOOM_KEY_SHIFT,
    [0x2c] = 'z', 'x', 'c', 'v', 'b', 'n', 'm', ',', '.', '/',
    [0x36] = DOOM_KEY_SHIFT,
    [0x38] = DOOM_KEY_ALT, DOOM_KEY_SPACE,
    [0x3b] = DOOM_KEY_F1, DOOM_KEY_F2, DOOM_KEY_F3, DOOM_KEY_F4, DOOM_KEY_F5,
    DOOM_KEY_F6, DOOM_KEY_F7, DOOM_KEY_F8, DOOM_KEY_F9, DOOM_KEY_F10,
    [0x58] = DOOM_KEY_F12,
    [0x60] = DOOM_KEY_ALT,
    [0x68] = DOOM_KEY_UP_ARROW, [0x6b] = DOOM_KEY_LEFT_ARROW,
    [0x6d] = DOOM_KEY_RIGHT_ARROW, [0x70] = DOOM_KEY_DOWN_ARROW,
};
static unsigned char scans_down[256], key_holds[256];

static void reset_keys(void)
{
    for (unsigned int i = 0; i < sizeof(scans_down); i++) {
        scans_down[i] = 0;
        key_holds[i] = 0;
    }
    zenith_doom_reset_keys();
}

static void physical_key(int scan, int pressed)
{
    if (scan <= 0 || scan >= (int)sizeof(scan_keys) || scans_down[scan] == pressed) return;
    doom_key_t key = (doom_key_t)scan_keys[scan];
    if (!key) return;
    scans_down[scan] = (unsigned char)pressed;
    /* Both Shift keys (or both Alt keys) can hold the same Doom control. */
    int transition = pressed ? key_holds[key]++ == 0 : --key_holds[key] == 0;
    if (transition && !zenith_doom_key(key, pressed)) reset_keys();
}

static int open_window(const char *title)
{
    surface = zenith_gui_open(DOOM_WINDOW_WIDTH, DOOM_WINDOW_HEIGHT, title, length(title));
    if (surface < 0 || zenith_gui_interactive(surface) < 0
        || !zui_init(&ui,pixels,DOOM_WINDOW_WIDTH,DOOM_WINDOW_HEIGHT)
        || zenith_gui_resize_enable(surface) < 0) {
        print("doom: a graphical session with viewport support is required\n");
        return 0;
    }
    return 1;
}

/* The original 320x200 renderer used non-square pixels. Fit its intended
 * 4:3 display without stretching it to the shape of the desktop window. */
static void game_frame(const unsigned char *rgb)
{
    static unsigned int source[WIDTH*HEIGHT];
    static unsigned short columns[ZUI_MAX_WIDTH], rows[ZUI_MAX_HEIGHT];
    static int width, height, x, y, old_width, old_height;
    if (ui.width != old_width || ui.height != old_height) {
        old_width=ui.width; old_height=ui.height;
        width=ui.width;
        height=width*3/4;
        if (height>ui.height) { height=ui.height; width=height*4/3; }
        x=(ui.width-width)/2;
        y=(ui.height-height)/2;
        for(int c=0;c<width;c++) columns[c]=(unsigned short)(c*WIDTH/width);
        for(int r=0;r<height;r++) rows[r]=(unsigned short)(r*HEIGHT/height);
        zui_fill(&ui,(struct zui_rect){0,0,ui.width,ui.height},0);
    }
    for (unsigned int i=0;i<WIDTH*HEIGHT;i++)
        source[i]=((unsigned int)rgb[3*i]<<16)|((unsigned int)rgb[3*i+1]<<8)|rgb[3*i+2];
    for(int r=0;r<height;r++) {
        unsigned int *dest=ui.pixels+(y+r)*ui.width+x;
        const unsigned int *line=source+rows[r]*WIDTH;
        for(int c=0;c<width;c++) dest[c]=line[columns[c]];
    }
}

static _Noreturn void run_engine(int play)
{
    if (play && zenith_gui_key_edges(surface) < 0) {
        print("doom: play requires Zenith's physical-key ABI\n");
        finish(1);
    }
    doom_set_print(print);
    doom_set_malloc(allocate, release);
    doom_set_file_io(open_wad, close_wad, read_wad, write_disabled, seek_wad, tell_wad, eof_wad);
    doom_set_gettime(get_time);
    doom_set_exit(finish);
    doom_set_getenv(environment);
    static char program[] = "doom";
    char *args[] = {program, 0};
    start_time = zenith_call(628, 0, 0, 0);
    if (start_time < 0) finish(1);
    doom_init(1, args, DOOM_FLAG_HIDE_MOUSE_OPTIONS | DOOM_FLAG_HIDE_SOUND_OPTIONS | DOOM_FLAG_HIDE_MUSIC_OPTIONS);

    long next_frame = start_time;
    int presented = 0;
    for (;;) {
        for (int i = 0; i < 64; i++) {
            struct zenith_gui_event event;
            long pending = zenith_gui_poll(surface, &event);
            if (pending < 0) finish(1);
            if (!pending) break;
            if (event.type == Z_GUI_CLOSE) finish(0);
            if (event.type == Z_GUI_RESIZE) {
                if (zui_apply_viewport(&ui,surface,DOOM_WINDOW_PIXELS)<0) finish(zui_window_closed(surface)?0:1);
                continue;
            }
            if (!play && event.type == Z_GUI_KEY && event.extra == GUI_ESCAPE) finish(0);
            if (!play) continue;
            if (event.type == Z_GUI_RESET || (event.type == Z_GUI_FOCUS && !event.code)) reset_keys();
            if (event.type == Z_GUI_KEY_DOWN || event.type == Z_GUI_KEY_UP)
                physical_key(event.code, event.type == Z_GUI_KEY_DOWN);
        }
        long now = zenith_call(628, 0, 0, 0);
        if (now < 0) finish(1);
        if (now < next_frame) continue;
        next_frame = now + 28;
        doom_update();
        game_frame(doom_get_framebuffer(3));
        if (zenith_gui_present(surface,ui.pixels,(unsigned long)ui.width*ui.height*4)<0)
            finish(zui_window_closed(surface)?0:1);
        if (!presented) { print(play ? "doom: ready\n" : "doom: preview presented\n"); presented = 1; }
    }
}

int main(int argc, char **argv, char **envp)
{
    if (argc == 2 && equal(argv[1], "--version")) {
        print("doom " SUNBEAM_VERSION "\n");
        return 0;
    }
    if (argc == 1 || ((argc == 2 || argc == 3) && equal(argv[1], "--launcher")
        && (argc == 2 || argv[2][0] == '/'))) {
        if (!open_window("Doom")) finish(1);
        int selected = doom_launcher(surface,&ui,envp,argc == 3 ? argv[2] : 0,load_game);
        if (selected <= 0) finish(selected < 0);
        run_engine(1);
    }
    if (argc == 3 && (equal(argv[1], "--check-wad") || equal(argv[1], "--play") || equal(argv[1], "--preview"))) {
        int check = equal(argv[1], "--check-wad");
        const char *error = check ? load_wad(argv[2]) : load_game(argv[2]);
        if (error) { print("doom: "); print(error); print("\n"); return 1; }
        if (check) { print("doom: base-game WAD directory is readable\n"); return 0; }
        int play = equal(argv[1], "--play");
        if (!open_window(play ? "Doom" : "Doom engine preview")) finish(1);
        run_engine(play);
    }
    print("usage: native run doom [--launcher [ABSOLUTE-FOLDER] | --version | --check-wad PATH | --preview PATH | --play PATH]\n");
    return !(argc == 2 && equal(argv[1], "--help"));
}
