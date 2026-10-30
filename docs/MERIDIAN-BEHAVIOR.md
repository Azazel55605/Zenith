# Meridian behavior and toolkit primitives — design

**Accepted design** (operator review 2026-10-29); stages 1 and 2 are implemented, stage 3 locally. Work owners:
**U1** (toolkit primitives, hover ABI), **S1** (Meridian navigation and pages),
**D1** (shared visual parity) in the [master roadmap](ROADMAP.md). This page is the
contract; state and evidence stay in the roadmap.
[Toolkit v1](USERLAND-UI.md) · [Graphical ABI](GRAPHICAL-CLIENTS.md) ·
[Page inventory](MERIDIAN-PAGES.md) · [Service contracts](MERIDIAN-PLAN.md)

## Baseline and decisions

KDE Plasma 6 System Settings is the behavioral baseline: drill-down sidebar,
scrolling form pages, combo boxes, a uniform Apply/Reset/Defaults footer, an
unsaved-changes prompt and hover feedback. Its look is not copied; Zenith keeps
its own [design language](DESIGN-LANGUAGE.md).

| Decision | Choice |
|---|---|
| Toolkit model | Extend immediate-mode `zui`; the application owns all values. A retained widget tree was rejected as a new toolkit; per-page patches as non-reusable. |
| Icons | Lucide SVG sources (ISC), rasterized at build time; tinted by role at runtime |
| Hover | New opt-in `Z_GUI_MOVE` event, separate kernel release after the input-freeze fix |
| Release order | Freeze-fix kernel first; then toolkit, Settings, hover kernel as independently qualified stages |

## Toolkit primitives (`userland/ui`)

All primitives are immediate-mode. The toolkit keeps only transient interaction
state (hover, pressed item, scrollbar drag) and never allocates. The set is open:
new primitives follow the same rules.

| Primitive | Contract |
|---|---|
| `zui_icon(name, rect, role)` | 16/24/32 px masks with 2x variants. Roles: normal = accent, selected = accent contrast (`on_accent`), disabled = secondary text. Unknown name draws nothing and returns 0. |
| Scroll area | `begin(rect, &offset)` / `end(content_height)` clips content. Wheel scrolls -code/2 px per event (at most 120); Home/End move list selection; `zui_scroll_reveal` brings a range into view. Page Up/Down need the stage-3 key codes. Overlay scrollbar only on overflow; draggable, track click pages. |
| List view | Virtualized rows (icon, label, optional subtitle, trailing chevron). Single selection, Up/Down/Home/End, Enter activates, first-letter type-ahead. Only visible rows consume the region budget. |
| Combo box | Closed field with value and chevron. Click/Enter opens a popup list (list view) placed inside the window. Esc cancels; Enter/click commits; type-ahead. |
| Form layout | Centered column, maximum width 640 px: right-aligned labels, left-aligned controls, help text below the control, section headings/separators. Below 720 px wide labels stack above controls. |
| Footer bar | Help (optional) · Defaults · Reset · Apply. Apply/Reset enabled only when draft differs from saved; Defaults only when saved differs from defaults. |
| Dialog | Modal card over a dimmed window: title, message, two or three buttons, trapped focus, Esc selects Cancel. |
| Hover | Only when the host advertises move events: hover tint on buttons, rows and chevrons; scrollbar widens. No hover is drawn otherwise. |

The fixed 32-control limit becomes a bounded per-frame budget of 128
interactive regions. Regions beyond the budget are drawn but not interactive and
increment a debug counter; content is never silently dropped.

## Meridian navigation and pages

- **Sidebar:** search field above the ten categories (list view with icons;
  chevron where a category has pages). Opening a category replaces the list with
  its pages under a ← back header and opens its first page. Quick Settings opens
  directly. Back, Alt+Left, or Backspace outside an edited field returns to the
  categories with that category selected.
- **Search:** typing filters all 43 pages by title and `pages.json` keywords;
  results replace the sidebar and show their category as a subtitle. Esc clears
  it and restores the previous sidebar. Ctrl+F focuses search; Tab cycles
  sidebar, page and footer.
- **Page area:** fixed header (title plus one-line description, no repeated
  title), one scroll area containing the form, one compact WIP note where a
  backend is missing, fixed footer.
- **Declarative pages:** a static table of sections and rows. Row = label, help,
  control kind (toggle, combo, segment, swatches, slider/stepper, text field,
  information line, page link) and a draft binding. Generic code owns layout,
  scrolling, draft/saved/default comparison, footer state, the WIP/disabled state
  and the Apply/Discard/Cancel dialog on every exit from a changed page
  (navigation, back, search, close).
- **Backends:** existing services and revision/permission checks are unchanged
  and bound as row bindings: appearance, keyboard layout, time zone, default
  session, network/account/About information.
- **Window size:** Meridian opens at 640x480, the largest surface the host admits at
  open (and 600 px is its minimum), so it always shows two panes: a 200 px sidebar
  below 720 px wide, 240 px above. Form labels stack above controls below 720 px.
  Resize and maximize keep the current viewport contract.

## Hover ABI

- `Z_GUI_MOVE = 11` carries surface-relative x/y. It is delivered only to surfaces
  that opt in through a new surface-request flag; legacy clients receive nothing new.
- The host keeps at most one pending move per surface and replaces it with the
  latest position, so motion cannot fill the bounded queue or displace keys/clicks.
- Leaving the surface sends one move with x = y = -1.
- The same ABI revision adds key extras Page Up = 12 and Page Down = 13 for scroll
  areas and lists.
- The ABI version and [graphical client guide](GRAPHICAL-CLIENTS.md) record the
  capability. The toolkit shows hover only when the host advertises it, so new
  Settings runs on older kernels without hover.

## Icon pipeline

- About 25 Lucide SVGs (the exact list is fixed in stage 1) in `userland/ui/icons/` with the ISC license text, also
  listed in the licenses manual.
- `tools/build-ui-icons.py` (beside `build-ui-fonts.py`) rasterizes the SVG subset
  Lucide uses with the Python standard library (exact stroke-distance coverage,
  8x8 supersampling) into 8-bit masks and generates a C table (name → size → mask).
  Like the font atlas it runs at build time and is not checked in; tests require
  byte-identical output across runs and refuse DTDs, transforms and unknown elements.
- `zui_icon` blends the mask with the role tint using the text coverage blend,
  in both opaque and material frames.

## Testing and failure handling

- **Toolkit host tests** (`test_ui.c`, ASan/UBSan): scroll clamping with extreme
  deltas; list virtualization at 0, 1 and 10,000 rows; combo popup placement at
  window edges; narrow form stacking; footer enablement truth table; dialog focus
  trapping; hover leave and coalescing; unknown icon names; region budget overflow.
- **Meridian host tests** (`test_meridian.c`, real state machine): every page
  renders and scrolls to its end; drill-down/back/search round trips; the
  unsaved-changes dialog on every exit path with Apply, Discard and Cancel; revision
  conflicts and permission denial still surface.
- **Native Settings gate:** updated geometry with rendered-frame witnesses; adds
  long-page wheel scrolling, a combo choice, drill-down/back, the dialog and (with
  the hover kernel) hover. Existing save/reboot/upgrade/permission/ext2 checks stay.
- **Native graphics gate:** legacy clients receive no move events; coalescing
  holds under 10,000 rapid moves.
- Backend errors keep the draft and show in the page status line. A missing icon
  leaves an empty slot.

## Delivery stages

Each stage is qualified independently and recorded under its roadmap owner.

1. Toolkit primitives, icons and tests (U1/D1). The UI gallery shows them; no
   Settings change.
2. Meridian navigation, page model, scrolling, combo, footer and dialog (S1/D1)
   as a Settings release on the current kernel.
3. Hover ABI kernel release (U1), then hover enabled in the toolkit.
