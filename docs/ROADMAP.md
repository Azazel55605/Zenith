# Roadmap

Reviewed **2026-10-26**. This page owns order and milestone exits, not the execution
log. Read [Status](STATUS.md) for delivery evidence and [Progress](PROGRESS.md) for
active work. Historical milestone version targets are not release versions.

## Now

1. **Close the selected CLI milestone.** Base/text/filesystem/network extraction
   is implemented and fully qualified. The operator accepted the process/file/text
   checks and confirms kernel 0.0.23 installed/boot-confirmed with net-tools 0.1.0;
   representative home-path network/TLS checks pass. Optional remaining installed
   replacement/cancellation exercises are listed in the [checklist](NETWORK-VM-TEST.md).
   Shell evaluation and privileged administration remain trusted services.
   [Active work](PROGRESS.md#cli-unbundling-and-application-foundations) separates
   local qualification, distributor delivery and installed acceptance.
2. **Deliver the graphical API, then build Settings.** The first owned surface/
   event API and independent probe pass full qualification. Complete its
   [VM acceptance](GRAPHICAL-VM-TEST.md), then implement typed authorized Settings
   services, restart semantics and shared client widgets/text/resources. Preserve
   the trusted test DE until independent compositor/session teardown is verified.
3. **Retain installed acceptance boundaries.** Operator reports installation;
   local QEMU tests and server publication are separate evidence. Continue
   representative installed permissions/input/update checks as features ship.

## Next: continue unbundling

| Order | Slice | Exit / dependency |
|---|---|---|
| 1 | Native input/write operator acceptance | Local common gate passed; delivery/update are operator reported; verify persistent writes/permissions on the installed VM |
| 2 | Native stream delivery | Full local gate passed; publish/confirm compatible kernel before updating tools and accepting operator behavior |
| 3 | Raw TTY/process lifecycle | Define terminal modes, wait/jobs and environment before larger CLI ports |
| 4 | More independent command packages | Move selected base/fs/network tools after their required ABI exists; update the executable without changing kernel bytes; keep recovery tools available |
| 5 | First protected graphical client | Versioned display/input/surface and IPC contract, per-client ownership and fault cleanup; client crash leaves login/session usable |
| 6 | Desktop and privileged service migration | Separate compositor/session/app responsibilities; restart and compatibility rules proven before replacing the temporary DE |

CLI packages take priority over GUI work, as confirmed on 2026-10-26. This sequence is not a
claim that all APIs or package names are settled. [Userland steps](USERLAND.md)
own the detailed gates. Managed multicore is a separate dependency track, not a
prerequisite for the next bounded file utility.

## Product tracks

| Track | Current position | Remaining delivery steps |
|---|---|---|
| Desktop/usability | Login, DE/local text selection and packaged appearance work | Shared UI/toolkit foundations → Settings → proper DE/automatic-tiling WM → Files/Editor overhaul → per-user customization/accessibility; GUI independence follows the native graphics contract |
| Package delivery | Installed updates and home automation work | Accept new ABI packages → improve update/restart/configuration handling → trust rotation/revocation → coordinated recovery; retain signatures and full release gates |
| Multicore | Opt-in native workers only | Wake/IPI and per-CPU state → GC barrier → pinned managed threads → migration/load balancing → contention/I/O/recovery acceptance |
| Recovery/storage | Retained kernels and undo, bounded ext2 | Decide snapshot scope/backend → clean root generations/trial/restore → evaluate efficient CoW; capacity changes need installer/checker/memory redesign |
| Developer tools | Host GCC/Clang builds freestanding Zenith executables | Cross-SDK/libc subset → terminal editor feasibility → Python runtime → on-system GCC if ABI/resources permit |
| Hardware | QEMU and operator VMware testing | Explicit device matrix → boot/storage/network/input validation → USB/physical installs and recovery |

Optional Doom work remains outside the main delivery sequence. Bash, Vim/nano,
Python and GCC are feasibility goals, not available Zenith packages. See
[applications](APPLICATIONS.md) and [Bash assessment](BASH-PORT.md).

## Existing milestone identities

| ID | Status within its original scope | Current owner / unfinished extension |
|---|---|---|
| M0 foundation | Implemented: repeatable build, host/native validation, panic/logging | [Development](DEVELOPMENT.md); latest hosted CI is not asserted here |
| M1 shell | Implemented: scripting, cancellation, layouts/history, editor/test DE | [Shell](components/SHELL.md); streaming/raw TTY remains open |
| M2 filesystem | Complete for supported ext2/accounts/access profile | [Storage/security](components/STORAGE-SECURITY.md); not general ext4 or journaled recovery |
| M3 networking | Complete for bounded IPv4 LAN client | [Networking](components/NETWORKING.md); not general network/server compatibility |
| M4 installer | Implemented and VM exercised | [Boot/recovery](components/BOOT-RECOVERY.md); snapshots/physical hardware are separate |
| M5 userland | In progress: protected foreground ELF plus bounded input/read/write file tools | [Userland](components/USERLAND.md); complete only after selected tools/services have controlled APIs and independent update/fault acceptance |
| M6 desktop | Foundations implemented; full replacement planned | [Sessions/desktop](components/SESSIONS-DESKTOP.md) |
| M7 hardware | Planned beyond the current VM profile | Device matrix and recovery acceptance needed |
| S sessions | Shared settings, login/local/serial hosts implemented | Settings UI, per-user preferences and independent session processes remain |
| P packages | Offline/online/update/distributor foundations implemented | [Packages](components/PACKAGES.md); future hardening is explicit there |
| R recovery | Kernel-only retention implemented | [Snapshot design](UPDATE-RECOVERY.md); matched system restore remains planned |

## Decisions before the corresponding work starts

- Raw TTY/job-control contract and scope of a small libc/SDK.
- GUI transport, compositor/toolkit boundary and privilege delegation.
- `/etc` merge policy and atomicity expectations across data and boot updates.
- Snapshot backend, mutable-data scope, capacity and failed-trial policy.
- Managed GC synchronization and upstream Cosmos integration strategy.

Preserve agreed requirements in [System plan](SYSTEM-PLAN.md). The
[previous roadmap](archive/2026-10-15/ROADMAP.md) retains original estimates,
upstream investigations and historical scope; its “next” and “pending” labels
are superseded by this page.
