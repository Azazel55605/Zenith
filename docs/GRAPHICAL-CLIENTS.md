# Protected graphical clients (ABI 1)

Work owners: **U1/U6/D2/D3** in the [master roadmap](ROADMAP.md). This page is a contract/procedure, not a separate work tracker.

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
| 8 | Scroll; code signed notch count, positive = up, clamped to -120..120 (a single notch is ±1) |
| 11 | Pointer motion after opt-in (call 627); content-local x/y, x=y=-1 on leave |

On focus loss, clients should discard locally held input state.

Key extra: Escape=1, Enter=2, Backspace=3, Tab=4, Left=5, Right=6, Up=7,
Down=8, Delete=9, Home=10, End=11, Page Up=12, Page Down=13, otherwise 0. These are Zenith codes, not
Cosmos enum ordinals. No key releases, right-button or text composition
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

## Interactive and metadata contracts

Call **605** `(owned_surface, 1, 0)` opts into interactive ABI 1. It requires a
live invocation-owned surface; a repeated enable is `-16`, a foreign/missing handle
is `-9`, malformed version/reserved fields are `-22`. While the surface remains
live, the original general/graphics request, presentation, file-transfer and raw
CPU tick quotas renew once per monotonic minute. Network lifetime limits remain
unchanged. Legacy clients retain lifetime quotas. Closing/revoking the surface
prevents renewal and permits only a two-second cleanup interval at service
boundaries. Full pointer checks, per-call byte bounds, descriptor/surface quotas,
fault/cancellation checks and the raw CPU watchdog still apply. This is not an
unrestricted background-process facility. Clock adjustment cannot renew quotas.

Call **612** `(writable_buffer, capacity, 1)` returns UTF-8 public system metadata
and its byte count, with capacity 1..4096. It validates the complete writable range
before collecting data, returns `-34` if too small, and writes no partial result.
No NUL byte is appended. It reports boot mode, kernel-generation ID when known,
hostname, active display geometry/refresh, uptime and configured local time/zone.
Unknown running release versions are explicitly unknown; installed package versions
are not substituted. This API contains no account secrets or device pointers.

Ctrl+letter graphical events carry a lowercase logical letter when Cosmos supplies
no text; AltGr and ordinary Unicode text remain unchanged. This makes native
application shortcuts usable with the existing layout maps.


Ctrl+letter events carry the logical lowercase letter when Cosmos provides no text;
ordinary Unicode/AltGr is preserved. Work and release acceptance: U1/Q2 in the
[master roadmap](ROADMAP.md). [Local targeted evidence](archive/2026-10-27/meridian-shell.md).

## Opt-in material frames (D3 first slice)

This extension has local software qualification and integrates with system-wide
preferences. Distributor/installed acceptance remains separate. ABI 1 client pixels
stay opaque; trusted decorations may independently use the host material policy.

- **607** `(out_caps, 32, 0)` queries the current invocation's graphical host.
  Returns eight little-endian u32: extension version **2**, feature bits **15**
  (coverage=1, regions=2, tint=4, blur=8), maximum regions **8**, corner radius **24**,
  total material area **131072 pixels**, host blur radius **4**, surface **640×480**.
  Headless/other-session users have no host; unsupported kernels return an error.
- Open **600** with the existing 48-byte request, `version=2, flags=1`, opts into
  premultiplied foreground coverage. All sizes/title/reserved checks still apply.
  Version 1 requires flags 0 and retains forced XRGB. Other pairs are rejected.
- **606** `(owned_handle, request, 48)` atomically presents a material frame.
  Header: u32 version=1, u32 flags=0, u64 foreground pointer, u64 exact byte count,
  u64 region pointer, u32 count, u32 reserved=0, u64 reserved=0. Count zero requires
  a null region pointer. Foreground is little-endian premultiplied **ARGB8888**:
  each RGB channel <= A; no unpremultiplied/hidden color is accepted.
- Each region is 32 bytes: eight signed i32 values `x,y,width,height,radius,role,
  effect,reserved`. Reserved=0, roles Navigation=3 / Panel=4 / Popover=5, effects
  Solid=0 / Tint=1 / Blur=2. Regions must fit the surface, not overlap, and obey
  the advertised total area/radius/count bounds. Pixels outside region bounding
  rectangles must be opaque. The host supplies the solid color outside rounded
  masks; clients cannot sample through excluded corners.
- Call 601 is rejected on format-2 surfaces; 606 is rejected on format-1 surfaces.
  Frames and region arrays are validated completely before replacing the owned
  snapshot, and copied into private host storage. Failed frames retain the last
  accepted image. Existing open/present/call/interactive budgets apply to 606/607.

The compositor captures lower layers **before** drawing this window/shadow,
uses a bounded separable box diffusion clamped to each region, caches unchanged
source/region combinations, then composites the tint and foreground separately.
No framebuffer address, screenshot, readback, user shader, opacity or arbitrary
sampling radius is supplied by the client. Text/opaque controls and solid content
are not faded with the material. Moving/overlapping windows sample the current
lower scene; the outer window mask still applies to the completed composition.

The opt-in `ui-gallery --materials` probe demonstrates 1 Solid / 2 Transparency /
3 Backdrop blur. The shared C library provides `zui_begin_material_frame` and
`zui_material_surface`: declare nonoverlapping material regions, then draw normal
content/controls/text on the foreground target. RGB drawing becomes premultiplied
coverage; legacy rendering is unchanged. On an explicit foreground target, ordinary
`zui_surface` calls for Navigation/Panel/Popover declare an eligible region before
clearing its coverage. Failed declarations keep the opaque fallback. Controls,
status/credential/document planes remain opaque; do not reinterpret finished RGB.

The host clamps every requested region through the shared desktop policy:
transparency off gives solid; blur off gives tint; resource/work/contrast limits
may further reduce the effect. The saved preference is unchanged. See
[policy and budgets](DESIGN-LANGUAGE.md#user-preference-and-fallback-rules) and
[608/609 preference services](NATIVE-SETTINGS.md#material-preference-extension).
Meridian negotiates format 2 and uses its sidebar as a material region. Chrome
and taskbar use the same host compositor; legacy app content remains opaque.
Protected authentication stays solid. Local evidence is recorded in
[desktop integration](archive/2026-10-28/desktop-material-integration.md).

## Pointer motion extension

Call **627** `(owned_handle, 1, 0)` opts a surface in to event 11; other clients never
receive it, and kernels before 0.0.30 return -38, so clients must treat hover as
optional. The host keeps one replaceable pending position outside the 64-entry queue,
delivered only when that queue is empty: motion cannot evict keys or clicks or cause a
reset. No motion is sent while a content drag holds the button (event 6 applies).

## Owned viewport extension

Native clients explicitly enable call **614** `(owned_handle, 1, 0)` to permit
window resize and maximize/restore. Legacy clients remain fixed-size. The minimum
client viewport is 600×480; the maximum is 1920×1000 pixels. This is window sizing,
not a display-mode or DPI/scaling service. Maximization uses the desktop work area,
retaining the taskbar and trusted title bar, within the advertised bound.

- **613** `(owned_handle, writable_snapshot, 32)` returns little-endian u32
  version=1, flags, requested width, requested height; u64 generation; u32 maximum
  width, maximum height. Flags: resize enabled=1, extended background regions=2.
- Event **10** reports requested width/height in X/Y. It is sticky/coalesced outside
  the ordinary input queue; close takes precedence. Clients query the current
  generation rather than treating an older event as authority.
- **615** `(owned_handle, readable_request, 24)` acknowledges u32 version=1,
  flags=0, width, height; u64 generation. Dimensions and generation must match the
  latest host request (-11 if stale). Invalid layout returns -22, invalid memory
  -14 and wrong handles -9. Only user/host window actions request a new size;
  clients cannot use this API to move/resize arbitrary windows.

Frame replacement retains private copies and strict byte counts. Until the client
acknowledges/repaints the requested size, the host uses a bounded placeholder,
never indexes an old frame with a new stride. New clients declare
`zenith-native-viewport-abi 1.0`. The native ELF mapping limit is 8 MiB to accommodate
a bounded static canvas; the signed file limit remains 256 KiB. Input, UID/session,
request/CPU and teardown protections continue to apply.

Resize-enabled format-2 clients can declare Content role **1** and up to 1920000
material pixels. Legacy material frames still have their original role/131072-pixel
bounds and unchanged 607 capabilities. The host authorizes Content effects only
when the saved Whole app background bit is effective. Controls and credential
surfaces remain opaque; no backdrop pixels or framebuffer pointers cross the API.
Wheel events now carry content-local X/Y, allowing independent pane scrolling.
