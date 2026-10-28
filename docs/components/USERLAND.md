# Independent programs and shared UI

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md)

Work IDs below are detailed steps of the master tracker, not a separate schedule.

## Implemented boundary

Signed static x64 ELF programs have private mappings, validated copyin/copyout,
owned file/network/graphics resources and exit/fault/watchdog/cancel cleanup.
Raw native execution is serialized on the BSP; managed service waits may overlap.
Base/fs/text/network clients, graphical probes and Appearance are independent.
Current local source also has Meridian navigation and native Welcome/System.
This is not general POSIX execution, managed SMP or an isolated compositor.

## U1 / D1 — Shared runtime and widgets

- [x] Buttons/toggles/choices, fixed Inter atlases, clipping, focus and opaque palettes.
- [x] Bounded UTF-8-preserving single-line input, wrapping and virtualized page lists.
- [x] Owned interactive quota renewal and unsaved-close intent.
- [ ] Prove operation beyond former lifetime limits under idle/activity/pressure,
  including minimized windows, cancellation and session revocation.
- [ ] Add shared editable/selectable multiline text, clipboard, file dialogs,
  list/selector/slider/number fields and consistent error/confirmation components.
- [x] Explicit bounded viewport resize/maximize, persistent inset workspaces and
  pane-aware wheel handling; [local evidence and freeze limits](../archive/2026-10-28/meridian-workspace-input.md).
- [ ] Add dynamic fonts, Unicode/shaping/IME, display scaling and richer input semantics;
  no glyph replacement may corrupt saved text.
- [ ] Match the accepted [design](../DESIGN-LANGUAGE.md) in primitives before more
  app-specific UI. Material roles keep geometry unchanged with D3 effects enabled or disabled.

## U2–U6 — Runtime separation

Editor, Terminal and Sunrise remain kernel-linked pending typed contracts. Apps,
compositor/session and privileged services ultimately need independently restartable,
versioned IPC boundaries. Keep driver/config/auth authority in trusted services;
never treat package names or environment variables as privileges. Same-kernel app
upgrade, ordinary-user denials, faults/cancel and recovery are required per port.

## Source and evidence

[Process bridge](../../src/Core/Processes), [x64 entry](../../src/Native/x64/Process.s),
[userland](../../userland), [shared UI](../USERLAND-UI.md),
[ABI](../NATIVE-PROGRAMS.md), [graphics](../GRAPHICAL-CLIENTS.md).
[Latest foundation evidence](../archive/2026-10-27/meridian-shell.md).
Q1/Q2 in the master roadmap own publication/installed acceptance.
