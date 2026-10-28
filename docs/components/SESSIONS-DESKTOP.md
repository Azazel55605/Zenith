# Sessions, desktop and applications

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md)

Work IDs below are detailed steps of the master tracker, not a separate schedule.

## Implemented boundary

Authenticated greeter, Desktop/local Headless selection, no-GPU serial recovery,
identity-scoped input and safe busy/unsaved teardown exist. The temporary floating
DE, compositor, greeter and privileged services remain trusted/kernel-linked.
Selected theme/font resources and typed appearance preferences are independently
updateable. A resource package does not make the greeter an independent process.

## Native app migration

| Master ID / app | Current frontend | Detailed exit |
|---|---|---|
| U5 Welcome/System | Native `desktop-tools` frontends, signed shell and menu dispatch locally tested | D1 parity, live preference/independent upgrade/user/teardown and Q2 release acceptance |
| S1 Meridian | Native 43-page shell, seven groups, search/recent links, functional Colors, keyboard layout/time zone and selected About data | U1 richer controls/lifetime tests, real page backends and Q1/Q2; WIP scope is not functionality |
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

- [x] D1: smooth native controls, completed-window corner masks, shared system-app
  controls and accepted segments/swatches/rows/navigation
  and host chrome locally; Q2 owns installed confirmation, U1/D2 richer widgets/hover/text/scale;
  preserve [material roles](../DESIGN-LANGUAGE.md#materials-and-optional-effects).
- [x] U1/D2: opt-in bounded client resize/maximize, stable sidebar/detail workspaces
  and drag keyboard routing; continuous title/sidebar materials and reduced-grid
  whole-background blur remove the stacked rectangular content underlay; [targeted tests and unresolved operator freeze](../archive/2026-10-28/meridian-workspace-input.md).
- [ ] D2: real display modes, scaling and crash-safe Keep/Revert.
- [x] S1: typed root-only keyboard/time-zone system-default catalogues and saves,
  stale revision checks, explicit save/discard and CLI parity.
- [ ] S1: per-user/default inheritance and the remaining typed settings backends.
- [ ] S2–S5: activate the [43-page inventory](../MERIDIAN-PAGES.md) in backend order;
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

Full DE keyboard launcher/panel/window behavior, notifications, switcher/workspaces
precede selectable automatic tiling. Owned IPC/restart boundaries support eventual
compositor/session separation. True locking must preserve apps while denying
input/content; administrative auto-login, autostart/restore, dimming and suspend
need explicit trusted lifecycle/device support. Keep recovery until these exits pass.

## Source, contracts and evidence

[Sessions](../../src/Core/Sessions), [configuration](../../src/Core/Configuration),
[GUI](../../src/Gui), [trusted apps](../../src/Apps), [native apps](../../userland).
[Login](../LOGIN-SESSIONS.md), [settings](../NATIVE-SETTINGS.md),
[graphics/lifetime/metadata](../GRAPHICAL-CLIENTS.md), [app requirements](../APPLICATIONS.md).
[Current local foundation](../archive/2026-10-27/meridian-shell.md); Q1/Q2 in the
master roadmap own actual publication/installed evidence.
