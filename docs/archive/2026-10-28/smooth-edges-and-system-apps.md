# Smooth edges and system-app styling — 2026-10-28

D1 follow-up / U2–U5 presentation evidence. [Master tracker](../../ROADMAP.md).

## Defects and changes

The first parity batch used a binary inside-circle test in native widgets: switch
tracks/knobs and swatches showed stair-step edges. The host clipped native/trusted
client drawing to a rectangle, allowing bottom content/status bars to repaint the
area outside the rounded frame. Painting a rounded background alone could not fix it.

- Native rounded shapes now blend 4×4 integer subpixel coverage. The no-SSE client
  build keeps its bounded rasterizer, opaque ABI and palette roles.
- The host masks the completed window, after all raw client copies and trusted
  drawing, against the actual backdrop captured after shadow rendering. A reusable
  16 KiB corner buffer avoids full-frame allocations. Pure regressions cover all
  four corners, intermediate coverage, clipped windows, repeated layers and square
  maximized geometry. Native Settings checks both bottom corners too.
- Terminal uses the shared canvas/selection roles and retains ANSI/default intent
  in scrollback, adapting existing output to light/dark without rewriting text.
- Editor uses shared New/Open/Save actions, themed canvas/gutter/selection/status,
  consistent insets and a pointer selection region below the toolbar. The chrome
  close action now shares its unsaved-change guard with Ctrl+Q.
- Sunrise uses shared fields, navigation and primary/neutral/destructive states,
  preserving disk identity, explicit erase consent, secrets and worker admission.
- Recovery Welcome/System use the same palette/type/insets; native counterparts
  already consume the shared library. Files body/navigation was intentionally untouched.

This is visual migration of existing windows. Editor/Terminal/Sunrise remain trusted
until the native process/PTY/typed service migrations U2–U4 are complete. No alpha,
transparency or blur capability is claimed. D3 is the next selected rendering slice.

## Validation

- Real client C rasterizer/state tests pass ASan/UBSan, including intermediate
  edge coverage, symmetry, clip canaries, all twelve palettes and 10,000 input
  sequences. Images retained in `obj/smooth-review`.
- C# host tests: **754 passed** (`obj/smooth-host.log`). Debug publish passes
  (`obj/smooth-build-final.log`).
- Common controller checks: **239 passed**, including the new native gate order
  and preservation of acceptance for older immutable source trees.
- Settings/native corner-mask gate passes, **125.2s**, QEMU 11.1.2 / TCG. Covers
  pointer/keyboard, close/discard, live apply, resource/client update, permissions,
  actual metadata, WIP navigation, Welcome/System dispatch, reboot and ext2 audit.
  Evidence: `obj/smooth-native-final`, `obj/smooth-native-final.log`.
- Guided GUI installation passes **38/38**, **124.8s**, including rejected empty
  credentials, explicit erase consent, hidden secrets, independent reboot/login,
  private shadow/home ownership and ext2 audit. Evidence: `obj/smooth-installer-final`.
- The new `tools/system-app-style-smoke.py` passes both modes, actual pointer Save,
  guarded chrome close with subsequent persisted edits and ordinary teardown.
  Evidence: `obj/smooth-apps-qualified`. It is part of the common native gate plan
  when present in the pinned source, not a substitute for the remaining gates.

| Final Debug artifact | SHA256 |
|---|---|
| ELF | `37918b5527f052e79611a438f9d190681f8a38bd1aa37814b5986481f22d51a9` |
| ISO | `28939ca0cc4ed458574d17180eee94e8991ac431694411c9c1d3f85b97678c73` |

The app fixture initially mistook a long accent frame edge for its Save button;
its anchor now requires the actual button width and canvas. The edit probe also
now checks the final line rather than assuming Save omits a trailing newline.
Neither correction replays an operation or weakens unsaved-close expectations.
The complete focused fixture then passed; product bugs were not hidden by retries.

Full pre-tag acceptance, distributor publication and the operator's installed VM
remain separate evidence.
Retained files are ignored local evidence, not package assets. Nothing is published
by these commands; production signing keys and actual disks are not used.
