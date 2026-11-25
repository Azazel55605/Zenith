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
| U5 Welcome/System | Signed desktop-tools frontends with shell/menu dispatch and trusted recovery fallbacks | Installed upgrade, permissions and teardown checks |
| S1 Meridian | Native Settings with 44 pages, eight navigation groups, scoped preferences and authorized system controls | Finish remaining backends and check the installed settings scope. [Page coverage](../ROADMAP.md#meridian--every-requested-destination) |
| U2 Editor | Native Aurora with tabs, editable UTF-8 documents, staged saving, recovery, project navigation and folding | External compiler/language-server integration depends on process and toolchain work. [Aurora](AURORA.md) |
| U3 Terminal | Native Polaris runs kernel-owned terminal sessions with recovery hosts | Preserve input modes, owned sessions and shell return across upgrades. [Polaris](SHELL.md#polaris-design) |
| U4 Sunrise | Trusted installer, shared fields/navigation and protected credential entry | Provide typed live/root-only disk, account and progress operations for the native wizard |
| A1 Files | Native Horizon with tabs, selection, transfers and archive operations | Removable media and network shares depend on S6 and N4. [Horizon](HORIZON.md) |

The asynchronous launcher captures credentials and owns cancellation; compositor
input never performs executable verification/entry. Missing native Welcome/System
packages retain recovery windows; a present invalid executable fails closed.
Live/unconfigured startup recovery windows remain trusted. Native launch output is
not indiscriminately journalled. Session stop revokes surfaces and invocations.

## D1–D3 / S1–S5 — Shared presentation and Settings

The shared layout, controls, inset workspaces, rounded masks and taskbar/chrome materials implement the accepted appearance. Optional whole-window translucency keeps credentials opaque. Signed apps use the same roles in both appearances. [Design language](../DESIGN-LANGUAGE.md) · [Material contract](../GRAPHICAL-CLIENTS.md#opt-in-material-frames-d3-first-slice)

S1 provides revisioned system settings and per-UID appearance overrides with inheritance. Check permissions and persistence at each new consumer. The primary display service owns mode changes and rollback. S2–S5 own new input policies, shared scheduling, asset controls and search. [Page coverage](../ROADMAP.md#meridian--every-requested-destination) · [Display settings](../DISPLAY-SETTINGS.md)

## W1–W3 / U6 — Product desktop and lifecycle

The DE and WM share the launcher, panel, notifications, switcher and workspaces.
Sunset's manual lock preserves apps and denies input and visibility until the owning user authenticates. W3 owns idle/grace, guarded automatic login, autostart and restore. U6 owns independently restartable compositor and session services. [Sunset](SUNSET.md)

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
[Original shell qualification](../archive/2026-10-27/meridian-shell.md) · [Current release evidence](../ROADMAP.md#state-and-evidence)

### M2/D2 display transaction

Implemented primary slice (2026-11-04): primary-output catalogue comes from the
pinned driver audit, with VMware framebuffer/register limits. Virtio/GOP stay
fixed-mode. UI-owner requests prepare new logical buffers before mode changes;
old buffers survive until Keep/Revert. Integer 100/200% scales the completed UI
and maps physical input into logical coordinates (minimum 800x600 logical).
The host owns the 15-second preview and client-exit/session rollback. Kept settings
use `/etc/zenith/display.conf`; `display=safe` ignores them during recovery boot.
[Contract](../DISPLAY-SETTINGS.md), [local evidence](../archive/2026-11-04/primary-display-settings.md).
VMware also supports filtered intermediate scales. D2 owns sharp fractional rendering, orientation/refresh and multiple outputs. Q2/H1 track installed and hardware acceptance.
