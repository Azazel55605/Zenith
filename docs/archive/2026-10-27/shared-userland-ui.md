# Shared native UI foundation — 2026-10-27

## Scope

User accepted the neutral light/dark design with optional effects and six accents
before shared widget implementation. This slice supplies an independent client
renderer, fonts, tokens and basic controls. No kernel/runtime/theme package source
changed; Settings persistence, live resource loading and compositor effects remain
future work. [Library contract](../../USERLAND-UI.md).

## Implementation and host evidence

- `userland/ui` is freestanding C, with explicit clipping and bounded controls/text.
- `userland/ui-gallery` renders the actual controls in a protected 600x440 surface;
  local appearance choices are not saved system settings.
- GCC and Clang both build the static standalone ELF; existing gui-probe still builds.
- `python3 -B tools/test_ui_library.py` passes: actual renderer/input code under
  ASan/UBSan; clipping canaries, malformed/unsupported text, bounded reads, six
  accents in both modes, contrast of actual exported palette values, forward/
  reverse Tab, unfocused input, disabled/reset/focus-loss/drag-out handling and
  10,000 deterministic press/reset/release sequences. Host dark/light renders
  were visually inspected. Full Unicode/scaling and text editing are not implemented.
- Inter atlases are generated from existing licensed assets, with Pillow 13.0.0
  locally. Font source hashes are embedded in generated source; the license travels
  with the payload. Compiler intermediates are excluded from package contents.

## Native evidence

Local QEMU 11.1.2, explicit TCG, existing kernel SHA256:
`3e09627e7a6c518516f088e36b7eee0a40a498e6d905734e27b192bb090b0281`.

A disposable live install and signed gallery 0.1.0 installation passed. The final
0.1.1 fixture upgrade was built from the final library sources and installed on a
separate copy of that prepared disk with:

```sh
PATH="$PATH:/usr/sbin:/sbin" ZENITH_VALIDATION_ACCELERATOR=tcg \
python3 -B tools/ui-gallery-smoke.py /tmp/zn-ui-acceptance-6 \
  --disk /tmp/zn-ui-acceptance-4/setup/disk.img --version 0.1.1
```

Final gallery ELF SHA256:
`a9086f792e71e66388912864284f3cfda2044a07e31f93ed9682e69c73028410`.
These are local disposable fixture versions, not published releases.

Passed:

- Signed native bundle declares graphics ABI 1.0 and installs with its font license.
- No-display admission refuses the gallery with exit 1.
- Desktop displays readable native Inter text and both appearance palettes.
- Actual focused keyboard events switch Dark to Light.
- Actual compositor pointer events select Teal; selected-control pixels are observed.
- Clicking unavailable effects produces no activation.
- Escape closes the client with exit 0 and restores shell input.
- Guest poweroff unmounts all filesystems; exact installed kernel digest is unchanged.
- Independent `e2fsck -fn` exits 0 on the extracted root filesystem.

Images and serial/diagnostic evidence remain under `/tmp/zn-ui-acceptance-6`;
review PNG is `obj/ui-foundation-review/native-teal.png`. Those are disposable local
outputs, not tracked source or permanent evidence attachments.

The first screenshot locator required an unoccluded 600-pixel row; real host chrome
covers the outer pixel on each side. Observation now recognizes the 598 visible
interior pixels and derives the content origin. Reused fixtures also use unique
fetch destinations (existing files are correctly refused) and fail immediately on
an observed incorrect guest status instead of waiting for the expected status.
No input or mutation is retried by these observation fixes.

## Acceptance boundary

This is local client-library and compositor proof, not full kernel release
qualification, distributor publication, operator-installed acceptance, a production
Settings application or desktop migration. The gallery is not in the automatic
release registry. The current opaque ABI and trusted host chrome remain intact.
