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
#define SVGA_REG_CAPABILITIES 17
#define SVGA_REG_CURSOR_ID 24
#define SVGA_REG_CURSOR_X 25
#define SVGA_REG_CURSOR_Y 26
#define SVGA_REG_CURSOR_ON 27
#define SVGA_CAP_CURSOR 0x20
#define SVGA_CAP_ALPHA_CURSOR 0x200
#define SVGA_CAP_EXTENDED_FIFO 0x8000
#define SVGA_FIFO_CAPABILITIES 4
#define SVGA_FIFO_CURSOR_ON 9
#define SVGA_FIFO_CURSOR_X 10
#define SVGA_FIFO_CURSOR_Y 11
#define SVGA_FIFO_CURSOR_COUNT 12
#define SVGA_FIFO_CAP_CURSOR_BYPASS_3 (1u << 4)
#define SVGA_CMD_DEFINE_CURSOR 19
#define SVGA_CMD_DEFINE_ALPHA_CURSOR 22
#define SVGA_CMD_UPDATE 1
#define SVGA_ID_PCI 0x040515adu /* PCI vendor 15AD, device 0405 */

struct svga_target {
    volatile u32 *fb;      /* visible frame, first pixel */
    u64 pitch, width, height;
    volatile u32 *fifo;    /* FIFO memory: MIN, MAX, NEXT_CMD, STOP, ... */
    u64 fifo_bytes;
};

static u64 svga_presents, svga_refused, svga_waits;

/* One row to the framebuffer with the CPU's string copy (fast-string microcode: whole lines, no
 * SSE in this object). The fence before NEXT_CMD orders it before the update command. */
static void svga_row(volatile u32 *out, const u32 *in, u64 n) {
    __asm__ volatile("rep movsl" : "+D"(out), "+S"(in), "+c"(n) : : "memory");
}

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
            if (scale == 1) { svga_row(out, line, w); continue; }
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

/* Fractional scales (125/150/175%): the logical frame is resampled bilinearly into device pixels
 * (8-bit weights, red/blue and green in packed lanes). A device pixel samples the logical frame at
 * its centre, so a logical change reaches the device pixels within one logical pixel of it: the
 * damage is widened by one before it is mapped. The stopgap until the shell draws at the device's
 * resolution; integer scales keep the exact copies above. */
static inline u32 svga_lerp(u32 p, u32 q, u32 f) {
    u32 g = 256 - f;
    u32 rb = (((p & 0xff00ffu) * g + (q & 0xff00ffu) * f) >> 8) & 0xff00ffu;
    u32 gg = (((p & 0x00ff00u) * g + (q & 0x00ff00u) * f) >> 8) & 0x00ff00u;
    return 0xff000000u | rb | gg;
}
/* Device rectangle for a logical damage rectangle; 0 when empty or invalid. */
static int svga_filter_rect(u64 width, u64 height, u64 x, u64 y, u64 w, u64 h, u64 percent, u64 dw, u64 dh, u64 *r) {
    if (percent < 101 || percent > 399 || !width || !height || width > 16384 || height > 16384 || !w || !h
        || x >= width || y >= height || w > width - x || h > height - y || dw > 16384 || dh > 16384) return 0;
    u64 x0 = x ? x - 1 : 0, y0 = y ? y - 1 : 0, x1 = x + w + 1 > width ? width : x + w + 1, y1 = y + h + 1 > height ? height : y + h + 1;
    u64 px0 = x0 * percent / 100, py0 = y0 * percent / 100;
    u64 px1 = (x1 * percent + 99) / 100, py1 = (y1 * percent + 99) / 100;
    if (x1 == width) px1 = dw; if (y1 == height) py1 = dh;
    if (px1 > dw) px1 = dw; if (py1 > dh) py1 = dh;
    if (px0 >= px1 || py0 >= py1) return 0;
    r[0] = px0; r[1] = py0; r[2] = px1 - px0; r[3] = py1 - py0;
    return 1;
}
/* Logical coordinate of a device pixel's centre, 8.8 fixed point, split into index and weight. */
static inline void svga_sample(u64 device, u64 percent, u64 limit, u64 *i0, u64 *i1, u32 *f) {
    long long v = (long long)((2 * device + 1) * 100 * 256 / (2 * percent)) - 128;
    if (v < 0) v = 0;
    u64 i = (u64)v >> 8; *f = (u32)(v & 255);
    if (i >= limit - 1) { i = limit - 1; *f = 0; }
    *i0 = i; *i1 = i + 1 < limit ? i + 1 : i;
}
/* Column samples, computed once per present (BSP only, like every present). */
static u32 svga_col0[16384], svga_col1[16384];
static u8 svga_colf[16384];
static void svga_filter(volatile u32 *dst, u64 dst_pitch_bytes, const u32 *src, u64 width, u64 height, const u64 *r, u64 percent) {
    for (u64 col = 0; col < r[2]; col++) {
        u64 x0, x1; u32 fx; svga_sample(r[0] + col, percent, width, &x0, &x1, &fx);
        svga_col0[col] = (u32)x0; svga_col1[col] = (u32)x1; svga_colf[col] = (u8)fx;
    }
    for (u64 row = 0; row < r[3]; row++) {
        u64 y0, y1; u32 fy; svga_sample(r[1] + row, percent, height, &y0, &y1, &fy);
        const u32 *a = src + y0 * width, *b = src + y1 * width;
        volatile u32 *out = (volatile u32 *)((volatile u8 *)dst + row * dst_pitch_bytes);
        for (u64 col = 0; col < r[2]; col++) {
            u32 x0 = svga_col0[col], x1 = svga_col1[col], fx = svga_colf[col];
            u32 top = fx ? svga_lerp(a[x0], a[x1], fx) : a[x0];
            u32 bottom = fx ? svga_lerp(b[x0], b[x1], fx) : b[x0];
            out[col] = fy ? svga_lerp(top, bottom, fy) : (top | 0xff000000u);
        }
    }
}
/* Into a buffer holding just the device rectangle (row stride stride_px); rect receives it. */
u64 zenith_scale_filtered(u32 *dst, u64 stride_px, const u32 *src, u64 width, u64 height, u64 x, u64 y, u64 w, u64 h,
                          u64 percent, u64 dw, u64 dh, u64 *rect) {
    if (!dst || !src || !rect || !svga_filter_rect(width, height, x, y, w, h, percent, dw, dh, rect) || stride_px < rect[2]) return 0;
    svga_filter(dst, stride_px * 4, src, width, height, rect, percent);
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
/* The device layout changes only with a mode set, which the display owner announces
 * (zenith_svga_invalidate): every present and pointer move otherwise re-read eleven registers,
 * two port exits each on a virtual machine. */
static struct svga_target svga_cached;
static u64 svga_cached_offset, svga_cache_hits;
void zenith_svga_invalidate(void) { svga_cached_offset = 0; }
static int svga_target_read(u64 offset, struct svga_target *t);
static int svga_target_now(u64 offset, struct svga_target *t) {
    if (offset && offset == svga_cached_offset) { *t = svga_cached; svga_cache_hits++; return 1; }
    if (!svga_target_read(offset, t)) { svga_cached_offset = 0; return 0; }
    svga_cached = *t; svga_cached_offset = offset;
    return 1;
}
/* The enabled device's current visible frame and FIFO, or 0. */
static int svga_target_read(u64 offset, struct svga_target *t) {
    if (!offset || zenith_cpu_logical_id() != 0 || !svga_find()) return 0;
    if (svga_read(SVGA_REG_ENABLE) != 1 || svga_read(SVGA_REG_CONFIG_DONE) != 1 || svga_read(SVGA_REG_BPP) != 32) return 0;
    u64 width = svga_read(SVGA_REG_WIDTH), height = svga_read(SVGA_REG_HEIGHT), pitch = svga_read(SVGA_REG_BYTES_PER_LINE);
    u64 start = svga_read(SVGA_REG_FB_START), fb_offset = svga_read(SVGA_REG_FB_OFFSET), vram = svga_read(SVGA_REG_VRAM_SIZE);
    u64 fifo_start = svga_read(SVGA_REG_MEM_START), fifo_bytes = svga_read(SVGA_REG_MEM_SIZE);
    if (!start || !fifo_start || fifo_bytes < 4096 || fb_offset + pitch * height > vram) return 0;
    *t = (struct svga_target){ (volatile u32 *)(offset + start + fb_offset), pitch, width, height,
                               (volatile u32 *)(offset + fifo_start), fifo_bytes };
    return 1;
}
/* 1: the rectangle is on screen (an update is queued). 0: nothing written or
 * queued; the caller presents through the driver instead. */
u64 zenith_svga_present(u64 offset, const u32 *src, u64 stride, u64 x, u64 y, u64 w, u64 h, u64 scale) {
    struct svga_target t;
    if (!svga_target_now(offset, &t)) { svga_refused++; return 0; }
    u64 ok = svga_core(&t, src, stride, x, y, w, h, scale, svga_sync);
    if (!ok) { svga_refused++; svga_cached_offset = 0; return 0; }
    svga_write(SVGA_REG_SYNC, 1);   /* ping: the host processes the update now */
    svga_presents++;
    return 1;
}

/* A fractional-scale present straight to the visible frame; 0 refuses (nothing written). */
u64 zenith_svga_present_filtered(u64 offset, const u32 *src, u64 width, u64 height, u64 x, u64 y, u64 w, u64 h, u64 percent) {
    struct svga_target t; u64 r[4];
    if (!src || !svga_target_now(offset, &t) || t.width * 4 > t.pitch
        || !svga_filter_rect(width, height, x, y, w, h, percent, t.width, t.height, r)) { svga_refused++; return 0; }
    u32 min, max, next, stop;
    if (!svga_fifo_valid(&t, &min, &max, &next, &stop)) { svga_refused++; svga_cached_offset = 0; return 0; }
    for (u32 n = 0; svga_fifo_free(min, max, next, stop) < 20; n++) {
        if (n == 4 || (svga_sync(), svga_waits++, !svga_fifo_valid(&t, &min, &max, &next, &stop))) { svga_refused++; return 0; }
    }
    svga_filter((volatile u32 *)((volatile u8 *)t.fb + r[1] * t.pitch) + r[0], t.pitch, src, width, height, r, percent);
    u32 words[5] = { SVGA_CMD_UPDATE, (u32)r[0], (u32)r[1], (u32)r[2], (u32)r[3] };
    for (u32 i = 0; i < 5; i++) { t.fifo[next / 4] = words[i]; next += 4; if (next == max) next = min; }
    __atomic_thread_fence(__ATOMIC_SEQ_CST);
    t.fifo[2] = next;
    svga_write(SVGA_REG_SYNC, 1);
    svga_presents++;
    return 1;
}

/* Hardware pointer. The host draws it over the frame, so moving it is a few register writes,
 * never a compose or present, and with the absolute pointer VMware shows it as its own pointer.
 * The image is premultiplied ARGB at the device's resolution (sharp under any scale). Devices
 * without the alpha cursor (QEMU's) get the legacy AND/XOR form: alpha below half is clear. */
static u64 svga_cursor_kind;   /* 0 none, 1 alpha, 2 legacy */
static int svga_fifo_words(const struct svga_target *t, const u32 *head, u32 nhead, const u32 *body, u64 nbody) {
    u32 min, max, next, stop;
    if (!svga_fifo_valid(t, &min, &max, &next, &stop)) return 0;
    u64 bytes = ((u64)nhead + nbody) * 4;
    if (bytes + 16 > (u64)(max - min)) return 0;
    for (u32 n = 0; svga_fifo_free(min, max, next, stop) < bytes; n++) {
        if (n == 8) return 0;
        svga_sync(); svga_waits++;
        if (!svga_fifo_valid(t, &min, &max, &next, &stop)) return 0;
    }
    for (u32 i = 0; i < nhead; i++) { t->fifo[next / 4] = head[i]; next += 4; if (next == max) next = min; }
    for (u64 i = 0; i < nbody; i++) { t->fifo[next / 4] = body[i]; next += 4; if (next == max) next = min; }
    __atomic_thread_fence(__ATOMIC_SEQ_CST);
    t->fifo[2] = next;
    svga_write(SVGA_REG_SYNC, 1);
    return 1;
}
/* Returns 1 (alpha) or 2 (legacy) when defined, 0 when the device has no hardware pointer. */
u64 zenith_svga_cursor_define(u64 offset, const u32 *argb, u64 w, u64 h, u64 hot_x, u64 hot_y, u32 *scratch, u64 scratch_words) {
    struct svga_target t;
    if (!argb || !w || !h || w > 64 || h > 64 || hot_x >= w || hot_y >= h || !svga_target_now(offset, &t)) return 0;
    u32 caps = svga_read(SVGA_REG_CAPABILITIES);
    if (caps & SVGA_CAP_ALPHA_CURSOR) {
        u32 head[6] = { SVGA_CMD_DEFINE_ALPHA_CURSOR, 0, (u32)hot_x, (u32)hot_y, (u32)w, (u32)h };
        if (!svga_fifo_words(&t, head, 6, argb, w * h)) return 0;
        return svga_cursor_kind = 1;
    }
    if (!(caps & SVGA_CAP_CURSOR)) return 0;
    u64 mask_words = (w + 31) / 32 * h, need = mask_words + w * h;
    if (!scratch || scratch_words < need) return 0;
    for (u64 i = 0; i < mask_words; i++) scratch[i] = 0;
    for (u64 y = 0; y < h; y++)
        for (u64 x = 0; x < w; x++) {
            u32 v = argb[y * w + x];
            int clear = (v >> 24) < 128;
            if (clear) ((u8 *)scratch)[y * ((w + 31) / 32) * 4 + x / 8] |= (u8)(0x80 >> (x % 8));
            scratch[mask_words + y * w + x] = clear ? 0 : (v | 0xff000000u);
        }
    u32 head[8] = { SVGA_CMD_DEFINE_CURSOR, 0, (u32)hot_x, (u32)hot_y, (u32)w, (u32)h, 1, 32 };
    if (!svga_fifo_words(&t, head, 8, scratch, need)) return 0;
    return svga_cursor_kind = 2;
}
/* Shows (visible 1) or hides the defined pointer at the device pixel (x, y): its hotspot. */
u64 zenith_svga_cursor_move(u64 offset, u64 x, u64 y, u64 visible) {
    struct svga_target t;
    if (!svga_cursor_kind || !svga_target_now(offset, &t)) return 0;
    u32 caps = svga_read(SVGA_REG_CAPABILITIES);
    if ((caps & SVGA_CAP_EXTENDED_FIFO) && t.fifo[0] > SVGA_FIFO_CURSOR_COUNT * 4
        && (t.fifo[SVGA_FIFO_CAPABILITIES] & SVGA_FIFO_CAP_CURSOR_BYPASS_3)) {
        t.fifo[SVGA_FIFO_CURSOR_ON] = visible ? 1 : 0;
        t.fifo[SVGA_FIFO_CURSOR_X] = (u32)x;
        t.fifo[SVGA_FIFO_CURSOR_Y] = (u32)y;
        __atomic_thread_fence(__ATOMIC_SEQ_CST);
        t.fifo[SVGA_FIFO_CURSOR_COUNT] = t.fifo[SVGA_FIFO_CURSOR_COUNT] + 1;
        return 3;
    }
    svga_write(SVGA_REG_CURSOR_ID, 0);
    svga_write(SVGA_REG_CURSOR_X, (u32)x);
    svga_write(SVGA_REG_CURSOR_Y, (u32)y);
    svga_write(SVGA_REG_CURSOR_ON, visible ? 1 : 0);
    return 2;
}
#endif
u64 zenith_svga_counter(u64 which) { return which == 0 ? svga_presents : which == 1 ? svga_refused : svga_waits; }
