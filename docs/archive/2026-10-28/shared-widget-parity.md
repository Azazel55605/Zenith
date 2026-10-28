# Shared widget visual parity — 2026-10-28

D1 implementation evidence, subordinate to the [master tracker](../../ROADMAP.md).
The accepted [solid specimen](../../design/system-specimen.svg) remains the target.

## Implementation

- Shared neutral grouped mode selector (280×36 track, inset 136×28 segments).
- Circular 32px accent targets on a 44px cadence, geometric check, keyboard focus
  and visible selected/focused name. Arrow keys traverse choices; package-provided
  accent colors and on-accent contrast roles remain authoritative.
- Label/help rows and independent switches; unavailable effects remain disabled.
- Navigation uses borderless rows, outline chevrons and a restrained selected marker.
  Secondary panes/cards retain explicit material roles; content and text stay opaque.
- Host chrome uses a 44px bar, equal 36px decoration cadence, geometric centered
  line icons and 12px window rounding. Window controls and dragging remain host-owned.
- Appearance, gallery, Meridian and Welcome/System share the rendering changes.
  Save failure, revision conflict, readonly users and close/discard retain behavior.

## Qualification

Host renderer: real C code under ASan/UBSan, all twelve mode/accent palettes,
geometry/clipping, swatch palette overrides, keyboard/pointer/disabled/capture,
10,000 event sequences, dirty/save/discard and all 43 Meridian destinations.
`ZENITH_UI_EVIDENCE_DIR=obj/parity-review python3 -B tools/test_ui_library.py`
retains the actual renderer's PPM images; these are not new design mockups.

GCC/Clang freestanding builds and Debug ISO publish pass. Host C# tests: 747 pass.
Controller tests: 239 pass (the suite needs its disposable `/var/tmp` workspace).
Native Settings gate passes on fresh disposable disks, QEMU 11.1.2 / TCG:
123.2s, retained under `obj/parity-native-final`. It covers actual mode selection,
close/Keep editing/save, live application, independent upgrade, signed resource
palette, bounds, runtime metadata, Ctrl+F/WIP navigation, Welcome/System through
shell/menu, permission refusal, reboot persistence and an independent ext2 audit.
The separate native gallery gate passes in 60.5s (`obj/parity-gallery-native`):
keyboard mode, pointer swatch, unavailable effects and installed filesystem audit.
Native fixture package versions are test inputs, not new published releases.

| Final Debug artifact | SHA256 |
|---|---|
| ELF | `6415537f344d3cecf618b6df0e4d5c0cb7f89b07c4226c086b43e0e0b20cfaec` |
| ISO | `10056224973e1492c89ce975f2bab9f6976a2339a62575d6a9cbf505140cf714` |

Actual frame: `obj/parity-native-final/saved.ppm`; host palette contact sheets
and focused/disabled examples: `obj/parity-review`. These ignored local files are
qualification evidence, not package assets. Final logs: `obj/parity-ui-final.log`,
`obj/parity-host.log`, `obj/parity-controller-final.log`, `obj/parity-build-final.log`,
`obj/parity-native-final.log` and `obj/parity-gallery-native.log`.

An initial native screenshot assertion still expected the old square subtle banner.
The guest had reached the intended Bluetooth WIP destination. The assertion was
updated to verify the new rounded semantic panel, then the full targeted gate was
rerun successfully. No timeouts, expected status codes or guest behavior were weakened.

## Boundaries

This qualifies the current solid native control set, not the entire future product.
No alpha/blur protocol is introduced. Hover/move/leave transport, richer forms,
Unicode/shaping, general icon packs and scaling retain U1/D2 ownership. No global
pointer access or new graphics syscall is added. Full release gates, distributor
publication and the operator's installed-VM acceptance are separate Q2 evidence.
