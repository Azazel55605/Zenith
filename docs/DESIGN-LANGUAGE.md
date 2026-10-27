# Zenith design language

**Design baseline v0.1 accepted by the user on 2026-10-27.** This defines the
shared visual and interaction language for widgets, Settings and the desktop.
The palette/font foundation and selected controls exist. The full component geometry and materials are not yet at preview parity; see the explicit gap table below.
[Visual specimen](design/system-specimen.svg) · [Master work D1/D3](ROADMAP.md#core-delivery-work)

## Agreed direction

Zenith should feel neutral, calm and softly layered. Content supplies the identity;
color and depth explain interaction. Avoid tinting every surface with the accent.
The same language applies to login, desktop chrome, native applications and future
floating/tiling sessions. Headless settings expose the same preference semantics.

- Define light and dark together. Dark is the initial default.
- Offer six accent presets first; allow any user-selected accent through a color
  picker later. Accent choice is independent of light/dark appearance.
- Allow users to enable transparency/translucency and blur. Effects are optional,
  with a complete opaque appearance when disabled or unavailable.
- Settings and CLI implementation are dependencies of configurable delivery.
  This agreement is not a request to pretend those preferences already work.

## Foundations

The following measurements and palettes form the accepted visual starting point,
not a frozen widget ABI. The specimen illustrates them in opaque mode.

| Foundation | Baseline rule |
|---|---|
| Typography | Existing packaged Inter Regular/SemiBold; Hack for terminal/code |
| Type scale | 12 metadata, 14 labels/body, 18 section titles, 24 page titles; logical pixels |
| Spacing | 4-unit base; common steps 4, 8, 12, 16, 24, 32 |
| Geometry | 8 control radius, 12 panels/windows; consistent radii within a role |
| Controls | 36 minimum normal height; 32 compact only in deliberate dense views |
| Layout | 16 content inset; 24 between sections; labels above fields; align values |
| Icons | One simple outline family; 16/20/24 sizes, consistent stroke and optical weight |
| Depth | Surface tone first, border second, subtle shadow where windows overlap |
| Motion | Short 100–180 ms state transitions when supported; immediate reduced-motion path |

Logical units must scale together; do not enlarge glyphs while leaving fixed-height
fields that clip them. Touch targets, assistive technology integration and alternate
input need separate implementation/acceptance; these draft sizes are not certification.

### Semantic palette

Use roles, never a widget's hardcoded color. Neutral surfaces should stay neutral
when the accent changes. Borders must not be the sole cue for a focused control.

| Role | Dark | Light | Use |
|---|---|---|---|
| Desktop | `#151619` | `#E8E9EC` | Background behind windows |
| Surface | `#202125` | `#FAFAFB` | Application content |
| Raised | `#2A2C31` | `#FFFFFF` | Fields, menus, nested panels |
| Subtle | `#25262B` | `#EFF0F3` | Navigation and grouped rows |
| Border | `#45474F` | `#CFD1D8` | Decorative separation, not sufficient for every control |
| Text | `#F1F2F4` | `#202126` | Primary labels and content |
| Secondary text | `#B4B7C0` | `#5C606B` | Descriptions; still readable body text |
| Focus | Derived accessible accent | Derived accessible accent | Outer focus indicator |

Disabled content may be quieter, but must remain distinguishable from editable
content and loading states. Small essential text targets at least 4.5:1 contrast;
meaningful control indicators and focus indicators target at least 3:1 against
adjacent surfaces. Verify actual composited colors, not just source tokens.

### Accent and status

Initial preset names: **Blue, Violet, Rose, Amber, Green, Teal**. Keep selection,
primary actions, active controls and focus consistent. Use an accent-derived tint
for selected rows; retain a check, indicator or text so selection is not color-only.
Use separate success, warning and danger roles, with icons/text. A rose accent must
not make every selected item look like an error; destructive actions require an
explicit label and confirmation where recovery is costly.

Baseline accents (dark/light): Blue `#8BAAFF`/`#3157B7`, Violet `#B6A0FF`/`#7042BE`,
Rose `#F39DB8`/`#AE325D`, Amber `#E7BB70`/`#886010`, Green `#8BCBA4`/`#277347`,
Teal `#7ECAC7`/`#197571`. These are semantic variants of one choice, not six identical
hex values forced into both appearances. Preview custom colors before applying;
preserve the requested hue where possible while deriving legible text/focus/action
variants. Decide exact contrast adjustment and storage representation during token
implementation; never silently make custom accent text unreadable.

## Preview parity — current implementation is not the finished visual system

The accepted [specimen](design/system-specimen.svg) remains the target. The early
native UI proved ownership, readable text and functional settings; it did **not**
complete that specimen. Do not redefine the target to match temporary widgets.

| Element | Accepted target | Actual gap / D1 exit |
|---|---|---|
| Mode choice | One grouped segmented selector, subtle track and selected segment | Currently two detached large choice buttons; replace in the shared toolkit |
| Accents | Compact circular swatches with a non-color-only selection marker | Currently rectangular text choices; add swatch/check + keyboard labels |
| Settings content | Aligned label/help/value rows, deliberate spacing and semantic sections | Current feature pages use coarse button lists/planned-option text; implement reusable rows/forms/cards |
| Navigation | Calm sidebar/group hierarchy with restrained selected-row emphasis | Category/result buttons are a functional scaffold; shared navigation rows should replace app-specific button walls |
| Chrome | Consistent window/panel radii and equal optically centered decoration slots | Host and native clients use different geometry/density; host retains ownership, align metrics centrally |
| Text/icons | Inter hierarchy plus one outline icon family; real layout/scale | ASCII atlases/fixed sizes and few icon primitives are temporary; U1/D2 own text/scale coverage |
| States | Clear focus, selected/disabled/error/busy without excess borders | Keep existing focus/permission/discard behavior while changing the shared presentation |

Close these at the **shared primitives/layout layer before further app migration**.
Then update existing consumers once. This is current unfinished implementation,
not a planned new visual identity or an excuse for a later per-app redesign.

## Materials and optional effects

[Solid/translucent/blurred comparison](design/material-specimen.svg) illustrates the
**same geometry and content**, in both modes. It is a design reference, not running
blur. Opacity/blur samples are illustrative until compositor measurements exist.

### Stable roles, independent of effect preferences

| Role | Default appearance | Optional material behavior |
|---|---|---|
| Content / document / terminal canvas | Solid semantic Surface | Stays opaque for legibility; no automatic whole-window transparency |
| Control / field / status / important dialog | Solid semantic Raised/Subtle | Text, icons, selection, focus and input feedback stay opaque |
| Navigation / secondary panel | Neutral semantic surface | May use tinted transparency and, independently, backdrop diffusion |
| Host window chrome / shell panel / menu / popover | Neutral material surface | Host-authorized tint/alpha/blur within the same bounds and corner mask |
| Recovery / protected authentication | Solid readable surface | Force-solid policy; user effects must not reduce trust or readability |

A settings/container background can use the Panel role too; eligible material is
not restricted to the sidebar. Content means the document/text plane, not every
pixel inside an application. Choose these semantic regions when building controls,
then change composition centrally.

Material choice **never changes layout**: same bounds, insets, type metrics,
rounding, hit targets, scroll geometry, focus order, separators and window controls.
Do not rebuild the page when effects toggle. Do not lower opacity on a finished
window bitmap: that also fades text, controls and security affordances.

### Rendering/ownership contract

Current graphics ABI 1 forces opaque XRGB. A role-aware opaque painter is the
fallback, not simulated transparency or blur. Userland owns shared widget geometry;
the host owns backdrop sampling, composition and trusted decorations.

The extension must negotiate a new capability/version with **bounded role/region/
clip/corner metadata plus a foreground coverage plane** (for example premultiplied
RGBA), keeping the legacy flattened XRGB path. Text/glyph antialias coverage must
be composited as foreground; today's already-flattened pixels cannot simply be
reinterpreted as alpha. The shared renderer changes targets/backends, not app layout.

Only the host samples a backdrop. Clients never receive screenshots/framebuffer
addresses, other-window pixels, arbitrary shaders or a right to blur outside their
owned surface. Clamp region counts/areas/radii; respect z-order, nested clips,
resize, focus, move, minimize, teardown and session revocation. Prefer damage-
tracked/cached bounded software effects before making them generally available.
The current shared role painter is a seam, **not** this future transport/renderer.

### User preference and fallback rules

Transparency and blur are separate choices. Transparency reveals the backdrop;
translucency combines a stabilizing tint with diffusion when available. Blur is
unavailable when transparent surfaces are off; retain its saved preference and
explain why it is inactive. Custom intensity can follow measured support.

Saved preference, capability availability and effective material must be distinct.
Unsupported renderers, contrast failure, resource pressure or recovery can use the
same opaque design without overwriting the user's saved choice. Preserve readable
text over busy/high-contrast backgrounds; never make blur essential to hierarchy.
Theme/style variants change tokens deliberately, not as a side effect of fallback.

### D1/D3 acceptance

Compare actual shared components with the accepted specimen in light/dark and all
six accents: normal/focused/selected/disabled/error/busy. Match component geometry
before effect work. Later render **identical layouts** in solid, tint-only and
blurred modes; test busy backgrounds, overlapping/moving windows, scaling and
runtime toggles. Audit contrast on final composites (4.5:1 text, 3:1 meaningful
indicators), damage/caching cost, long idle/activity, missing capability and forced
solid fallback. Performance fallback must not move a control or change a draft.

## Shared interaction rules

- Distinguish hover, pressed, selected, keyboard focus, disabled and busy. Focus
  remains visible on selected controls; Tab order follows reading order.
- Buttons have clear verbs. One primary action per task area; danger is exceptional.
- Fields retain labels. Place help/error beside the field; explain how to recover.
  Toggles change a Boolean preference; choices use radios/segmented selection.
- Menus and dialogs support keyboard navigation, Escape and predictable focus return.
  Do not dismiss important changes just because focus moved elsewhere.
- Use a consistent page heading, grouped settings rows and short descriptions.
  Empty/error/loading states say what happened and provide an appropriate action.
- Long work shows phase and progress when known, otherwise honest activity. Offer
  cancellation only where implemented; do not invent percentages or instant success.
- Settings distinguish **saved**, **active**, **unsaved**, **restart required** and
  **unavailable here**. Save failure retains edits. Confirm discarding edits; applying
  appearance must not silently log out, restart a session or lose application work.

## Ownership and delivery boundaries

The existing `zenith-style` metapackage selects theme/font packages; retain that
separation. Shared widget code belongs in userland. Themes supply semantic resources,
applications consume roles, and the host owns window chrome and optional backdrop
composition. Shipped defaults stay separate from local/per-user preferences.

Current theme VERSION=1 covers only a subset of colors; radii/layout remain compiled.
Current graphical clients have fixed opaque XRGB surfaces and no shared text/font
service, resizing, hover events, clipboard or IME. They cannot request alpha/blur.
Do not smuggle effects into that ABI or weaken surface isolation. Typed Appearance preferences and signed palette resources now exist. Material/foreground transport, richer fonts and preference scope still require the explicit extensions above.

## Work ownership and visual acceptance

D1 in the [master roadmap](ROADMAP.md) owns preview parity; D2 owns mode/scale,
D3 owns material composition, S1/S4 own preference/resources and U1 owns shared text
and controls. Component checklists may expand those IDs, not create another style
roadmap. The accepted specimen and comparison above are implementation references;
host renders prove layout only, native/installed checks establish runtime behavior.
