# Independent programs and shared UI

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md)

Work IDs below are detailed steps of the master tracker, not a separate schedule.

## Implemented boundary

Signed static x64 ELF programs have private mappings, validated copyin/copyout,
owned file/network/graphics resources and exit/fault/watchdog/cancel cleanup.
On multi-CPU x64 boots the general scheduler and native service threads can run across CPUs. The single-CPU fallback and explicit native-worker mode still have dedicated checks. Shell authority, hardware operations and the trusted compositor keep explicit owners. Native GUI packages include Aurora, Horizon, Polaris and Meridian. [CPU modes](../MULTICORE.md) · [Native apps](SESSIONS-DESKTOP.md#native-app-migration)

## U1 / D1 — Shared runtime and widgets

- [x] [Meridian behavior design](../MERIDIAN-BEHAVIOR.md) stage 1 (local): 128-region budget,
  Lucide icons, scroll area, list view, combo box, form layout, footer, dialog and
  hover plumbing ([implementation plan](USERLAND-UI-STAGE1.md)).
- [x] Buttons/toggles/choices, fixed Inter atlases, clipping, focus and opaque palettes.
- [x] Bounded UTF-8-preserving single-line input, wrapping and virtualized page lists.
- [x] Owned interactive quota renewal and unsaved-close intent.
- [ ] Prove operation beyond former lifetime limits under idle/activity/pressure,
  including minimized windows, cancellation and session revocation.
- [x] S1 bounded searchable keyboard/time-zone/session selectors and confirmed
  personal-default reset; scoped service and C state-machine tests.
- [ ] Extend reusable multiline editing and file controls beyond the existing app-specific implementations. Reuse the working clipboard and shared form/list/dialog primitives. [Aurora](AURORA.md) · [Toolkit](../USERLAND-UI.md)
- [x] Explicit bounded viewport resize/maximize, persistent inset workspaces and
  pane-aware wheel handling; [local evidence and freeze limits](../archive/2026-10-28/meridian-workspace-input.md).
- [ ] Add dynamic fonts, broader Unicode/shaping/IME and device-resolution text scaling while preserving saved bytes.
- [x] The shared desktop and signed clients use the accepted visual and material roles. [Design language](../DESIGN-LANGUAGE.md)

## U2–U6 — Runtime separation

Aurora and Polaris are independent clients. Trusted editor and terminal fallbacks still serve recovery. Sunrise's native frontend needs typed disk/account/progress services. U6 covers a separate step, independently restartable session, compositor and privileged service processes with versioned IPC and explicit authority. Exercise upgrades, ordinary-user denials, fault/cancel and recovery for each port. [Session and app boundaries](SESSIONS-DESKTOP.md#native-app-migration)

## Source and evidence

[Process bridge](../../src/Core/Processes), [x64 entry](../../src/Native/x64/Process.s),
[userland](../../userland), [shared UI](../USERLAND-UI.md),
[ABI](../NATIVE-PROGRAMS.md), [graphics](../GRAPHICAL-CLIENTS.md).
[Latest foundation evidence](../archive/2026-10-27/meridian-shell.md).
Q2 in the master roadmap owns current publication and installed acceptance.
