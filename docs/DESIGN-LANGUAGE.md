# Zenith design language

**Design baseline v0.1 accepted by the user on 2026-10-27.** This defines the
shared visual and interaction language before widgets and Settings are built.
It does not claim that these controls, effects or preferences already exist.
[Visual specimen](design/system-specimen.svg) · [Execution](PROGRESS.md#system-design-language)

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

## Materials and optional effects

Opacity and blur are separate preferences. Transparency reveals what is behind a
surface; translucency combines a tinted transparent material with diffusion/blur.
Blur requires compositor support; alpha alone is not blur.

| Material | Intended use | Fallback |
|---|---|---|
| Solid | Main document canvas, terminal, dense content | Always available |
| Translucent | Optional panel, sidebar, menu and window-chrome material | Opaque semantic surface |
| Blurred translucent | Same material with backdrop diffusion when enabled | Translucent if allowed, otherwise solid |

Proposed settings: **Transparent surfaces** on/off and **Backdrop blur** on/off.
Blur is unavailable when transparent surfaces are off; retain the saved preference
and explain why it is inactive. A future intensity setting can follow. Do not lower
text/icon opacity with the background. Use a stabilizing tint so busy wallpaper
cannot defeat legibility. Password entry and important dialogs must stay readable
with every effect choice. Recovery and unsupported renderers use solid surfaces.

No effect should be necessary to understand focus, hierarchy or selection. Avoid
perpetual animation; damage-track redraw and bound blur region/cost. Performance
fallbacks must report effective versus saved appearance rather than changing the
saved preference. Exact material opacity/blur radius requires real compositor tests.

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
Do not smuggle effects into that ABI or weaken surface isolation. A versioned resource
contract and typed appearance services must be designed before implementation.

## Delivery sequence and acceptance

1. Visual baseline accepted: neutrals, density, typography, geometry and states.
   Title-bar controls use equal slots with optically centered vector icons; do not
   align decorations with spaces inside a text label.
2. Define versioned semantic tokens/resources, text rendering and widget behavior;
   keep theme compatibility and embedded recovery resources explicit.
3. Build the minimal userland controls for an independently packaged Settings app;
   prove keyboard/pointer focus, light/dark, all accents and error/disabled states.
4. Add authorized saved/active appearance preferences through shared CLI/GUI services.
   Document whether each change applies live, next login or after a restart.
5. Add compositor material support, then exposed transparency/blur controls. Verify
   opaque fallback, moving windows, busy backgrounds and bounded software-render cost.
6. Migrate clients/chrome incrementally; do not claim the test DE replaced until its
   independent lifecycle and teardown are verified.

Review requires rendered normal/focused/disabled/error states in both appearances,
contrast checks for all presets, keyboard-only use and scaled text without clipping.
Native rendering, effects performance and installed-VM acceptance remain future gates.
