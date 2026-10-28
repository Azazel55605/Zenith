# Continuous workspace materials

[Master D1/D3/U1/Q2 ownership](../../ROADMAP.md). This is the visual follow-up to
[the workspace/viewport batch](meridian-workspace-input.md), prompted by inspection
of its whole-background screenshot against the accepted
[material specimen](../../design/material-specimen.svg).

## Findings and changes

The shared workspace rendered two full-height material rectangles with different
tints, then painted rounded sidebar/content cards over those rectangles. This
left a visible rectangular underlay around the content inset. Full-resolution
blur accounting could also exhaust the shared work/cache budgets and turn the
large content region into tint-only transparency.

Whole-background workspaces now declare one continuous neutral base material and
paint only a subtle rounded detail inset over it. The sidebar is no longer a
separate card. Solid mode also uses a continuous sidebar/base with one detail
inset. The trusted host removes its additional inner corner mask and title divider
for the opt-in workspace profile; legacy clients retain their existing behavior.

Large resizable client backgrounds and wide host chrome use 4x4 averaged samples,
separable diffusion and bilinear reconstruction. Title/sidebar use the same tint
and blur scale. Cache accounting uses actual sampled storage (12 bytes per blurred
sample); the existing 8 MiB cache pool and 393216 diffusion-sample budget remain.
Capturing the lower scene and compositing opaque foreground still scan bounded
full-size frames. Transparency-only mode remains full-resolution. Pressure can
still cause an explicit tint/solid fallback; saved preferences are never changed.
No GPU acceleration, framebuffer readback or new app authority is introduced.

## Validation

772 host tests passed, including reduced-grid diffusion, odd-sized/clipped samples,
opaque foreground preservation, unchanged-scene cache reuse and full-viewport
blur cache/work accounting. Real C UI ASan/UBSan checks passed; explicit pixel
checks reject a rectangular underlay outside the rounded inset. Settings and
Welcome/System clients compile under GCC and Clang. The Debug ISO build passed.

Final Debug ELF SHA256:
`440b94c02ac7b3d9c908855a6aef18599f23d9a3323bab64d65f336cd682e004`.
Final ISO SHA256:
`efa8510d827be400ab76e23210bf1ba2e47d11f77a896d2df0b1ac7402d733f8`.

The native materials gate on those final binaries passed in 84.585 seconds.
An earlier Settings screenshot check failed after assuming a 100-pixel title drag
that had not moved the window. The screenshot retained the original position;
the test sent the press/release through raw monitor commands rather than the
existing rendered button-transition acknowledgements. The helper now uses that
acknowledged path; it does not replay the settings operation or loosen the visual
assertion. The final prepared-fixture replay passed in 67.263 seconds, including
whole-background and maximized screenshots, effects off/on, wheel navigation,
keyboard during dragging, signed client upgrade, permissions, reboot persistence
and independent ext2 audit. Evidence is retained in `obj/material-followup-replay`
and `obj/material-followup-materials-final`, with adjacent logs.

Local screenshots/tests are not installed VMware acceptance. The separately
reported wheel/move freeze remains open; this visual correction does not establish
its cause or resolution. Full clean Release qualification and distributor/installed
acceptance remain Q2 requirements before releasing this untagged follow-up.
