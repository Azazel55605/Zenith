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
| S1 Meridian | Native 43-page shell, seven groups, search/recent links, functional Colors and selected About data | U1 richer controls/lifetime tests, real page backends and Q1/Q2; WIP scope is not functionality |
| U2 Editor | Trusted window | Shared multiline text/selection, clipboard/dialogs, UTF-8/LF/CRLF, undo/search, external changes and permission-aware staged saves |
| U3 Terminal | Trusted shell host | Owned PTY/session byte/mode protocol, native renderer, raw/canonical/secret/type-ahead/jobs/EOF/cancel and all recovery hosts |
| U4 Sunrise | Trusted installer | Typed root/live-only wizard with explicit erase consent, safe account secrets, progress and maintenance blockers |
| A1 Files | Intentionally unchanged | Separate full navigation/transfer/selection overhaul after this migration batch |

The asynchronous launcher captures credentials and owns cancellation; compositor
input never performs executable verification/entry. Missing native Welcome/System
packages retain recovery windows; a present invalid executable fails closed.
Live/unconfigured startup recovery windows remain trusted. Native launch output is
not indiscriminately journalled. Session stop revokes surfaces and invocations.

## D1–D3 / S1–S5 — Shared presentation and Settings

- [x] D1: current native controls match accepted segments/swatches/rows/navigation
  and host chrome locally; Q2 owns installed confirmation, U1/D2 richer widgets/hover/text/scale;
  preserve [material roles](../DESIGN-LANGUAGE.md#materials-and-optional-effects).
- [ ] D2: real display modes, scaling and crash-safe Keep/Revert.
- [ ] S1: per-user/system defaults, typed existing settings and CLI/GUI parity.
- [ ] S2–S5: activate the [43-page inventory](../MERIDIAN-PAGES.md) in backend order;
  [Meridian contracts](../MERIDIAN-PLAN.md) specify details, not a second schedule.
- [ ] D3: host-owned optional materials/blur with unchanged layout and opaque fallback.

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
