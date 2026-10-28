# Desktop material integration — 2026-10-28

Work owner: [D3](../../ROADMAP.md#core-delivery-work). This is a dated evidence
record, not a second tracker. It completes the software integration following the
[material foundation](material-rendering-foundation.md).

## Result and contracts

- Meridian saves mode, accent, transparency and blur in one revision-checked
  system-wide draft. Transparency and blur are independent bits; blur remains
  saved while transparency is off. Root-only write/ordinary-user read, failed
  persistence and stale edits retain the existing authority and behavior.
- Schema 1 reads as solid; schema 2 adds MATERIALS=0..3. 608/609 expose the saved,
  effective and available masks/reason and atomic full save. Legacy 610/611 sizes,
  reserved bytes and semantics stay intact; legacy saves retain material bits.
  Old kernels reject schema 2 and use their fallback without overwriting it.
- Host decorations and taskbar share the client software compositor and policy.
  The decoration/content divider disappears with transparency enabled. Content
  has a host-owned 12px top-left curve in every mode; client coordinates and
  hit targets do not change. The completed outer corner mask still prevents bleed.
- Meridian negotiates format 2; its Navigation role produces an actual material
  region. Content, controls/status and text remain foreground/opaque. Existing
  ABI 1 clients stay forced opaque. Shared C semantic surface calls can declare
  eligible regions only on an explicit foreground target; failure stays solid.
- Primary/secondary text contrast sets host tint opacity (210/220 through 244)
  or falls back to solid. An 8 MiB cache lease pool and 393216-pixel per-compose
  blur budget bound software resources/work. Oversize/cache denial gives solid;
  blur work overflow gives tint. Closing/session teardown releases leases.
  Renderer fallback reports a reason without rewriting saved preferences.
- Greeter/recovery, installer credentials and terminal hidden password reads stay
  solid. Taskbar configuration and Files redesign remain outside this change.

## Local qualification

The current Debug ELF is
`b75d49d18d7d48dd151fc53a6265ff508866ec0074443915e09311325bb6fe88`;
ISO SHA256 is `1cb6bf3e03f6ed54249ab35ceb5cc8716f5ea70f80430c98cd7f5cfa72e5b552`.
Disposable local QEMU uses TCG. Generated evidence remains in ignored `obj/`.

- Host: 765 tests passed, including schema/atomic save and legacy preservation,
  strict wire requests, retained blur, contrast/work fallback, bounded cache
  release/non-resurrection and inner-only corner masking.
- Real C UI under ASan/UBSan: settings save/draft/readonly effects, all 43 Meridian
  pages, input sequences, premultiplied coverage and automatic region fallback.
  Settings built with GCC and Clang.
- Native material gate in dark and light: signed installed foreground/material
  protocol, solid/tint/blur busy backdrop, opaque content/control preservation,
  moving/retiring surfaces and independent ext2 audit.
- Native Settings: save/apply/reboot, root permissions, stale/dirty behavior,
  independent package upgrade, actual taskbar/chrome/inner and outer corner pixels,
  headless saved-versus-unavailable renderer status, actual off/on divider/panel
  pixels with retained blur, identical content geometry and translucent Meridian
  sidebar versus opaque content. Final full Settings fixture passed in 134.084s; the focused prepared replay
  with rendered navigation identification passed in 46.892s.
- Existing graphical-client gate: 48 checks and independent ext2 audit passed,
  including input isolation, close/minimize/cancel/upgrade and UID isolation.
- Controller: 239 tests passed; 91 documentation files/links, 43 master work IDs,
  43 Meridian pages and single-tracker boundaries checked.
- Existing Terminal/Editor: both palettes, pointer Save, guarded chrome close and
  normal teardown passed against the new compositor.

## Acceptance boundary

This is a bounded CPU rendering path. It does not implement GPU acceleration,
full damage tracking, arbitrary-size effects, per-user preferences or elevation.
Budgets cap cache leases and blurred area, not all OS allocations or wall-clock
frame latency. D2 owns display resize/scale; Q2 owns distributor and real installed
VM acceptance, sustained performance and target-device contrast/repaint review.
No packages or tags are published by this implementation batch. Use the
[installed checklist](../../testing/SETTINGS-VM-TEST.md) for operator acceptance.
