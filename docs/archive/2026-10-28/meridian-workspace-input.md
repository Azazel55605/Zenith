# Meridian workspace, viewport and background follow-up

Work ownership: [U1/D1/D2/D3/S1/Q2](../../ROADMAP.md). This records local evidence,
not an additional work tracker or a release authorization.

## Changes

Meridian now keeps categories/search beside the selected module, using shared
inset sidebar/detail containers. Welcome and System Information use the same
workspace primitive. All 43 Settings pages remain present; missing backends remain
explicitly WIP. KDE's [navigation guidance](https://develop.kde.org/hig/layout_and_nav/)
informed the persistent sidebar/detail organization.

Opt-in owned native viewports support resize/maximize/restore up to 1920x1000.
Requests have exact dimensions and a generation acknowledgement before frame stride
changes. Legacy fixed clients retain their ABI. Separate display modes, DPI,
orientation and borderless fullscreen are not implemented by this contract.

A system-wide Whole app background preference extends optional materials to
supported client canvases. Text/controls retain opaque foreground pixels; protected
authentication remains solid. Existing transparency/blur choices and saved whole
background bit survive legacy saves. Calls 608/609 keep version 1 and masks 0..3;
616/617 use version 2 and masks 0..7. No per-app override exists yet.

Adaptive caches reserve actual source/blur storage within the global 8 MiB budget;
large backgrounds can fall back to tint or solid. Large copyin buffers are reused.
Wheel input uses bounded pane-aware offsets, coalesces consecutive events without
crossing other input, and avoids redraw at unchanged bounds. Title dragging now
routes keyboard events, including release-frame keys; closing releases drag owners.

## Local validation

Debug kernel build passed. Exact final binaries:

- ELF SHA256: `49b412b1ac3525d326487bc8effe678e316912ee3c49edcf162041313310eece`
- ISO SHA256: `863957ae88b927ff5a6aa2e1ef0abc72e53f2aed22c70380fdf205f0a6a4425f`

770 host tests and 240 controller tests passed. The real C renderer/state machine
passes ASan/UBSan with extreme signed wheel deltas, all pages, whole-background
premultiplied pixels, large viewports and unsaved draft preservation on resize.
Settings and desktop-tools compile with GCC and Clang under warnings-as-errors.

Targeted disposable native checks:

- Settings: 170.045 seconds; whole background, rapid wheel navigation, keyboard
  during drag, maximize/restore, signed independent upgrade, permissions, saved
  state after reboot and independent ext2 audit passed.
- Legacy graphical clients: 48 checks, 155.626 seconds; ext2 audit passed.
- Native materials gate passed (85.634 seconds), including existing opaque-client
  and compositor constraints.

Evidence is retained locally in `obj/meridian-native-final`,
`obj/meridian-graphics-final` and their adjacent logs. Screenshots were inspected
for the composed whole-background appearance and maximized inset layout.

## Freeze investigation and limits

The operator reports installed 0.0.26 freezes during fast mouse-wheel category
scrolling and sometimes movement, without panic or reboot. Effects dependency is
not established. Local evidence does **not** prove that freeze fixed.

The released baseline survived one PS/2 mouse stress run with virtio keyboard:
768 wheel movements, 128 drag motions, subsequent keyboard navigation and shell
response passed. A separate baseline replay with both PS/2 keyboard and mouse
failed its post-wheel navigation-marker check; the captured serial still contains
scheduler activity. That check alone does not distinguish lost input, navigation
state or host starvation. It must not be described as a confirmed kernel hang or
as a passing legacy-input test. One earlier synthetic large pointer relocation also
failed acknowledgement; a later prepared native run passed. No driver fix is
claimed from these observations.

`tools/meridian-scroll-replay.py` preserves the original fixture and boots a copy;
`--ps2 --legacy` selects the released UI geometry and PS/2 devices. Fixture ELF
identity must match. The installed [operator checklist](../../testing/SETTINGS-VM-TEST.md)
remains required, especially sustained VMware wheel/drag behavior. Full clean
Release qualification, distributor delivery and installed candidate acceptance
have not been performed for this untagged follow-up. No new release tag is implied.
