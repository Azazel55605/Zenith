# Shared native UI foundation (client library v1)

[Design language](DESIGN-LANGUAGE.md) · [Graphical ABI](GRAPHICAL-CLIENTS.md)

This is a freestanding C library linked into independent native clients. It uses
existing opaque graphics ABI 1; it adds no kernel code, syscall, global input or
framebuffer access. It is the first widget foundation, not a replacement desktop
or a finished Settings application.

## Implemented

- Semantic neutral light/dark surfaces and text, six paired accent palettes;
  applications select the current palette without changing saved system preferences.
- Bounded clipped fills, rounded rectangles and antialiased text using embedded
  Inter A8 glyph atlases at 12/14/24 pixels (Regular/SemiBold as appropriate).
- Labels, buttons, Boolean toggles and choice controls; enabled/disabled,
  selected, pressed and visible keyboard focus states. No fabricated hover state:
  the current host does not send hover events.
- Tab/Shift+Tab wrap over enabled controls. Enter/Space activate the focused
  control; Ctrl/Alt chords are not consumed. Pointer release activates only after
  a matching enabled press. Focus loss/reset cancels held state.
- Application-owned values and positive unique action IDs; no hidden callback
  into a service and no settings mutation. At most 32 controls per UI context.
- Dirty-driven gallery presentation: idle polling does not continuously redraw.

`userland/ui/ui.h` owns the API; `ui.c` is host-testable renderer/input code.
`userland/ui-gallery` is a separate example executable that changes only its own
preview. Effects are visibly unavailable. Escape/chrome close exits; the host keeps
its existing invocation, ownership, request/presentation and lifetime limits.

## Build and test

Python Pillow is required **only** for the UI atlas build/test path, not other
native tools. The local implementation was checked with Pillow 13.0.0. The builder
uses the existing repository Inter files; it neither downloads fonts nor parses
TTF files inside the guest. Font licenses are carried in the gallery payload at
`/usr/share/zenith/ui-gallery/LICENSE-Inter.txt`. Inter remains under its existing
SIL Open Font License; this is a compiled atlas of that font, not a new typeface.

```sh
python3 -B tools/test_ui_library.py
python3 -B tools/build-userland.py /tmp/zenith-ui-payload --program ui-gallery
ZENITH_USER_CC=clang python3 -B tools/build-userland.py /tmp/zenith-ui-clang --program ui-gallery
PATH="$PATH:/usr/sbin:/sbin" ZENITH_VALIDATION_ACCELERATOR=tcg \
  python3 -B tools/ui-gallery-smoke.py /tmp/zn-ui-NEW
```

Use an unused short absolute directory for the native fixture. It builds/signs its
own gallery, installs a disposable guest disk, checks no-display refusal, observes
real pixels, keyboard appearance changes, pointer accent selection, disabled
controls, normal exit/shell restoration and independent ext2 integrity. It uses
existing `output-x64/Zenith.iso`/`.elf`; no kernel rebuild is required. It does not
alter an installed VM or publish a package. `zenith-ui-gallery` native bundles
require `zenith-native-graphics-abi 1.0`.

To reuse a prepared disposable fixture, add `--disk PATH --version NEWER_VERSION`;
the baseline is copied before boot and the newly built signed gallery is installed
on that copy. Existing download destinations are never overwritten.

The host suite executes the exact renderer/input sources with ASan/UBSan, clipping
canaries, contrast checks over all 12 actual palettes and 10,000 deterministic input sequences. Host pixel previews establish
layout only; the native fixture establishes actual compositor behavior. Neither
substitutes for installed/distributor acceptance or full release qualification.

## Resource and layout contract

Clients allocate their own width*height pixel array (maximum current API 640x480).
Initialize a `struct zui`, select appearance, add controls, forward every host
focus/reset/input event, consume returned action IDs, then redraw when dirty. Send
the complete frame through `zenith_gui_present`; handles remain application-owned.
The gallery demonstrates this sequence. Labels must remain valid while registered;
control IDs are unique and positive. Rectangles must fit the canvas at registration.

Text x/y denotes the top of a line box. The atlas retains a common font baseline,
with integer advances and antialias blending into opaque surface colors. Each call
reads at most 4096 bytes; labels use at most 256. The initial atlas covers printable
ASCII. Unsupported or malformed UTF-8 displays a replacement `?` without treating
continuation bytes of a complete sequence as separate glyphs. No shaping, IME,
font fallback, multiline layout or general text-edit field is claimed.

Current font sizes are fixed; scalable text, integer/fractional UI scale and layout
reflow still need explicit implementation and clipping acceptance. Do not treat the
fixed gallery as proof of accessibility/scaling support. Focus indicators and
selected labels remain distinct; essential labels use the design's legible tokens.

The library is statically linked; token/font changes currently require rebuilding
the client, **not the kernel**. This does not yet consume installed theme/font
updates live. A versioned resource loading mechanism and shared preference service
must connect independently updated themes/fonts before broad app migration.

## Next steps

1. Typed authorized Settings reads/writes, effective/saved/restart states and CLI parity.
2. Resource/theme preference loading and reusable layout/text-field/scroll controls
   as the first real Settings pages need them. Keep errors and unsaved edits explicit.
3. Scale/font coverage, keyboard interaction and independent package-update tests.
4. Compositor effects and user controls with performance-tested opaque fallback.

The development gallery has no release tags or public pipeline entry. The first
functional consumer is the [independent Settings Appearance page](NATIVE-SETTINGS.md),
which reads shared signed palette resources and saves preferences through typed services.

[Local native acceptance and exact artifact hashes](archive/2026-10-27/shared-userland-ui.md).
