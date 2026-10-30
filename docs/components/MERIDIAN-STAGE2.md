# Meridian KDE-baseline pages (stage 2) — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

Owners: **S1/D1** (U1 primitives) in the [master roadmap](../ROADMAP.md); contract in
[Meridian behavior design](../MERIDIAN-BEHAVIOR.md), stage 2. Stage 1 primitives:
[toolkit guide](../USERLAND-UI.md#client-library-v2-widgets-stage-1). State and
evidence are recorded in the roadmap, not here.

**Goal:** Rebuild Meridian on the stage-1 widgets: drill-down sidebar with icons and
search, one scrolling form page per destination, combo boxes instead of
Previous/Next, a uniform Defaults/Reset/Apply footer and an Apply/Discard/Cancel
dialog on every exit from a changed page.

**Architecture:** Three host-testable units replace the interleaved state machine:
a **navigation model** (sidebar mode, selection, search results, back), **page
bindings** (one draft/saved/default contract per editable page: colors, keyboard,
time zone, session) and the **shell** (layout, drawing and event routing over the
stage-1 widgets). `app.c` becomes the colors model only. `main.c` keeps every log
marker the native gate relies on.

**Tech Stack:** Freestanding C (std=c11, no libc/allocation/SSE), `userland/ui`
widgets, Python native gate (`tools/native-settings-smoke.py`).

## Global Constraints

- Default window 800x560; minimum 600x480; narrow (single-pane) layout below 720 px wide.
- Sidebar width 240 px in wide layout; footer height 56 px; form max width 640 px.
- Interactive regions per frame stay within `ZUI_MAX_CONTROLS` (128).
- Keep log markers: `SETTINGS_READY`, `MERIDIAN_PAGE <id>`, `MERIDIAN_FOCUSED`,
  `SETTINGS_UNSAVED`, `SETTINGS_DISCARD_PROMPT`, `SYSTEM_SETTINGS_REVISION_CHANGED`,
  `SETTINGS_REVISION_CHANGED`, `MERIDIAN_RESIZED`, `SETTINGS_EXIT`. `MERIDIAN_PAGE
  navigation` is printed when the sidebar returns to the category list.
- Existing services, revision checks, permissions and CLI modes in `main.c` are unchanged.
- Defaults: personal colors scope = inherit system defaults (scope action 2); system
  colors scope = dark, blue, effects 0, translucency 40; keyboard/time zone/session
  have no built-in default (Defaults hidden).
- `settings.elf` keeps at least 32 KiB below the 256 KiB package limit (existing test).
- Every task ends with `cd tools && python3 -B -m unittest test_ui_library` passing.

## Execution notes (2026-10-30)

- **Window:** the host admits at most 640x480 at open and has a 600 px minimum, so the
  planned 800x560 default and single-pane narrow mode were dropped: Meridian opens at
  640x480 with two panes (200 px sidebar below 720 px wide, 240 px above). This
  replaces the window and narrow statements in the constraints and Task 4 below.
- **Defaults** stays visible but disabled on pages without defaults, as in KDE.
- **Layering:** a stage-1 bug gave hover event 9, already `Z_GUI_CLOSE_INTENT`; it is
  now 11. Overlays use the opaque control role, not a material region.

## File structure

| File | Responsibility |
|---|---|
| `userland/settings/app.h` / `app.c` | Colors model: state, backend, preview, apply, reset, defaults, scope (no drawing) |
| `userland/settings/navigation.h` / `navigation.c` (new) | Sidebar model: categories, category pages, search, selection, back |
| `userland/settings/bindings.h` / `bindings.c` (new) | Draft contract for editable pages (colors and system preferences) |
| `userland/settings/meridian.h` / `meridian.c` | Shell: layout, drawing, event routing, dialog |
| `userland/settings/main.c` | Native loop, window size, markers |
| `userland/settings/test_settings.c`, `test_navigation.c` (new), `test_meridian.c` | Host tests |
| `tools/test_ui_library.py` | Compiles the new units and tests |
| `tools/native-settings-smoke.py` | Real guest gate for the new layout |

---

### Task 1: Colors model without drawing

**Files:** Modify `userland/settings/app.h`, `app.c`; rewrite `userland/settings/test_settings.c`.

**Interfaces:**
- Produces (in `app.h`, replacing `settings_draw`/`settings_event`/`settings_set_viewport`/`settings_request_navigation`; `struct settings_app` loses `ui`, `viewport`, `prompt`, `closing`):

```c
/* Draft edits. Each returns 0, or a negative errno-style code when the edit is refused
 * (read only, effect unavailable, or the window could not record the unsaved state). */
int settings_preview(struct settings_app *a,unsigned int mode,unsigned int accent,unsigned int effects,unsigned int level);
long settings_apply(struct settings_app *a);   /* save; 0, -11 stale, -13 denied, other failure */
int settings_reset(struct settings_app *a);    /* drop the draft, reread saved state */
int settings_defaults(struct settings_app *a); /* personal: inherit system; system: dark/blue/0/40 draft */
int settings_at_defaults(const struct settings_app *a);
int settings_set_scope(struct settings_app *a,unsigned int personal); /* refused while edited */
void settings_theme(const struct settings_app *a,struct zui *ui);     /* draft palette, swatches, full_background */
```

- [x] **Step 1: Failing test** — replace `test_settings.c` body with model tests (fake backend as today):
  preview of mode/accent marks `edited` and calls `dirty(1)`; preview back to saved values clears it;
  read-only state (`flags` without `Z_SETTINGS_WRITABLE`) refuses with -13 and leaves values;
  `settings_apply` saves `mode/accent/effects/level/base_revision`, clears `edited`, calls `dirty(0)`;
  stale revision returns -11 and keeps the draft; `settings_reset` restores saved values;
  `settings_defaults` in system scope yields a dark/blue/0/40 draft (edited when saved differs);
  with a scope backend in personal scope it calls `scope(2, base_revision)` and rereads;
  `settings_at_defaults` true only for those values (system) or `inherited` (personal);
  `settings_set_scope` refused (-16) while edited; `settings_theme` copies the draft palette.
- [x] **Step 2:** `cd tools && python3 -B -m unittest test_ui_library` — FAIL (undefined `settings_preview`).
- [x] **Step 3: Implement** — move the edit logic out of `settings_event` (actions 1–2, 10–15, 50–54, 30, 31, 60/61, 91 with prompt 4) into the functions above; keep `settings_init` (without controls) and `settings_refresh`. Delete the drawing code.
- [x] **Step 4:** Tests pass. `meridian.c` is not compiled by this test target yet; keep the Meridian test target building by guarding it out of `test_ui_library.py` until Task 4 (comment in the test explains why).
- [x] **Step 5: Commit** — "Turn the Settings colors app into a drawing-free model".

---

### Task 2: Navigation model

**Files:** Create `userland/settings/navigation.h`, `navigation.c`, `test_navigation.c`; modify `tools/test_ui_library.py`.

**Interfaces:**

```c
enum { NAV_CATEGORIES=0,NAV_PAGES=1,NAV_SEARCH=2 };
struct meridian_nav {
    int mode,category,page;             /* page: index into meridian_pages, always valid */
    int results[MERIDIAN_PAGE_COUNT],result_count;
    char query[64];
};
extern const char *const meridian_categories[10],*const meridian_category_icons[10];
void nav_init(struct meridian_nav *n,int page);
int nav_category_pages(int category,int *out);            /* ordered page indices; returns count */
/* Sidebar rows for the current mode: label, icon (categories), subtitle (group or category). */
int nav_rows(const struct meridian_nav *n,int index,struct zui_list_item *out);
int nav_row_count(const struct meridian_nav *n);
/* Activating row i: categories -> NAV_PAGES of that category and its first page
 * (Quick Settings opens its page directly and stays in NAV_CATEGORIES); pages/search -> that page. */
void nav_activate(struct meridian_nav *n,int row);
void nav_back(struct meridian_nav *n);                      /* NAV_PAGES/SEARCH -> NAV_CATEGORIES, category kept */
void nav_search(struct meridian_nav *n,const char *query);  /* empty query leaves search */
int nav_open(struct meridian_nav *n,const char *id);        /* by page id; selects its category */
```

Search matches title, id and `pages.json` labels (case-insensitive); results show their
category as subtitle. Category icons: `zap keyboard usb network palette app-window
layout-grid shield languages monitor-cog`.

- [x] **Step 1: Failing test** (`test_navigation.c`): all 43 pages reachable through categories exactly once;
  activating category 1 lists its five pages and opens `input.mouse`; `nav_back` returns to categories with
  category 1 selected; `nav_search(n,"bluetooth")` yields exactly `devices.bluetooth`; `nav_search(n,"")` returns
  to the previous mode; Quick Settings opens `quick.overview` without leaving the category list; `nav_open`
  of every id selects its category; unknown id returns 0 and changes nothing.
- [x] **Step 2:** Add a sanitizer target compiling `test_navigation.c navigation.c meridian-pages.c ui.c fonts.c icons.c`; run — FAIL.
- [x] **Step 3: Implement** using the generated `meridian_pages` table (category string compare as today).
- [x] **Step 4:** Pass. **Step 5: Commit** — "Add a host-tested Meridian navigation model".

---

### Task 3: Page bindings

**Files:** Create `userland/settings/bindings.h`, `bindings.c`; tests in `test_navigation.c` (renamed target "settings units").

**Interfaces:**

```c
enum { BIND_NONE=0,BIND_COLORS=1,BIND_KEYBOARD=2,BIND_TIMEZONE=3,BIND_SESSION=4 };
struct meridian_bindings {
    struct settings_app *colors;
    struct zenith_system_settings preferences; int preferences_ready;
    long (*read)(struct zenith_system_settings *);
    long (*save)(unsigned int field,unsigned int choice,unsigned long revision);
    long (*dirty)(int value);
    unsigned int choice[4];      /* draft per field 1..3 */
    unsigned long revision;      /* revision the drafts are based on */
    const char *message;
};
int binding_of(const char *page_id);
int binding_changed(const struct meridian_bindings *b,int kind);
int binding_at_defaults(const struct meridian_bindings *b,int kind); /* 1 for kinds without defaults */
int binding_has_defaults(int kind);
long binding_apply(struct meridian_bindings *b,int kind);
void binding_reset(struct meridian_bindings *b,int kind);
void binding_defaults(struct meridian_bindings *b,int kind);
int binding_choose(struct meridian_bindings *b,int kind,unsigned int choice); /* preferences draft */
void binding_refresh(struct meridian_bindings *b);  /* validates like preferences_valid today */
int binding_choice_count(const struct meridian_bindings *b,int kind);
const char *binding_choice_name(const struct meridian_bindings *b,int kind,unsigned int index);
```

- [x] **Step 1: Failing test:** keyboard draft change marks changed and `dirty(1)`; apply saves field 1 with the
  draft revision; a revision bump during the draft makes apply return -11 and keeps the draft with message
  "Changed elsewhere. Reset to reload."; read-only flags refuse `binding_choose` (-13); reset restores saved;
  session binding unavailable (choice count 0) when `preferences.version==1`; colors kind delegates to the
  Task 1 model.
- [x] **Steps 2–5:** run (FAIL), implement by moving `preferences_valid`/`refresh_preferences`/save logic out of
  `meridian.c`, run (PASS), commit — "Give editable Meridian pages one draft contract".

---

### Task 4: Shell layout, drawing and events

**Files:** Rewrite `userland/settings/meridian.h`, `meridian.c`, `test_meridian.c`; modify `tools/test_ui_library.py`
(restore the Meridian target with the new sources).

**Interfaces:**

```c
struct meridian {
    struct zui ui;
    struct settings_app colors;
    struct meridian_bindings bindings;
    struct meridian_nav nav;
    struct zui_list sidebar; struct zui_scroll page_scroll; struct zui_combo combo;
    struct zui_dialog dialog; struct zui_input search; char search_text[64];
    int search_focus,narrow_page,closing,pending_row,pending_action;
    long (*information_read)(unsigned int,struct zenith_settings_information *);
    char public_information[4096]; unsigned int public_kind; int public_error;
    char information[4096];
};
/* Same entry points as today; meridian_ui returns &a->ui. */
```

Layout contract (window coordinates, wide layout):
- Workspace sidebar 240 px: search field at (24,20,212,34); sidebar list from y=66; in
  NAV_PAGES/SEARCH a back row "← <category>" (icon `arrow-left`) is the first row.
- Page area: header title (heading font) at content.x+24, y=24; one-line description
  (caption) at y=58; scroll area from y=84 to footer top; footer 56 px at the bottom of
  the content inset when the page has a binding, otherwise no footer.
- Page bodies are forms (`zui_form_*`): WIP note row first ("Work in progress: …" from
  the existing WIP strings); colors: Color mode segment pair, Accent swatches (six,
  32 px, caption), Effects section with three toggles and a translucency stepper
  (`-`/`+` buttons with the percentage), Scope segment (Personal/System) when scopes exist;
  keyboard/time zone/session: one combo row; information pages (network, users, about):
  `info` rows per line of the service text; other WIP pages: one row per `pages.json` label
  with "Not available yet" in muted text.
- Narrow (<720): only the sidebar or the page; the page header gets a back icon button.
- Event routing order: dialog, combo, search field, sidebar list, page scroll, page controls,
  footer. Ctrl+F focuses search; Esc in search clears it; Alt+Left/Backspace (outside a
  field) = back; Ctrl+Q/close intent with a changed page opens the dialog with action close.
- Leaving a changed page (sidebar activation, back, search, close) opens
  `{"Apply changes?","This page has unapplied changes.",{"Apply","Discard","Cancel"}}`;
  Apply that fails keeps the page and shows the binding message.

- [x] **Step 1: Failing test** (`test_meridian.c` rewritten): every page opens and draws at 800x560 and 600x480
  with `ui.count<=ZUI_MAX_CONTROLS`; sidebar drill-down/back/search round trips by real clicks (helper finds a
  control by id or label and clicks its `zui_control_area`); colors: click Light segment → `SETTINGS_UNSAVED`
  equivalent (`colors.edited`), footer Apply enabled, Apply saves; change then click another category →
  dialog open; Cancel stays, Discard navigates and resets, Apply saves then navigates; keyboard combo: open,
  Down, Enter → changed; Apply saves `preferences.keyboard`; revision bump → Apply keeps draft with message;
  network/users pages show service text lines; 2000 alternating extreme wheel events keep scroll offsets in
  range; resize 600x480 ↔ 1920x1000 keeps drafts; material frame keeps controls opaque (existing assertions
  adapted to the new geometry).
- [x] **Steps 2–5:** run (FAIL), implement, run (PASS), commit — "Rebuild Meridian on drill-down navigation, form pages and a uniform footer".

---

### Task 5: Native loop

**Files:** Modify `userland/settings/main.c`.

- [x] Open `800x560` (material and opaque surfaces); scroll coalescing splits at the sidebar edge
  (`x < 252` wide, page-only in narrow); print `MERIDIAN_PAGE navigation` when the sidebar returns
  to categories; `SETTINGS_DISCARD_PROMPT` when the dialog opens; keep every other marker.
- [x] Build: `python3 -B tools/build-userland.py obj/s2-settings --program settings`; size test passes.
- [x] Commit — "Open Meridian at 800x560 with stage-2 markers".

---

### Task 6: Native Settings gate

**Files:** Modify `tools/native-settings-smoke.py` (Settings UI section only; CLI, permission, reboot and
audit steps unchanged).

- [x] Locate the window by the focused search field border (`7ecac7` row at the sidebar) and derive every
  control position from the Task 4 layout contract; keep `frame()` retries and rendered witnesses.
- [x] Replace colors steps: open `style.colors` via `--page`; keyboard Tab/Enter on Light; check
  `SETTINGS_UNSAVED`; chrome close → `SETTINGS_DISCARD_PROMPT` and dialog Cancel keeps editing;
  toggle transparency/blur and footer Apply → `SETTINGS_REVISION_CHANGED`; material/solid frame checks
  as today with new sample points.
- [x] Replace shared-default steps: open keyboard/time zone/session pages, open the combo, Down × count,
  Enter, Apply; check `SYSTEM_SETTINGS_REVISION_CHANGED` and CLI parity as today.
- [x] Navigation: Ctrl+F, type `bluetooth`, Enter → `MERIDIAN_PAGE devices.bluetooth`; rapid wheel over
  sidebar and page, then Esc/back responds; maximize/restore → `MERIDIAN_RESIZED`.
- [x] Run `python3 -B tools/native-settings-smoke.py obj/s2-gate`; all checks pass. Commit — "Drive the
  stage-2 Meridian layout in the native Settings gate".

---

### Task 7: Documentation, evidence and release

- [x] Update `docs/MERIDIAN-PAGES.md` header/state, `docs/MERIDIAN-BEHAVIOR.md` (stage 2 implemented locally),
  `docs/SETTINGS-SESSIONS.md` if it describes the old picker, roadmap S1/D1 rows, and record evidence.
- [ ] Full `deploy/distributor/validate.py` qualification on unchanged source.
- [ ] Ask the operator before tagging the Settings release (`zenith-settings/v0.4.0`).
