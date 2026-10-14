# Zenith documentation

[Installation and boot generations](INSTALLATION.md): Sunrise/serial guided installation, self-contained UEFI boot and versioned boot layout.

[Signed kernel updates](KERNEL-UPDATES.md): offline signing/delivery, reboot activation,
confirmation, previous-kernel fallback and undo recovery.

[Signed online packages](ONLINE-PACKAGES.md): 0.0.7 private core/extra discovery,
dependency planning, named install/upgrade and kernel-update convenience.

This is the project documentation entry point. Status reviewed on **2026-10-11**
against the current repository. Current S0/S1/P0/P1 and M3 behavior is documented separately
from the future session/update plans. Milestone version labels are targets, not release dates.

[Multicore foundation](MULTICORE.md): opt-in native CPU workers, validation and
remaining managed scheduler/GC milestones.

## Reading paths

| You want to understand… | Start here |
|---|---|
| What works, how to boot and use it | [Project README](../README.md) |
| How the current system actually works | [Architecture](ARCHITECTURE.md) |
| Account setup, authentication and access limits | [Accounts](ACCOUNTS.md) |
| Shared settings and current session capabilities | [Settings/session foundation](SETTINGS-SESSIONS.md) |
| Next login/session and styling package slice | [Login/session manager plan](LOGIN-SESSIONS.md) |
| IPv4 configuration, diagnostics, time and HTTP/HTTPS downloads | [Networking](NETWORKING.md) |
| Offline package builder, inspect and dry-run | [P0 package prototype](PACKAGE-PROTOTYPE.md) |
| Desktop, headless, DE, tiling WM, settings and Files direction | [System plan](SYSTEM-PLAN.md) |
| Updating an installation and distributing applications | [Package manager design discussion](PACKAGE-MANAGER.md) |
| Moving desktop/apps outside the kernel | [Userland direction](USERLAND.md) |
| Serving packages from home | [LAN repository plan](PACKAGE-SERVER.md) |
| Managing distributor builds | [Dashboard setup](../deploy/distributor/DASHBOARD.md) |
| Saved-system boot, snapshot trials and restore | [Upgrade recovery](UPDATE-RECOVERY.md) |
| What happened and why | [Project history](HISTORY.md), then [slice evidence](PROGRESS.md) |
| What comes next and when a milestone is complete | [Roadmap](ROADMAP.md) |
| Building, testing, debugging and working with Cosmos | [Development guide](DEVELOPMENT.md) |
| Zenith app names, Files/Editor overhauls and GCC/Python/editor ports | [Application and developer-tool plan](APPLICATIONS.md) |
| Whether Bash can run here | [Bash portability assessment](BASH-PORT.md) |
| Earlier measurements and engineering background | [Technical notes](TECHNICAL-NOTES.md) |
| What the upstream framework documents | [Cosmos reference](cosmos/index.md), [pinned source](cosmos/SOURCE.md) |
| Rules for contributors and agents | [Shared instructions](../INSTRUCTIONS.md) |

## Status vocabulary and maintenance

- **Implemented** means code exists; **verified** means the stated scenario was tested.
- **Planned requirement** records requested product behavior without committing its API.
- **Proposed** means an architecture or scope recommendation awaiting discussion.
- **Deferred** means deliberately outside the next slice, not silently completed.

The architecture describes current code; the system plan describes product requirements;
the package document owns update design questions; the roadmap owns milestone exits;
PROGRESS records dated execution and evidence. Keep these roles separate so a proposal
cannot become a shipped claim through repetition. Historical test counts are snapshots.
Local validation does not establish that hosted CI passed after an unpushed commit.

M0/M1/M2 are complete within their documented scope. Bounded ext2, installed roots,
real accounts/authentication and filesystem access enforcement are verified;
[ACCOUNTS](ACCOUNTS.md) describes setup and the trusted-kernel boundary. The next
implementation slice must be selected explicitly from the expanded roadmap.

- [Signed offline packages](PACKAGES.md): core/extra, authenticated scripts/data installation, dependencies/conflicts and recovery.

- [Native programs](NATIVE-PROGRAMS.md): first protected x86_64 ELF ABI, signed
  executable installation, limits and remaining process/app migration.
