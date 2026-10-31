# Desktop motion — design

**Operator-approved direction** (2026-10-30): window snapshots, delivered in the stages
below. Work owner: **W1**, with **U1** for the toolkit and **S4** for preferences in the
[master roadmap](../ROADMAP.md). State and evidence stay in the roadmap.
[Design language](../DESIGN-LANGUAGE.md) · [Graphical ABI](../GRAPHICAL-CLIENTS.md)

## Problem

`Desktop.ComposeScene` redraws everything for any change: wallpaper, every window, blur
captures and the taskbar. It then copies the result to `_frame` and the display. No window
keeps its finished pixels, so an animated frame would cost a full recompose. Nothing has a
frame clock: the desktop draws only when input or a client changes something.

## Measuring the budget

`fps reset` starts a measurement and `fps` reports scene frames, average and worst frame,
worst compose and present, and frames over 16 ms and 33 ms. Timings from the installed
VMware desktop set the frame rate in stage 3. QEMU TCG timings do not predict them.

## Compose cost

Before any animation depends on it, a scene frame must stop growing with the window count.
The compositor keeps two caches, both pure speed-ups with the old path as fallback:

- **Lower scene:** wallpaper and all windows below the top one. It is reused until a lower
  window's content, bounds, focus, title or hovered button, the stack, or the theme changes.
  The blur budget it spent is restored, so the top window looks the same either way.
- **Finished material pixels:** once a material's backdrop repeats, it repaints by row copy.
  They have their own 16 MiB budget; running out only costs speed.

Materials paint in row loops with an SSE2 path, tested identical to its scalar fallback. The
scheduler saves XMM registers across task switches; nothing enables or saves AVX, so wider
vectors need kernel work first. Only the bench below covers Terminal windows; a native client
adds its per-pixel foreground blend.

`fps bench` logs the cost of a full redraw, a top-window redraw and a drag, and how many
pixels of the cached scene differ from a full redraw. The system-app gate requires zero after
a lower window changed.

| 1280x800, KVM, all materials, Debug kernel | 2 windows | 4 windows |
|---|---|---|
| Before: any scene change | 17.0 ms | 37.9 ms |
| Lower window changed | 5.0 ms | 9.2 ms |
| Top window changed | 2.0 ms | 2.0 ms |
| Top window dragged, scalar | 12.0 ms | 11.8 ms |
| Top window dragged, SSE2 rows | 3.3 ms | 3.3 ms |

Dragging rebuilds that window's blur and repaints it live; the window count no longer matters. The SSE2 row is measured at translucency 100.

## Stage 2: layers and clock, no visible change

An animation needs two images, and each is produced once:

- **Base:** the scene without the animating window.
- **Layer:** that window's finished pixels, copied from a complete scene, including its
  blur, so the blur code does not change.

Each animated frame copies the base into `_frame`, draws the layer at the current
position, scale and opacity, then draws the cursor and presents. No window or application
redraws.

| Unit | Purpose |
|---|---|
| `Motion` | Pure: elapsed time, duration and easing give progress 0–1. Host tested. |
| `WindowLayer` | Owned bounded pixel copy of one rectangle, with blend and nearest-neighbour scale. Host tested. |
| `MotionPlayer` | In `Desktop`: holds base and layers, advances them and reports when finished. |

Rules:

- **State changes first.** The window manager applies the real state at once, so hit tests,
  focus and input never wait for an animation.
- **Only input ends it early.** A press, key, wheel turn or drag jumps to the end state. Button
  releases, hover changes and background updates, such as a blinking terminal cursor, wait:
  the full compose at the end catches up.
- **Never required.** Allocation failure, a frame over budget twice in a row, reduced motion
  or duration 0 all take the existing immediate path. Session stop, panic and power-off
  cancel animations without waiting.
- **Memory.** One base and one layer, each at most one screen, allocated on first use and kept
  for the session. An animation does not start unless free memory covers them with a 16 MiB
  reserve.
- **Waking.** The desktop draws nothing while idle. While animating it relies on the `Run()`
  loop, which the 10 ms scheduler tick wakes at the latest.

`fps motion` fades the focused window in over 150 ms, logs the frames drawn, worst frame and
how it ended, and shows the same under `fps`. It proves the path on real hardware before any
effect depends on it. The system-app gate runs it and checks the scene returns to the same
pixels, apart from the clock and the cursor.

## Stage 3: window animations

Durations follow the design language (100–180 ms).

| Action | Effect |
|---|---|
| Open | Fade in, scale 96% to 100% |
| Close | Fade out, scale 100% to 96%, using the last pixels; the process has already ended or been asked to close |
| Minimize / restore | Move and scale towards or from the taskbar pill, with fade |
| Maximize / restore | Move and scale from old to new bounds, then the new content |

Edge snapping does not exist yet; it gets this animation when it does. Only the top window's
changes animate, and the latest change wins. Starting one reuses the cached lower scene.

A native client takes a moment to draw at its new size. The animation ends on its last
frame and the normal path shows the client's next frame, as it does today. A new native
window opens with its first frame, or empty after 500 ms, so it does not fade in blank.

Taskbar pills keep opening order instead of stack order, so a minimizing window has a fixed
place to go.

`style.animations` offers Normal, Fast (60%), Slow (150%) and Off (reduced motion). It is a
system-wide choice in `/etc/zenith/motion.conf`, also set by `settings set animations`, and
applies to the next window change. Per-effect controls are not built.

Each animation logs its name, frames, worst frame and how it ended. The system-app gate plays
all five changes, checks the resulting window state, and checks Off plays nothing.

## Stage 4: taskbar

**Look.** Sizes live in the theme: a 48 px bar, a 36 px control height and the shared radii.
Each pill has a 16 px icon and its title, cut with "..." inside the pill. Active is the accent
tint with an accent bar, hover is the neutral hover, and minimized is muted, so the three no
longer look alike. Client titles lose the "Native · " prefix. The clock is centered.

**Too many windows.** Pills shrink evenly to 120 px, then the rest move to a `+N` button beside
the clock that lists them. The focused window always keeps a pill. Every window stays reachable.

**Hiding.** `taskbar` is a system-wide choice in `/etc/zenith/taskbar.conf`, set on the
`windows.taskbar` page or with `settings set taskbar`:

| Choice | Bar shows | Maximize fills |
|---|---|---|
| `always` | Always | Space above the bar |
| `maximized` | Unless the top window is maximized | Whole screen |
| `auto` | Only on demand | Whole screen |

In every mode the bar ducks for a fullscreen top window, returns when the pointer reaches the
bottom three screen rows, and stays 400 ms after the pointer leaves. An open launcher or `+N` list
keeps it up, and dragging a window to the bottom edge does not raise it.

**Fullscreen.** F11 toggles the focused resizable window to the whole screen without a title
bar. A client gets the normal resize. Client surfaces stop at 1920x1000, so on taller screens
wallpaper shows below a fullscreen client.

**Motion.** The bar slides in over 160 ms and out over 120 ms, with its rounded corners masked
and without its shadow. A window change at the same moment takes priority and the bar moves
at once. Animations Off moves it at once too.

The system-app gate checks each mode, fullscreen, the reveal and overflow.

## Later stages

Each gets its own design before work starts.

5. **Login:** the shared design and controls, hover, confirmation for Restart and Shut down,
   cursor-only redraws; crossfades for login and logout.
6. **Inside applications:** a toolkit frame clock, page transitions and smooth scrolling.
7. **Boot and shutdown:** logo and progress once graphics is up, diagnostics still
   reachable; a short shutdown fade that can never delay power-off.
