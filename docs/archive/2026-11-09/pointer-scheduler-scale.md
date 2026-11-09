# Pointer, scheduler and fractional scale (Q3/C4/D2)

[Master roadmap](../../ROADMAP.md) · [Archive](../README.md)

## Report

On the installed VMware host with 0.0.50 the operator found CPU0 much better, but
moving windows and the pointer still felt laggy and choppy. They asked for the
scheduler change recorded in [rendering phase 4](rendering-phase4.md) and for
fractional scales (125%, 150%, ...) instead of a blocky 200%.

## Causes

- **The pointer arrived 80 times a second.** The Cosmos PS/2 driver's IntelliMouse
  knock sets sample rates 200, 100, 80 and leaves the last. On VMware the guest also
  never asked for the absolute pointer, so VMware fed relative PS/2 steps.
- **The desktop starved.** The kernel loop is the scheduler's idle thread: it runs
  only when no other thread is ready. Every pinned wait (sleep, timed monitor wait,
  contended mutex) marked its thread Sleeping/Blocked and halted in place, and a
  program's service thread halted *Running* while its slice ran on an AP. Nothing
  switched away until the next 10 ms timer tick. Two busy threads taking turns kept
  the loop off the CPU entirely, and when the loop halted it did so past queued
  threads. Measured with Observatory and Polaris at 1920x1200 on 8 CPUs (KVM): the
  desktop presented 0-7 frames a second while Observatory's graphs changed 30 times
  a second, and a 150 ms window animation got 3-7 of its frames (gaps 80-340 ms).
  The phase 4 CPU0 figures (23-30%) were partly a starved desktop doing no work.
- **Per-wake traces.** Once threads block properly, Cosmos printed a COM1 line for
  every monitor signal and block, hundreds a second, each byte a port exit on a VM:
  77% of CPU0 samples in one profile.

## Changes

- **Pointer.** 200 reports a second after the PS/2 knock (`PointerRateFixup`).
  On VMware the absolute pointer (vmmouse, `AbsolutePointer`): the guest pointer
  follows the host's exactly without grabbing; buttons and wheel come from it,
  relative PS/2 continues if it stops; restricted to CPL0. On VMware SVGA the device
  draws the pointer (`HardwareCursor`: alpha cursor, legacy AND/XOR elsewhere, FIFO
  cursor bypass 3 or registers): moving it never waits for a compose, present or
  tick, and its image is rendered from an outline at the device resolution
  (`PointerImage`, host-tested). QEMU keeps the pointer in frames by default because
  harnesses find it in screen captures (`fps cursor hardware|software` switches).
  `pointer=absolute` forces vmmouse on QEMU for tests (QMP absolute events verified:
  25% of each axis lands at 256,192 on 1024x768).
- **Scheduler** (`ParkSwitchFixup`, policy bits in `zenith_cpu_park_configure`,
  `cpu park on|off`): a parked non-idle thread's halt raises the scheduler interrupt
  with IF clear (STI's shadow covers HLT), so it switches away at once; the stride
  pick returns the idle thread when it has not run for 6 ms; the idle thread about to
  halt with queued threads hands them the CPU instead. Program slice waits block on a
  monitor that the kernel loop pulses after each completion doorbell (10 ms bound).
  The `[CV] Signal` and `[SCHED] BlockThread` traces are quieted in build copies,
  and so are thread lifecycle successes (create, run, entry, exit): printed from
  thread context, a switch can now cut them, and a command's own exit trace split
  its status line on the serial console (the first full validation's repository gate
  waited 600 s on `P4_KEY=0` hidden inside `[SCHED] ExitThread: …`). Failure traces stay.
- **SVGA register cache.** Presents and pointer moves reuse the device layout until
  a mode set invalidates it (eleven registers, two port exits each, per present).
- **Fractional scale (stopgap).** `DisplayLayout` holds a percent. 125/150/175% on
  VMware SVGA (x64) present the logical frame resampled bilinearly in native code
  (direct to the framebuffer, or into the driver path); damage widens by one logical
  pixel. Display ABI: snapshot flag 8 plus the percent at offset 320; requests may
  name a percent; the legacy scale field stays 1 or 2. Settings lists the percents
  when the flag is set; `display` and `display test W H PERCENT` match. Schema 2 of
  `/etc/zenith/display.conf` holds only fractional percents.

## Results (QEMU KVM, 8 CPUs, `smp=native`, Observatory and Polaris)

| | desktop frames/s | CPU0 | animation frames / longest gap |
|---|---|---|---|
| rules off (0.0.50 behaviour), traces quiet | 2-7 | 22-25% | 3-7 / 80-340 ms |
| rules on | 32-41 | 25-26% | 24-28 / 10-20 ms |

Local checks: 901 host tests, native C tests (workers, SVGA present and the new
filtered scaler), UI library/Settings tests, process broker, and repeated
material-switch runs for the AP race (see below). 150% at 1920x1200 verified by
screen capture (1280x800 logical, soft but clean).

## Limits and open work

- Fractional scales are filtered, not sharp. Sharp scaling needs the shell to draw
  at the device resolution with scaled metrics, a scale in the client ABI, scaled
  toolkit font atlases and icons, and new native program releases (next D2 project).
- The device pointer and absolute pointer are verified only on QEMU's devices; the
  operator's VMware host is the acceptance target (`fps` shows both states).
- The self-interrupt counts as a scheduler tick: the parked thread is charged a
  quantum of pass and run time (stride order is unaffected because its remaining
  pass is taken at block time), and the periodic tick traces print more often.
- The desktop is still the idle thread; it gets a turn per tick, not a priority.
