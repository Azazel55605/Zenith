# Protected graphical clients (ABI 1)

This first API uses the existing trusted desktop as compositor. A separately
packaged static x64 ELF submits private software pixels and consumes bounded
window events. No framebuffer/device address, GUI object, kernel callback, global
input stream, trusted dialog or settings mutation is exposed.

## Ownership and lifecycle

Admission requires an active graphical desktop and the native invocation's UID
matching that desktop's logged-in account. Environment variables do not select
identity. No display/greeter/local-console host means unavailable. A host is bound
to its login generation; changing identity or stopping the session revokes all
surfaces and pending opens. An old process cannot attach itself to a later login.

One live surface per invocation, four simultaneously admitted per desktop, at
most four successful opens per invocation. Closed handles are never reused within
an invocation and cannot address another invocation's surfaces. Handles, buffers
and pending events are reclaimed on native exit, fault, timeout and cancellation.
The shell retains its existing process/job/session-transition rules.

The process worker only owns buffers/queues. Desktop tick alone opens windows or
mutates WindowManager. Decorated windows are labelled Native, fixed size, movable
and minimizable. The desktop owns focus, stacking, clipping and close controls.
Only focused window key presses and routed content pointer events reach a client.
No clipboard, arbitrary window enumeration, fullscreen or input grab exists.

## Wire interface

All calls use the existing x64 protected service yield. Reserved fields and the
entire readable/writable user range are checked before side effects. Calls return
negative error codes; they do not export managed addresses or exception objects.

| Call | Arguments | Result |
|---|---|---|
| 600 open | request pointer, 48, 0 | invocation-local handle |
| 601 present | handle, readable pixels, exact byte count | 0 |
| 602 poll | handle, writable event, 24 | 1 event, 0 empty |
| 603 close | handle, 0, 0 | 0 |

The 48-byte little-endian open request is: version u32 (=1), flags u32 (=0),
width u32, height u32, title pointer u64, title byte length u64, two reserved u64
(=0). Dimensions are 160..640 by 100..480. Title is 1..80 printable ASCII bytes.
No client-chosen screen position or privileged decoration flags exist.

Pixels are tightly packed row-major XRGB8888, four bytes per pixel, stride width*4.
The high byte is ignored and forced opaque. The complete buffer is copied into
owned compositor storage; no mutable client pages remain mapped into the display.
Presentation pauses the caller for 16 ms and permits at most 3600 calls per
invocation. The GUI service request budget is 65536, separate from ordinary file
and network budgets. Existing native CPU/context limits still apply. This is a
bounded first client profile, not an unlimited application runtime.

Poll never waits holding the native filesystem service gate. Empty polls yield
for 16 ms. Its 24-byte event contains six signed i32 fields:
`type, code, x, y, modifiers, extra`.

| Type | Meaning |
|---|---|
| 0 | No event; all fields zero |
| 1 | Sticky close/revocation request; client should exit |
| 2 | Focus changed; code 1 focused, 0 unfocused |
| 3 | Key press; code UTF-16 character, modifiers Shift=1/Control=2/Alt=4 |
| 4 / 5 / 6 | Left pointer press/release/drag; code 1, content-local clamped x/y |
| 7 | Input queue overflow; discard any locally held key/button state |
| 8 | Scroll; code signed delta clamped to -120..120 |

On focus loss, clients should discard locally held input state.

Key extra: Escape=1, Enter=2, Backspace=3, Tab=4, Left=5, Right=6, Up=7,
Down=8, Delete=9, Home=10, End=11, otherwise 0. These are Zenith codes, not
Cosmos enum ordinals. No key releases, hover/right-button or text composition
protocol is provided yet. The queue has 64 entries; overflow clears stale input,
emits reset then the newest event. Close is sticky and cannot be evicted.

Relevant errors: -9 bad/closed handle, -14 invalid user range, -19 no matching
active desktop or desktop admission limit, -22 malformed request/size/title,
-24 invocation open limit, -27 presentation budget, -32 surface closed.

## Probe and validation

The reusable C header is `userland/include/zenith_graphics.h`; its typed wrappers
retain the exact wire contract, with no libc dependency.

`tools/build-userland.py --program gui-probe DESTINATION` builds the independent
client. Signed package zenith-gui-probe declares zenith-native-graphics-abi 1.0;
older kernels must reject it. Run with `native run gui-probe`, or --probe,
--bounds, --exit, --fault and --version. This is a test application, not Settings.

Host tests cover wire validation, private handles, buffer copies, event overflow
and cleanup. `tools/native-graphics-smoke.py NEW_DIRECTORY` installs its own signed
packages on a disposable disk, tests no-display refusal, renders through the real
compositor, routes keyboard/pointer/close, tests cleanup and upgrades the executable
while kernel bytes remain unchanged, then audits ext2. The current-source release
plan includes it alongside all existing gates; old immutable tags without this
harness retain their original plan. A build/host pass alone is not graphical proof.

## Next boundaries

Settings needs narrowly authorized typed read/change services, restart semantics
and shared client widgets/tokens. Dynamic resizing, damage rectangles, scalable
text/font resources, a reusable SDK, launcher lifecycle and long-lived app budgets
need separate increments. The compositor/login/session remain trusted kernel-linked
code; this API does not claim the desktop migration is complete.

The disposable graphical gate uses an explicitly selected virtio mouse and keyboard;
legacy PS/2 coverage is separate. [Distributor input-profile evidence and no-build replay](archive/2026-10-27/graphical-input-profile.md).

Synthetic button transitions are held until a rendered cursor movement confirms
the desktop sampled them; release is acknowledged and the pointer position is
restored. Fixed short press/release delays are not a valid sampling guarantee on
slower emulators. This is validation pacing, not a change to the client ABI.

A successful present means the private pixel copy was accepted; it is not a
synchronous screen-present barrier. The acceptance harness observes a complete
rendered frame containing the probe before locating its controls. Surface-open or
focus logs alone do not establish that those pixels are visible.

The first [shared client UI library](USERLAND-UI.md) supplies opaque software
widgets and Inter text without changing graphics ABI 1.
