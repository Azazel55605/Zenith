# Sessions, desktop and applications

[Documentation](../README.md) · [Current status](../STATUS.md)

**State:** authenticated login, local text/DE selection and packaged appearance
are implemented. Operator confirms working login on kernel 0.0.12. Full DE/WM and
protected graphical client surfaces/events are now fully qualified locally.

## Implemented steps

- [x] Share keyboard/timezone/session settings across CLI and session hosts.
- [x] Require installed-account authentication before opening a configured session.
- [x] Provide a graphical greeter with masked password and Desktop/Headless choice.
- [x] Run Headless as local text with keyboard/history when a display is available;
  retain no-GPU serial authentication/recovery.
- [x] Safely logout/relogin or switch sessions; refuse busy mutations/unsaved editors.
- [x] Clear queued input, prior credential scope and clipboard on session teardown.
- [x] Load verified fonts/theme/branding packages with embedded recovery fallbacks.
- [x] Supply the temporary floating desktop, Terminal, Files test client and Editor.

## Current behavior and limits

Ctrl+Alt+T opens Terminal; Ctrl+Alt+L requests logout. `session switch de|headless`
ends the session and returns to login; it does not preserve running apps.
`zenith-style` installs the selected theme/fonts through dependencies. Colors and
fonts can update without rebuilding the kernel; compiled layout and rendering cannot.
The greeter/session/GUI remain trusted kernel code, not a separate login executable.

Files remains a simple single-selection filesystem test application. Editor is a
small GUI editor. The System information window is not a Settings application.
The desktop floats windows; there are no implemented automatic-tiling WM/workspaces.

## Remaining steps

- [x] Implement the first [protected surface/event API](../GRAPHICAL-CLIENTS.md)
  and independent probe client; 48 real-compositor checks pass.
- [x] Complete full graphical-client qualification at `cea1f5a`.
- [x] Operator reports installed and tested; self-check, visible independent
  drawing and pointer interaction are shown. [Evidence](../archive/2026-10-26/protected-graphical-clients.md#operator-installed-acceptance).
- [x] Record the [system design direction](../DESIGN-LANGUAGE.md) before widgets.
- [x] User accepts its visual specimen, measurements and shared control states.
- [x] Build the first [shared userland widget/text library](../USERLAND-UI.md).
- [x] Connect signed semantic resource palettes and the typed appearance service.
- [x] Build the independent [Settings Appearance page](../NATIVE-SETTINGS.md) with
  preview/save, failed/stale edit retention and explicit dirty-close confirmation.
- [x] Pass full clean-source Release qualification at `12ded91`.
- [ ] Publish and complete installed Appearance acceptance.
- [ ] Extend Settings to keyboard/timezone/session and native font resource loading.
- [ ] Replace the temporary DE and add a selectable automatic-tiling WM.
- [ ] Rework Files: navigation, selection, transfer jobs, permission/error UX and
  responsiveness; keep shared filesystem operations outside UI code.
- [ ] Rework Editor: document lifecycle, search/undo and robust editing UX.
- [ ] Add per-user preferences, richer styling/assets and accessibility checks.
- [ ] Move GUI/session services outside the kernel through explicit privilege/IPC APIs.

## Source and acceptance

Owners: [sessions](../../src/Core/Sessions), [configuration](../../src/Core/Configuration),
[GUI](../../src/Gui), [apps](../../src/Apps).
References: [login and styling](../LOGIN-SESSIONS.md),
[settings contract](../SETTINGS-SESSIONS.md), [application requirements](../APPLICATIONS.md),
[system requirements](../SYSTEM-PLAN.md).

[Login smoke](../../tools/login-session-smoke.py) checks real authentication,
ordinary/root identity, wrong-password rejection, busy/unsaved refusal, DE/local
switching, German input/history and signed appearance changes under the same
kernel. Preserve no-display [serial acceptance](../../tools/headless-smoke.py)
and independent ext2/secret-free log checks. Source `61618cd` passed 69 login
checks; [evidence](../archive/2026-10-15/PROGRESS.md#2026-10-15--displayed-kernel-recovery-validation-locally-verified).
