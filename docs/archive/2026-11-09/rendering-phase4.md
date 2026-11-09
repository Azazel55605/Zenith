# Rendering phase 4: what really changed, idle CPU0 and power screens (Q3/C4)

[Master roadmap](../../ROADMAP.md) · [Archive](../README.md)

## Report

On the installed VMware host (8 CPUs, 1920x1200) with 0.0.49, Observatory showed
CPU0 at about 41% with nothing happening, the desktop felt slow, and turning blur
and transparency off made it slower still. Boot and power screens did not follow
the resolution, and 200% looked blocky.

## Reproduction and measurements

`QEMU vmware-svga` (64 MB VRAM), KVM, 8 CPUs, `smp=native`, 1920x1200, Polaris on
top of an Observatory window on its Performance page (its graphs glide at about
30 frames a second). New diagnostics: `fps phases` (per-compose lower/top passes
and their parts), `fps verify` (the shown scene against one composed from nothing,
every window capturing and blurring again), tick counts and the longest gap
between desktop ticks in each motion log line, and AP fault RIP/code/CR2 in
`cpu status`. CPU0 samples came from the QEMU monitor (`info registers` on
CPU 0, symbolized against the kernel ELF).

| CPU0 in that scene | blur | transparent | solid |
|---|---|---|---|
| 0.0.49 | 61-67% | 66% | 74% |
| this change | 27-29% | 27-30% | 23-30% |

The user's report that effects off is slower is confirmed: without blur, native
windows sampled their backdrop at full resolution instead of a quarter.

## Causes and changes

- **A client's frame redrew its whole window, and every translucent window over
  it.** Clients present whole frames. Each counted as a whole-window change, and
  the lower pass then grew its damage over every translucent window it touched.
  One compose cost 13.7 ms (content 9.6 ms). Now `ProcessSurface.CopyFrame`
  reports the rectangle that actually differs while it copies (new material
  regions report everything); windows accumulate it as `ContentDamage`. The lower
  pass grows damage bottom up only by each material's reach (blur radius,
  bilinear reconstruction and block rounding: `MaterialComposition.Reach`, 32 px)
  around what changed below a window, and the top window likewise. A capture that
  can change a window everywhere (a fresh capture, or a new adapted opacity from
  the luminance range) draws that window once more in full (`WholeChanged`).
  Compose fell to about 1.7 ms and the average scene frame from 15-20 ms to
  about 1.5 ms; `fps verify` reports 0 differing pixels in all three material modes.
- **Blend.** The premultiplied "over" used a per-channel loop with three divisions.
  It now uses red/blue lanes in one multiply and the exact `(t + (t >> 8)) >> 8`
  rounding; a host test compares every alpha against the old formula.
- **Presents of client frames.** Validation decoded bytes and divided per pixel;
  it now reads words per row with a cached region interval (host test against the
  old rule). Frames are copied as spans instead of per-pixel decoding.
- **Spinning after frames.** After each scene frame the kernel loop kept running
  until the next 4 ms frame slot. Only a running animation or drag keeps it awake
  now; other changes wait halted for the next interrupt.
- **Window animations** restore and present only the layers' old and new
  rectangles and the pointer, not a 9 MB copy and full-screen present per frame.
  The SVGA row copy uses `rep movsl` instead of volatile single stores.
- **Boot and power screens** draw at the device's physical resolution, sized by
  the screen height (1080 lines = 100%): logo, label and progress bar no longer
  shrink on large screens or turn blocky under a scale. The boot frame is reduced
  to the logical size for the fade into the first session.

## Findings not changed here

- **A latent AP race.** Counting the BSP's halts while it waits for a program
  slice or an AP compose tile as idle time (an `rdtsc` and an atomic exchange
  around the existing `sti; hlt`) made an AP triple-fault in 4 of 10 runs of the
  material-switch repro (transparent and solid while Observatory and Polaris run);
  the same kernel without it passed 9 of 9, 0.0.49 7 of 7. Nothing in it touches AP
  state, so a timing-dependent race in the protected-process path is present and
  rarely reached. The accounting change was removed; Observatory's CPU0 figure
  still includes the BSP halted in those waits. AP faults now record RIP, error
  code and CR2 (`cpu status`) for the next occurrence; a triple fault leaves none.

- **The desktop starves while native programs run.** The kernel loop is the
  scheduler's idle thread. A program's BSP service thread halts in place while its
  slice runs on an AP (`zenith_cpu_process_wait`), and the pinned Cosmos condition
  and sleep waits also halt in place until an interrupt. With Observatory and
  Polaris open a window animation got about 190 desktop ticks in 150 ms with gaps
  up to 81 ms; without them about 1,650. Blocking service threads on a monitor
  instead was measured worse (7-49 ticks), because `ConditionVariable.WaitTimeout`
  parks the same way. The remedy is a scheduler change: switch away at once from a
  thread that parks, and keep the desktop off the idle-thread priority.
- **AP compose offload** (`TryCompose`) does not pay: drag 7.2 versus 7.3 ms and a
  lower-window change 15.9 versus 17.6 ms with it on and off. Its tiles are copied
  in and out, which costs what the blend saves.
- **Early boot hang under QEMU** with 8 CPUs and OVMF: no kernel output, the CPUs in
  firmware/bootloader code. It occurs with 0.0.49 too, more often with two VMs
  running at once; not seen on the operator's VMware host.
- **HiDPI.** 200% enlarges a logical frame. Sharp scaling needs the shell to draw at
  physical resolution with scaled metrics, a scale in the client ABI, scaled font
  atlases and icons in the toolkit, and new releases of the native programs.
- **VMware 3D (SVGA3D/VGPU10).** Possible, but a separate driver project; QEMU's
  device has no 3D, so only the installed host could test it.
