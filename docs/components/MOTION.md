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

**Shape and hiding.** `taskbar` is a system-wide choice in `/etc/zenith/taskbar.conf`, set on
the `windows.taskbar` page or with `settings set taskbar`. Docked runs edge to edge along the
bottom row with square corners; floating is inset and rounded.

| Choice | Bar | Maximize fills |
|---|---|---|
| `dock-maximized` (default) | Floating; docked while the top window is maximized or fullscreen | Space above the docked bar |
| `floating` | Always floating | Space above the floating bar |
| `docked` | Always docked | Space above the docked bar |
| `maximized` | Floating; hidden while the top window is maximized | Whole screen |
| `auto` | Floating, only on demand | Whole screen |

One setting covers both, because a Meridian page holds one control and these are the
combinations worth having. `always`, saved by 0.0.35, reads as `floating`.

**Fullscreen.** F11 toggles the focused resizable window to the whole screen without a title
bar. A client gets the normal resize. Client surfaces stop at 1920x1000, so on taller screens
wallpaper shows below a fullscreen client.

**Motion.** The bar slides in over 160 ms and out over 120 ms, with its corners masked and
without its shadow, and changes shape by moving and stretching between the two rectangles while
its corner radius follows. It travels above everything else. A window change that moves the bar
plays with it in one animation, at the window's duration, and logs as `Resized bar=TaskbarDocked`.
A bar change on its own never cuts a window animation short: the bar moves at once instead.
Animations Off moves it at once too.

The system-app gate checks each mode, fullscreen, the reveal and overflow.

## Stage 5: login

**Look.** The card, fields and buttons use the shared `AppStyle` controls and theme sizes, the
same radii and control height as the desktop, with the logo beside the title. The focused field
shows a caret, a failed sign-in is in the danger color, and Desktop and Headless are two buttons
rather than `< >`. Branding from `login.conf` still applies.

**Pointer.** Controls show hover, and a pointer move over the same control redraws only under
the cursor, as the desktop does.

**Power.** Restart and Shut down ask first, in a panel that replaces the two buttons. Only a
click on the named button goes ahead. Escape, Cancel or any other click cancels, and other keys
are ignored while it is open, so typing a password cannot restart the machine. Power actions
have no keyboard path yet.

**Crossfade.** Login to desktop and desktop to login fade over 220 ms, scaled by the animation
choice. The stopping session copies its last frame; the next one fades from it on its first
draw and then wipes both buffers, so no session's pixels outlive the fade. A local console drops
the copy unused. The fade blocks the session for its length and gives up after two frames over
33 ms. Off, or too little free memory, skips it. It logs as `SessionFade`. Boot and power-off
have no fade yet.

The login gate checks confirmation and cancellation, and that both fades ran.

## Stage 6: inside applications

**Why scrolling was slow.** A native frame waited twice: 16 ms in the present, then 16 ms in the
empty poll after it, so a client topped out near 30 frames per second whatever it drew. Meridian
also re-read its settings on every draw, spending a general call budget of 4096 per minute.
The scheduling point every 16 service calls tested the general call counter, which graphics calls
do not advance, so whenever it rested on a multiple of 16 every poll and present slept. Under a burst
of wheel input that held a client at about 3 frames per second for up to 18 s. It now counts all calls.

**Pacing.** A present now waits only for what is left of its 16 ms interval, and the first empty
poll after a present does not wait. An idle client still sleeps 16 ms per poll, and presents can
never beat the interval, so no client gains anything by spinning. No ABI change: this helps every
existing client, and the present budget doubles to 7200 per minute so pacing can't reach it.

**Clock.** Call 628 returns monotonic milliseconds. It counts against the graphics budget and takes
no service gate. Kernels before 0.0.38 return -38, and the toolkit then has no motion.

**Toolkit frame clock.** `motion_percent` in `struct zui` defaults to 0, which is the old behavior,
so clients opt in. A client calls `zui_motion_frame` before drawing, and widgets set `animating` when
they need another frame. A pause counts as at most 50 ms of animation time.

**Smooth scrolling.** `offset` stays the logic's position and `visual` follows it. Each frame closes
a share of the distance that depends only on the time passed, about 160 ms for a notch, so a slow
machine covers more ground per frame, up to the 50 ms a frame may count, instead of scrolling slower. Controls register at the drawn
position, so a click always hits what is under the pointer. Dragging the thumb and replaced content
do not animate.

**Page transitions.** Meridian asks for one with call 629 before presenting the new page, and the
desktop crossfades over 150 ms. A new page rises 10 px into place; a changed sidebar only fades. The
compositor already keeps the client's frames, so the client needs no extra memory, where a client-side
fade would cost about 30 MiB: native address space is 8 MiB and each page is allocated at double size.
The desktop keeps two more frames per window that has faded. Frames presented during the fade become its
target, so hover still shows. A press, key or wheel turn ends it at once, and the client has the real page
from the start, so input is never lost. Older kernels refuse the call and the page appears at once. It
logs as `ClientFade`.

The system's animation choice scales both, and Off disables both. Frames in motion skip the settings
refresh. Meridian prints `MERIDIAN_SCROLL` with frames and milliseconds, and the settings gate checks
that and `ClientFade`, along with Off.

**Sign-in fade.** It removes one card from the same wallpaper, so 220 ms of ease-out barely showed.
It now takes 420 ms, easing both ways. The logout fade stays as it was.

**Limits.** Terminal, Files and Editor are kernel windows that scroll by whole lines or rows and
keep doing so. Welcome and System keep line scrolling, since their text is laid out by lines, but
gain the faster pacing. Popups and dialogs have no animation.

## Stage 7: boot and shutdown

**Boot screen.** Once graphics is up, the kernel shows the logo and a bar that fills as real startup phases
finish: disks, accounts, settings, network, session. The bar never guesses a percentage, and the phase name
shows where a slow boot is. The first phase has no text, because fonts live on the disk it mounts. The last
frame crossfades into the first session with the existing fade. Colors are fixed dark, because the
appearance is not loaded yet.

**Diagnostics stay reachable.** The serial log runs as before. `session=headless` (the boot menu's rescue
entry) and `nosplash` skip the screen. A failure before the first session paints the panic screen over it,
then waits as before, on R from serial or now the keyboard. If the boot screen itself fails, the boot goes on
without it. Firmware and the boot menu come before the kernel, so the screen starts after them.

**Shutdown.** Restart and Shut down fade over 200 ms, at most, to the logo and what is happening. The order is
the point: sync and unmount first, then the fade, then the power change, so a failed flush leaves the session
as it was, and nothing the display does can risk data. A Terminal `poweroff` runs on a worker, which asks the
session thread and gives up after a bound. Two slow frames, too little memory, an error, Off, or a console
session all skip it. It logs as `Farewell`. The disks are already unmounted, so that line reaches serial only.

The login gate checks the boot screen and its fade. The system-app gate checks the shutdown fade and its bound.

## Rendering follow-up: private foreground tiles

Q3/C4 extend the same master-tracked rendering work: fixed integer foreground
composition joins blur on private AP snapshots. UI mutation, material preparation,
ordering and device publication remain on the BSP. Exact clipped-region results
are published only after every tile completes; small/busy/single-CPU paths retain
the scalar implementation. The 0.0.47 regression fixes, resource budgets and
qualification boundaries are recorded in the [follow-up evidence](../archive/2026-11-07/display-memory-composition.md).

## Rendering phase 3: presents and backdrops

On VMware SVGA II a frame reaches the screen without the driver's `Display()`:
only damaged rows go to the visible framebuffer, scaled as written, with one
update command for the rectangle; other drivers keep the driver path. Material
captures sample only what changed beneath a window since its last capture.
`fps direct on|off` compares presents; `fps bench` splits the backdrop into
sample, blur and range. [Evidence](../archive/2026-11-09/rendering-phase3.md).

## Rendering phase 4: what really changed

Native clients present whole frames; the compositor now redraws only the rectangle
that differs (`ProcessSurface.CopyFrame`), and a translucent window over a change
only within its material's reach (`MaterialComposition.Reach`), falling back to the
whole window when a capture changes it everywhere. Animation frames restore and
present only the layers' old and new places. Boot and power screens draw at the
physical resolution, sized by screen height. `fps phases` splits composes into
lower and top passes; `fps verify` compares the scene with one composed from
nothing. [Evidence](../archive/2026-11-09/rendering-phase4.md).
