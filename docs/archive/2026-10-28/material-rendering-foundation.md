# Material rendering foundation — 2026-10-28

D3 first implementation slice, subordinate to the [master tracker](../../ROADMAP.md).
Wire and ownership contract: [graphical clients](../../GRAPHICAL-CLIENTS.md#opt-in-material-frames-d3-first-slice).

## Implemented boundary

Explicit format 2 admits private premultiplied foreground and nonoverlapping
host material intentions, with fixed counts/areas/radii and full atomic validation.
The host owns palette tint, opacity, radius-4 separable box diffusion and the lower
scene snapshot. Only the declared rectangles are sampled, before drawing this
window or its shadow; cached filtering is reused for unchanged background/regions.
Opaque content/controls and full-coverage text are not faded. Window masking and
session/UID ownership remain authoritative. Legacy 600/601 frames are unchanged.

The shared C renderer can target premultiplied coverage and declare regions without
changing its control/text layout. An explicit `ui-gallery --materials` probe switches
between solid, tint and blur; it reads the active palette at launch. Existing apps
and protected login/recovery remain opaque; system-wide settings are still WIP.
The public feature query is not a claim of production/hardware qualification.

## Local evidence

- **760 host tests pass**: protocol versions/reserved lengths, malformed and
  overlapping rectangles, area/radius/role/effect bounds, premultiplication and
  opaque-outside-region rules, failed-frame atomicity, copied ownership, preserved
  foreground, clipping/movement and unchanged-source cache behavior.
- **239 controller tests pass**, including the material gate in the common native
  plan and preservation of older immutable source-tree acceptance.
- The real C renderer passes ASan/UBSan with fractional premultiplied edges and
  all existing opaque control tests. GCC/Clang build the standalone material probe.
  Debug ISO/ELF publish succeeds.
- Native material probes pass in both **dark and light** on QEMU 11.1.2 / TCG,
  including a busy text backdrop. They reject invalid kernel pointers/roles/alpha,
  observe actual solid/tint/blur frames, verify full-coverage text and opaque
  controls/content remain unchanged, move the blurred window, close normally and
  audit installed ext2 independently. No service status alone substitutes for a
  composed-frame check: each transition waits for pixels different from the prior
  material frame. The first probe found that presentation acknowledgement can
  precede actual composition; this wait was corrected before final qualification.
- Opaque compatibility: **48 native graphical checks pass**, including lifecycle,
  permission/session/fault/cancel/upgrade and ext2 audit. The full targeted Settings
  gate also passes, including save/discard/live apply, independent upgrade,
  permissions, reboot persistence and filesystem audit.

Logs: `obj/material-host-final.log`, `obj/material-ui-final.log`,
`obj/material-controller-final.log`, `obj/material-build.log`,
`obj/material-dark-final.log`, `obj/material-light-final.log`,
`obj/material-legacy-graphics.log`, `obj/material-legacy-settings.log`.
Frames/fixture disks: `obj/material-dark-final` and `obj/material-light-final`.
These are ignored local evidence; no package/server or installed-VM acceptance
is inferred. Fixture keys and package versions are disposable test inputs.

| Debug artifact | SHA256 |
|---|---|
| ELF | `2aabaafaf530124c1eebd91265091c75a7a366ac1f5b852c0e72ad059b1d85b2` |
| ISO | `bae264ad5adff29efffb11f0c71cc11b73a22b5130bfb4c7b46b4c822ce28037` |

## Remaining D3 work

Saved/effective/capability preference state and proper user controls; host chrome
and shell material integration; automatic
opaque fallback under resource/contrast pressure; wider light/dark/accent/busy
background tests; minimize/restore/overlap and long-running update acceptance;
actual performance/latency/memory limits across supported hosts; finer damage/cache
invalidation and D2 scaling/resizing integration. General app activation follows
these contracts. This is not a GPU backend, Gaussian blur or desktop-wide enablement.
