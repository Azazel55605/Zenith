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
- **Any scene change ends it.** Input that dirties the scene, or a client frame for a window
  in the animation, jumps to the end state and composes normally.
- **Never required.** Allocation failure, a frame over budget twice in a row, reduced motion
  or duration 0 all take the existing immediate path. Session stop, panic and power-off
  cancel animations without waiting.
- **Memory.** Base and layers are bounded to one screen each. Layers are released when the
  animation ends.
- **Waking.** The desktop keeps drawing nothing while idle. While animating it must wake
  often enough to draw; the wake rate of the `Run()` halt loop is measured in this stage,
  and a timer alarm is added if it is too low.

Exit: every gate passes with motion enabled but no animation started, and `fps` shows no
idle frames.

## Stage 3: window animations

Durations follow the design language (100–180 ms).

| Action | Effect |
|---|---|
| Open | Fade in, scale 96% to 100% |
| Close | Fade out, scale 100% to 96%, using the last pixels; the process has already ended or been asked to close |
| Minimize / restore | Move and scale towards or from the taskbar pill, with fade |
| Maximize / restore / snap | Move and scale from old to new bounds, then the new content |

A native client takes a moment to draw at its new size. The animation ends on its last
frame and the normal path shows the client's next frame, as it does today.

Stage 3 also makes `style.animations` a real page: global speed and reduced motion, plus
per-operation controls only for effects that exist.

## Later stages

Each gets its own design before work starts.

4. **Taskbar overhaul:** theme-owned sizes, icons on pills, reachable overflow, no
   "Native · " prefix; a changeable work area, real fullscreen, duck and auto-hide; a
   taskbar settings page.
5. **Login:** the shared design and controls, hover, confirmation for Restart and Shut down,
   cursor-only redraws; crossfades for login and logout.
6. **Inside applications:** a toolkit frame clock, page transitions and smooth scrolling.
7. **Boot and shutdown:** logo and progress once graphics is up, diagnostics still
   reachable; a short shutdown fade that can never delay power-off.
