/* Direct VMware SVGA II presentation, BSP only (included by workers.c).
 *
 * The pinned Cosmos driver keeps its back buffer in VRAM and, on every
 * Display(), copies the whole back buffer to the visible one (device memory
 * read and written) and then waits for a full-screen update. That cost is
 * paid for any damage, even a cursor move. Here only the damaged rows go to
 * the visible framebuffer (FB_START + FB_OFFSET, BYTES_PER_LINE pitch),
 * expanded by the integer scale while they are written, and one
 * SVGA_CMD_UPDATE for that rectangle is queued. The host is pinged (SYNC)
 * but not waited for, as the Linux vmwgfx FIFO path does.
 *
 * Protocol: VMware SVGA Device Developer Kit, svga_reg.h (registers,
 * FIFO MIN/MAX/NEXT_CMD/STOP and SVGA_CMD_UPDATE = 1).
 */
#define SVGA_REG_ENABLE 1
#define SVGA_REG_WIDTH 2
#define SVGA_REG_HEIGHT 3
#define SVGA_REG_BPP 7
#define SVGA_REG_BYTES_PER_LINE 12
#define SVGA_REG_FB_START 13
#define SVGA_REG_FB_OFFSET 14
#define SVGA_REG_VRAM_SIZE 15
#define SVGA_REG_MEM_START 18
#define SVGA_REG_MEM_SIZE 19
#define SVGA_REG_CONFIG_DONE 20
#define SVGA_REG_SYNC 21
#define SVGA_REG_BUSY 22
#define SVGA_CMD_UPDATE 1
#define SVGA_ID_PCI 0x040515adu /* PCI vendor 15AD, device 0405 */

struct svga_target {
    volatile u32 *fb;      /* visible frame, first pixel */
    u64 pitch, width, height;
    volatile u32 *fifo;    /* FIFO memory: MIN, MAX, NEXT_CMD, STOP, ... */
    u64 fifo_bytes;
};

static u64 svga_presents, svga_refused, svga_waits;

/* Bytes the producer may still write without NEXT_CMD reaching STOP. */
static u64 svga_fifo_free(u32 min, u32 max, u32 next, u32 stop) {
    return next >= stop ? (u64)(max - next) + (stop - min) - 4 : (u64)(stop - next) - 4;
}

static int svga_fifo_valid(const struct svga_target *t, u32 *min, u32 *max, u32 *next, u32 *stop) {
    *min = t->fifo[0]; *max = t->fifo[1]; *next = t->fifo[2]; *stop = t->fifo[3];
    return *min >= 16 && *min < *max && *max <= t->fifo_bytes && !((*min | *max | *next | *stop) & 3)
        && *next >= *min && *next < *max && *stop >= *min && *stop < *max;
}

/* sync() pings the host and waits (bounded) for it to drain the FIFO. */
static u64 svga_core(const struct svga_target *t, const u32 *src, u64 stride, u64 x, u64 y, u64 w, u64 h, u64 scale,
                     void (*sync)(void)) {
    if (!t->fb || !t->fifo || !src || scale < 1 || scale > 4 || !w || !h || stride < w || stride > 16384
        || x > 16384 || y > 16384 || w > 16384 || h > 16384
        || (x + w) * scale > t->width || (y + h) * scale > t->height || t->width * 4 > t->pitch) return 0;
    u32 min, max, next, stop;
    if (!svga_fifo_valid(t, &min, &max, &next, &stop)) return 0;
    /* Room for one five-word command before any pixel changes. */
    for (u32 n = 0; svga_fifo_free(min, max, next, stop) < 20; n++) {
        if (n == 4) return 0;
        sync(); svga_waits++;
        if (!svga_fifo_valid(t, &min, &max, &next, &stop)) return 0;
    }
    for (u64 row = 0; row < h; row++) {
        const u32 *line = src + row * stride;
        for (u64 d = 0; d < scale; d++) {
            volatile u32 *out = (volatile u32 *)((volatile u8 *)t->fb + ((y + row) * scale + d) * t->pitch) + x * scale;
            if (scale == 1) { for (u64 i = 0; i < w; i++) out[i] = line[i]; continue; }
            for (u64 i = 0; i < w; i++) {
                u32 v = line[i];
                for (u64 k = 0; k < scale; k++) *out++ = v;
            }
        }
    }
    u32 words[5] = { SVGA_CMD_UPDATE, (u32)(x * scale), (u32)(y * scale), (u32)(w * scale), (u32)(h * scale) };
    for (u32 i = 0; i < 5; i++) {
        t->fifo[next / 4] = words[i];
        next += 4; if (next == max) next = min;
    }
    __atomic_thread_fence(__ATOMIC_SEQ_CST);
    t->fifo[2] = next;   /* one publication of the whole command */
    return 1;
}

#ifdef ZENITH_CPU_HOST_TEST
static volatile u32 *host_svga_fifo;
static u64 host_svga_syncs;
static void host_svga_sync(void) {
    /* The fake host consumes everything queued. */
    host_svga_syncs++; if (host_svga_fifo) host_svga_fifo[3] = host_svga_fifo[2];
}
u64 zenith_svga_test_present(u32 *fb, u64 pitch, u64 width, u64 height, u32 *fifo, u64 fifo_bytes,
                             const u32 *src, u64 stride, u64 x, u64 y, u64 w, u64 h, u64 scale) {
    struct svga_target t = { fb, pitch, width, height, fifo, fifo_bytes };
    host_svga_fifo = fifo;
    return svga_core(&t, src, stride, x, y, w, h, scale, host_svga_sync);
}
u64 zenith_svga_test_syncs(void) { return host_svga_syncs; }
#else
static u16 svga_port;
static u64 svga_probed;
static inline void port_out32(u16 port, u32 value) { __asm__ volatile("outl %0, %1"::"a"(value), "Nd"(port)); }
static inline u32 port_in32(u16 port) { u32 v; __asm__ volatile("inl %1, %0":"=a"(v):"Nd"(port)); return v; }
static u32 svga_read(u32 index) {
    u64 flags = irq_off(); port_out32(svga_port, index); u32 v = port_in32(svga_port + 1); irq_restore(flags); return v;
}
static void svga_write(u32 index, u32 value) {
    u64 flags = irq_off(); port_out32(svga_port, index); port_out32(svga_port + 1, value); irq_restore(flags);
}
static void svga_sync(void) {
    svga_write(SVGA_REG_SYNC, 1);
    for (u32 n = 0; n < 1000000 && svga_read(SVGA_REG_BUSY); n++) pause_cpu();
}
/* The SVGA II I/O BAR (vendor 15AD, device 0405) once; the BAR never moves. */
static int svga_find(void) {
    if (svga_probed) return svga_port != 0;
    svga_probed = 1;
    for (u32 address = 0x80000000u; address < 0x81000000u; address += 0x100) {
        u64 flags = irq_off();
        port_out32(0xcf8, address); u32 id = port_in32(0xcfc);
        u32 bar = 0;
        if (id == SVGA_ID_PCI) { port_out32(0xcf8, address | 0x10); bar = port_in32(0xcfc); }
        irq_restore(flags);
        if (id == SVGA_ID_PCI) { if (bar & 1) svga_port = (u16)(bar & 0xfffc); break; }
    }
    return svga_port != 0;
}
/* 1: the rectangle is on screen (an update is queued). 0: nothing written or
 * queued; the caller presents through the driver instead. */
u64 zenith_svga_present(u64 offset, const u32 *src, u64 stride, u64 x, u64 y, u64 w, u64 h, u64 scale) {
    if (!offset || zenith_cpu_logical_id() != 0 || !svga_find()) { svga_refused++; return 0; }
    if (svga_read(SVGA_REG_ENABLE) != 1 || svga_read(SVGA_REG_CONFIG_DONE) != 1 || svga_read(SVGA_REG_BPP) != 32) { svga_refused++; return 0; }
    u64 width = svga_read(SVGA_REG_WIDTH), height = svga_read(SVGA_REG_HEIGHT), pitch = svga_read(SVGA_REG_BYTES_PER_LINE);
    u64 start = svga_read(SVGA_REG_FB_START), fb_offset = svga_read(SVGA_REG_FB_OFFSET), vram = svga_read(SVGA_REG_VRAM_SIZE);
    u64 fifo_start = svga_read(SVGA_REG_MEM_START), fifo_bytes = svga_read(SVGA_REG_MEM_SIZE);
    if (!start || !fifo_start || fifo_bytes < 4096 || fb_offset + pitch * height > vram) { svga_refused++; return 0; }
    struct svga_target t = { (volatile u32 *)(offset + start + fb_offset), pitch, width, height,
                             (volatile u32 *)(offset + fifo_start), fifo_bytes };
    u64 ok = svga_core(&t, src, stride, x, y, w, h, scale, svga_sync);
    if (!ok) { svga_refused++; return 0; }
    svga_write(SVGA_REG_SYNC, 1);   /* ping: the host processes the update now */
    svga_presents++;
    return 1;
}
#endif
u64 zenith_svga_counter(u64 which) { return which == 0 ? svga_presents : which == 1 ? svga_refused : svga_waits; }
