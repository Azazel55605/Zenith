# Rendering phase 3: direct SVGA presents and incremental backdrops (Q3/C4)

[Master roadmap](../../ROADMAP.md) · [Archive](../README.md)

## Review of the 0.0.48 phase 3

0.0.48 added private AP foreground tiles (`CpuWorkers.TryCompose`) for native
material clients. Measured on the VMware SVGA II path (`tools/frame-bench.py`,
4 CPUs, `smp=native`, blur and full translucency), it never ran for the shell's
own windows or Polaris (`parallel compose frames=0`), and its BSP still copies
every tile in and out. Parallel blur saved about 10% of a cold whole-scene compose.
Neither addressed the measured costs below. The offload remains as qualified
for native material clients; it is not the source of the gains recorded here.

## Measured causes

- **Present, about 25 ms a frame.** The pinned `SvgaIIDriver` keeps its back
  buffer inside VRAM. `Display()` copies the whole back buffer to the visible one
  (`MemoryBlock.MoveDown`: device memory read and written), then sends a
  full-screen `SVGA_CMD_UPDATE` and polls `SVGA_REG_BUSY`, for any damage,
  including a cursor move. Compose took 1.3-1.8 ms of a 33 ms frame. This was
  the CPU0 load and why the desktop felt equally slow with and without effects.
- **Backdrop sampling, 4-6 ms when a lower window changed.** Averaging the scene
  into quarter-scale samples cost 16 bounds-checked reads per sample, repeated
  for whole windows just to find nothing changed; the blur itself took 0.25 ms.

## Implementation

- `src/Native/Cpu/svga.h` (built into the C1 object, BSP only): finds the SVGA II
  I/O BAR once, checks ENABLE/CONFIG_DONE/BPP and the framebuffer and FIFO bounds,
  writes only the damaged rows to `FB_START + FB_OFFSET` at `BYTES_PER_LINE`,
  expanding the integer scale as it writes, queues one five-word
  `SVGA_CMD_UPDATE` (room is checked before any pixel changes; `NEXT_CMD` is
  published once) and pings `SVGA_REG_SYNC` without waiting. Refusal changes
  nothing and the caller uses the driver.
- `FramePresenter` tries the direct path for the desktop and for boot, login,
  session/farewell fades and the local console. The first driver present after a
  direct one is the whole frame, since the driver's VRAM back buffer is stale.
  `fps direct on|off` compares the paths; `fps` reports both counters.
- `MaterialComposition` averages full on-screen blocks without bounds checks,
  red and blue summed in one word (16 x 255 fits a 16-bit lane); edge samples keep
  the clipped formula. Results are identical (host test against the reference).
- Incremental capture: `WindowManager` records what really changed beneath each
  window while redrawing lower windows (moved, restyled or updated windows and
  windows whose own backdrop changed, bottom up), not the redraw rectangle grown
  over whole translucent windows. `MaterialComposition.Dirty` limits sampling to
  those blocks when regions, place, scene size and fallback are unchanged. Any
  changed sample still blurs the whole region again.
- Built-in window backgrounds admit any area (they sample at quarter scale);
  the former 1,920,000-pixel limit silently dropped blur on maximized windows
  above 1080p.

## Results (QEMU VMware SVGA II, KVM, 4 CPUs, blur on)

| Measure | 0.0.48 | Now |
|---|---|---|
| Window animation (`fps motion`), 1024x768 | 31 fps, worst 33 ms | 166-168 fps, worst 2-4 ms |
| Window animation, 1920x1080 | | 150-156 fps, worst 2.7-3.8 ms |
| Window animation, 1600x1200 at 200%, 1 CPU | | 123-126 fps |
| One present | 25.4 ms | 0.32-0.38 ms (2.4 ms full 1600x1200) |
| Lower window changes, 1 / 2 lower windows | 6.4 / 10.0 ms | 2.6 / 4.3 ms |
| Dragging the top window | 3.9 ms | 2.4-2.7 ms |

Every run keeps `verify=0`, `partial-differs=0/0` and `cached-differs=0`: partial
and cached composes equal a full compose pixel for pixel. Screenshots at 1024x768,
1080p and 200% show correct output. Host: 892 tests, including the sampler
reference and incremental-versus-full captures; `test_svga_present.py` covers
damaged rows, scale, FIFO wrap, waiting for room and refusals.

## Boundaries

QEMU's VMware SVGA device is not the installed VMware host: actual frame rates,
the host's FIFO processing and 4K modes remain operator acceptance. The built-in
Terminal lays its text out on every draw (about 1-3.6 ms at 1080p); that is
application cost, not compositor cost. Network packet throughput is separate.
