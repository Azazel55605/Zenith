# System requirements and delivery boundaries

Accepted product direction, reviewed **2026-10-15**. This page owns requirements,
not execution status. [Status](STATUS.md) states what exists; [Roadmap](ROADMAP.md)
orders delivery; component pages own implementation steps.

## System shape

One installation supports desktop and Headless sessions. Headless means a local
text console with keyboard input when a display is available, with serial fallback.
Desktop has two eventual choices: a full DE and an automatic-tiling WM sharing
applications and core services. The simple floating DE remains available for testing
until the replacement works.

Kernel, desktop, applications and appropriate privileged services should have real
runtime boundaries following the Linux architectural approach. Independently
installing/updating an app must not require linking a new kernel. An app fault must
not corrupt the kernel or another app. This does not require Linux binary
compatibility, a particular compositor protocol or a microkernel.

## Required experiences

| Area | Requirement | Acceptance direction |
|---|---|---|
| Login/session | Real authentication, Desktop/Headless selection, safe logout/relogin and recovery | Correct identity, hidden secrets, no stale input/privilege, busy/unsaved work handled visibly |
| Settings | A proper graphical Settings application using the shared service | Consistent CLI/GUI validation; distinguish active, saved and restart-required state |
| DE | Usable launcher, panel/window/workspace management, power/session actions | Keyboard and pointer workflows, responsiveness and understandable errors |
| WM | Selectable automatic tiling with shared apps/services | Deterministic focus/layout, keyboard controls, floating exceptions and recovery |
| Files | Replace the test client with a practical file manager | Navigation/selection, transfer progress/cancel, conflicts, permissions, links and errors |
| Editor | Replace the basic GUI editor with a usable document workflow | Search/undo, save safety, unsaved-document handling and keyboard usability |
| Customization | Updateable shared style/assets, later per-user overrides | Safe validation/fallback; changes apply at a documented reload/relogin boundary |
| Accessibility/input | Correct layouts, navigation, legibility and feedback | German layout and non-mouse workflows tested in the actual session |
| Software delivery | Signed independent packages and installed-system updates | Dependencies/conflicts/ownership enforced; visible plans, progress and failure recovery |
| Kernel updates | Install alongside retained kernel while old kernel stays loaded | Reboot activation, explicit confirmation and usable previous-kernel recovery |
| Recovery | Saved-state selection, trial boot and restore | Consistent kernel/root/package/config set; interruption/failure behavior proven |

Current login and appearance are foundations, not completion of the full DE/WM,
Settings, Files or protected graphical architecture. See
[sessions/desktop checklist](components/SESSIONS-DESKTOP.md).

## Package and hosting requirements

Default repository identities include `core` and `extra`. Group related tools into
useful independently versioned packages such as base/network/filesystem tools as
the executable ABI permits. Dependencies and conflicts must be checked before
writes. Keep administrator settings and personal data separate from replaceable
package payloads.

The package server both builds and distributes. It runs in a dedicated Debian VM
with its own IP, reachable on LAN and through existing VPN/DNS/proxy infrastructure.
Initial public exposure is not required. Trusted package-specific tags trigger
build/validation/sign/publication; a manual trigger, useful logs and a lightweight
dashboard are required. Retain enough failed-build evidence while cleaning old
working files automatically. See [package design](PACKAGE-MANAGER.md) and
[server operations](PACKAGE-SERVER.md).

## Application identity and developer tools

Zenith-specific packages may use names such as Sunrise and Sunbeam, but retain
clear descriptions and familiar command names. Resource packages and metapackages
must be described honestly; branding is not executable separation.

Prefer a host GCC cross-development path first. Explore terminal editors such as
nano/Vim, Python and eventually on-system GCC after the ABI, libc/TTY and resource
requirements are established. Bash is a larger POSIX/process compatibility project.
Optional Doom work is separate from the main roadmap. Detailed possibilities and
naming remain in [Applications](APPLICATIONS.md) and [Bash feasibility](BASH-PORT.md).

## Recovery design constraints

Btrfs-like trial/restore is a requested experience, not a selected filesystem.
Retaining an old kernel does not revert modified system files or the package DB.
Define `/home`, account/configuration and service-data scope before choosing full
copies, A/B roots, block CoW or a new filesystem. Snapshotting does not replace
external backups. See [Recovery design](UPDATE-RECOVERY.md).

## Decisions still needed

- Native descriptor/stream/TTY and GUI transport contracts.
- Privileged service boundaries, restart and IPC ownership.
- Per-user configuration precedence and `/etc` merge policy.
- Snapshot backend, disk capacity, trial policy and mutable-data scope.
- Managed multicore/GC integration with Cosmos.

The [original detailed planning baseline](archive/2026-10-15/SYSTEM-PLAN.md)
preserves earlier acceptance discussions. Current component checklists and this
page supersede its delivery status; do not treat historical proposals as shipped.
