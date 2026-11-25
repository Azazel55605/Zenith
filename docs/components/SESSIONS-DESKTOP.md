# Sessions, desktop and applications

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md)

Work IDs below are detailed steps of the master tracker, not a separate schedule.

## Implemented boundary

The authenticated greeter offers the floating Desktop, Tiling WM and local Headless
sessions. They share identity-scoped input and busy/unsaved teardown checks.
Headless supports serial recovery without a display. Both graphical sessions use
the same trusted compositor, greeter and privileged services.
Selected theme/font resources and typed appearance preferences are independently
updateable. A resource package does not make the greeter an independent process.

## Native app migration

| Master ID / app | Current frontend | Detailed exit |
|---|---|---|
| U5 Welcome/System | Native `desktop-tools` frontends, signed shell and menu dispatch locally tested | D1 parity, live preference/independent upgrade/user/teardown and Q2 release acceptance |
| S1 Meridian | Native 44-page shell, seven groups, search/recent links, scoped Colors/inheritance, searchable keyboard/time-zone/session choices, authorized network/account information and selected About data | U1 richer controls/lifetime tests, real page backends and Q1/Q2; WIP scope is not functionality |
| U2 Editor | Trusted window, shared-design toolbar/canvas/status, guarded chrome close | Shared multiline text/selection, clipboard/dialogs, UTF-8/LF/CRLF, undo/search, external changes and permission-aware staged saves |
| U3 Terminal | Trusted shell host, shared material canvas, solid hidden input and appearance-aware ANSI scrollback | Owned PTY/session byte/mode protocol, native renderer, raw/canonical/secret/type-ahead/jobs/EOF/cancel and all recovery hosts |
| U4 Sunrise | Trusted installer, shared fields/navigation/action controls and credential-solid account entry | Typed root/live-only wizard with explicit erase consent, safe account secrets, progress and maintenance blockers |
| A1 Files | Intentionally unchanged | Separate full navigation/transfer/selection overhaul after this migration batch |

The asynchronous launcher captures credentials and owns cancellation; compositor
input never performs executable verification/entry. Missing native Welcome/System
packages retain recovery windows; a present invalid executable fails closed.
Live/unconfigured startup recovery windows remain trusted. Native launch output is
not indiscriminately journalled. Session stop revokes surfaces and invocations.

## D1–D3 / S1–S5 — Shared presentation and Settings

- [x] [Meridian behavior design](../MERIDIAN-BEHAVIOR.md) stage 2 (local): drill-down
  navigation, form pages, combo choices, footer and unsaved-changes dialog
  ([implementation plan](MERIDIAN-STAGE2.md)).
- [x] D1: smooth native controls, completed-window corner masks, shared system-app
  controls and accepted segments/swatches/rows/navigation
  and host chrome locally; Q2 owns installed confirmation, U1/D2 richer widgets/hover/text/scale;
  preserve [material roles](../DESIGN-LANGUAGE.md#materials-and-optional-effects).
- [x] U1/D2: opt-in bounded client resize/maximize, stable sidebar/detail workspaces
  and drag keyboard routing; continuous title/sidebar materials and reduced-grid
  whole-background blur remove the stacked rectangular content underlay; [targeted tests and unresolved operator freeze](../archive/2026-10-28/meridian-workspace-input.md).
- [x] D2 primary slice: driver-bounded display modes, integer scaling and host-owned Keep/Revert; installed/device acceptance remains Q2/H1.
- [x] S1: typed root-only keyboard/time-zone system-default catalogues and saves,
  stale revision checks, explicit save/discard and CLI parity.
- [x] S1 shared stage: per-UID profiles, live default inheritance/reset, searchable
  catalogues, registered default sessions and read-only network/account summaries.
  [Contract/evidence](../archive/2026-10-28/shared-settings-stage.md); Q2 owns installed two-account acceptance.
- [ ] S1 remaining broader backends: guarded edits/authority and richer About inventory;
  driver/device, clock scheduling and account lifecycle remain their roadmap owners.
- [ ] S2–S5: activate the [44-page inventory](../MERIDIAN-PAGES.md) in backend order;
  [Meridian contracts](../MERIDIAN-PLAN.md) specify details, not a second schedule.
- [ ] D3: software integration locally qualified: explicit foreground/regions,
  cached tint/blur, saved/effective controls, shared chrome/taskbar/Meridian policy,
  contrast and bounded resource/work fallback. [Evidence](../archive/2026-10-28/desktop-material-integration.md).
  Q2 installed/device/sustained performance and D2 resize/scale acceptance remain;
  credential entry remains solid. Terminal, Editor, non-credential Sunrise and
  recovery Welcome/System bodies now opt into the whole-background policy; Files
  is excluded. System-wide translucency level maps onto a readable contrast range. Supported workspace apps may
  opt into the system-wide whole-background setting; legacy preference saves preserve
  its bit. Taskbar customization belongs to W1/S4.

## W1–W3 / U6 — Product desktop and lifecycle

The DE and WM share the launcher, panel, notifications, switcher and workspaces.
Owned IPC/restart boundaries support eventual
compositor/session separation. True locking must preserve apps while denying
input/content; administrative auto-login, autostart/restore, dimming and suspend
need explicit trusted lifecycle/device support. Keep recovery until these exits pass.

### W1 — Apps and Windows

Meridian applies window behavior to the floating desktop. Focus can follow the
pointer after a configurable delay. Click and hover raising are independent;
double-clicking the titlebar and using the maximize button can maximize, minimize,
toggle fullscreen, or do nothing. Edge tiling and magnetic snapping to nearby
windows are independent.

Hold Alt and press Tab to cycle through the configured cards or compact list.
Shift cycles backwards. Release Alt or use Enter to activate, Escape to cancel.
The switcher can list all windows or the most recently used window per trusted
application identity. Sort by focus recency, opening order, or title and show the
current workspace or all workspaces. Choosing a window on another desktop switches
there. This desktop has one output. Monitor filtering awaits multi-monitor support.

Create, rename, reorder or remove up to eight desktops in Meridian. The taskbar
shows windows on the active desktop and provides a numbered pager. Ctrl+Alt+Left
and Right switch in order. Add Shift to move the focused window to that desktop
with you. Removing a desktop sends its windows to the first desktop in the new
order. Reordering preserves window ownership. Workspaces stay live while hidden,
and switching clears pointer/input ownership. Optional crossfades honor the global
animation speed and resource budget. Names and order persist. Windows and the
currently selected workspace are session state.

[Taskbar placement and pins](MOTION.md#stage-4-taskbar) ·
[Notification rules and history](APPEARANCE.md) ·
[Native settings contract](../NATIVE-SETTINGS.md#desktop-preferences)

### W2 — Automatic tiling

Choose **Tiling WM** at login. On a configured installation, `session switch wm`
returns to the greeter with WM selected. Save the default in Meridian's session
settings or run `session set-default wm` as root. `session=wm` selects it for a
single boot. Choose Desktop or `session switch de` to return to floating placement.

The first resizable window on a desktop gets the master tile. Subsequent windows
share the stack. Wide screens prefer a master on the left and portrait screens
prefer a master on top. Opening, closing, minimizing or restoring a window
reflows its desktop. When the stack cannot fit another window at its minimum
size, focusing that window shows its page. If two tiles cannot fit side by side
or top to bottom, the focused tile gets the work area. Alt+Tab reaches windows
on other pages. Fixed-size clients float. F11 uses the full display and restores
the tile on exit.

| Shortcut | Action |
|---|---|
| Left Alt + Arrow | Focus the neighboring tile |
| Left Alt + Page Up or Page Down | Cycle through every tile on this desktop, including hidden pages |
| Left Alt + Shift + Arrow | Swap with that neighbor |
| Left Alt + Minus or Equal | Shrink or grow the master by five percentage points |
| Ctrl + Left Alt + F | Float or retile the focused resizable window |
| Ctrl + Left Alt + 1–8 | Select a configured desktop by its position |
| Ctrl + Alt + Left or Right | Select the previous or next desktop |
| Add Shift to a desktop shortcut | Move the focused window and follow it |

Run `wm` for the shortcut list. The initial master share is 55%.
`settings set tile-split 60` saves a new default (25–75%). Set the panel in
**Meridian → Apps & Windows → Taskbar → Screen edge**. Applying **Top** or
**Bottom** updates both graphical sessions' shared preference. An administrator
can also use `settings set panel-position top`. The tiler uses the panel's work
area. Manage desktop names and order under **Virtual Desktops**. W2 currently
manages the primary display.

In **Meridian → Apps & Windows → Window Behavior**, the **Tiling WM** controls
set the gap between tiled windows and the margin around the tiled workspace.
Both use logical pixels, range from 0 to 32, and default to zero. Apply changes
to reflow open tiles. The outer gap starts at the panel-adjusted work area and
also applies to a single tile. Floating windows keep their own bounds and F11
uses the full screen. On small workspaces the tiler can reduce the effective
spacing to respect window minimum sizes. The saved values carry across
workspaces and installed reboots. For example, `settings set tile-gaps 8 16`
sets an 8-pixel inner gap and a 16-pixel outer gap. Use
`settings set tile-gaps 0 0` for touching tiles.

The distributor's `w2` gate installs a disposable guest, boots its saved WM
session and exercises three terminals, workspace moves, panel placement and
native Polaris resizing. Run it locally against `output-x64` with a fresh
evidence directory.

```sh
python3 -B deploy/distributor/validation-runner.py tools/wm-smoke.py /tmp/zenith-wm-check
```

The default input device is VirtIO. Use `--keyboard ps2` to exercise PS/2.

Local QEMU acceptance at 1280×800 measured 12 logical pixels between all three
tiles and 16 around the work area. Resetting both gaps restored their original
bounds. With the panel at the top, the single tile's 16-pixel inset survived
an installed reboot. Signed Polaris reported 69 terminal columns in its tile,
154 in fullscreen and 69 after returning to the tile. Changing gaps while
Polaris floated preserved its window bounds. The session
checks also covered authenticated switching, busy/unsaved logout guards and
serial fallback with a saved WM default. Installed VMware acceptance is open.

## Source, contracts and evidence

[Sessions](../../src/Core/Sessions), [configuration](../../src/Core/Configuration),
[GUI](../../src/Gui), [trusted apps](../../src/Apps), [native apps](../../userland).
[Login](../LOGIN-SESSIONS.md), [settings](../NATIVE-SETTINGS.md),
[graphics/lifetime/metadata](../GRAPHICAL-CLIENTS.md), [app requirements](../APPLICATIONS.md).
[Current local foundation](../archive/2026-10-27/meridian-shell.md); Q1/Q2 in the
master roadmap own actual publication/installed evidence.

### M2/D2 display transaction

Implemented primary slice (2026-11-04): primary-output catalogue comes from the
pinned driver audit, with VMware framebuffer/register limits. Virtio/GOP stay
fixed-mode. UI-owner requests prepare new logical buffers before mode changes;
old buffers survive until Keep/Revert. Integer 100/200% scales the completed UI
and maps physical input into logical coordinates (minimum 800x600 logical).
The host owns the 15-second preview and client-exit/session rollback. Kept settings
use `/etc/zenith/display.conf`; `display=safe` ignores them during recovery boot.
[Contract](../DISPLAY-SETTINGS.md), [local evidence](../archive/2026-11-04/primary-display-settings.md).
Installed VMware/device acceptance remains Q2/H1; orientation/refresh, fractional
scale and multi-output support remain D2.
