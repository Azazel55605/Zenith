# Zenith project history

History through M3 LAN client completion (2026-10-11). This is a narrative and change map, not a claim
that every planned feature shipped. Exact commits are available through `git log`;
[PROGRESS](PROGRESS.md) contains detailed M2 validation, failures and corrections.

## 2026-10-05: initial system and project identity

`85cc001` introduced a Cosmos Gen 3 kernel with graphical desktop, shell and installer.
`86bb815` separated milestone planning from engineering notes. `e1b728d` named the
project Zenith and added its logo. The original system already favored a Unix-style
filesystem tree over a drive-letter interface and an in-kernel graphical environment.
There was no independent program loader or userspace process model.

## M0: a repeatable development baseline

`c2e4120` added host tests, native QEMU smoke coverage, CI, a readable panic screen
and persistent logging. `757729b` adjusted runner disk/QEMU tooling. `cad3c33`
completed the initial development guide and explicit local/released Cosmos package
switching. The objective was reproducible change validation, not a production-ready OS.
Logs mirror serial output and persist boot.log/previous boot log when storage permits.

## M1: shell and desktop usability

The same day's M1 sequence made command execution cancellable and moved it to a worker
(`5e96d5a`), added saved keyboard layouts/time zones (`4ad10a7`), and coordinated clean
shutdown plus mount/sync operations (`4899d56`). Shell scripting (`a6741a9`) added a
parser/interpreter, control flow, functions, substitutions and script lookup.

The Editor replaced Notes (`63c373a`), manual pages documented the command surface
(`0246b7c`), windows became resizable/maximizable with terminal reflow (`e64fd4e`),
and selection/clipboard connected terminal and editor interactions (`51cbec9`).
Additional text utilities followed (`062a96c`); `c2586d4` recorded M1 completion.
`c7010a9` introduced narrowly scoped retries for the observed pre-Zenith Cosmos boot
fault in nested KVM. This retry is not evidence that arbitrary guest failures are ignored.

M1 did not introduce streaming pipes, raw-mode TUIs, general jobs/processes or isolated
apps. The editor remained graphical and all commands/apps remained kernel-linked.

## 2026-10-08: shared guidance and first M2 slices

`50773bf` added INSTRUCTIONS with AGENTS/CLAUDE entry points and progress tracking.
`dd7e067` added read-only proc files with per-open snapshots. `176bdd1` added null/zero
and bounded binary copying; `a23f303` added read-only live disk/partition byte streams
with capacity and removed-device checks. Raw writes and random devices stayed deferred.
Inspection found the installed secure-random plug unsuitable for password salts.

`42e8f56` integrated experimental ext2 secondary volumes using a bounded profile.
Native writes exposed an upstream allocator defect causing multiply-owned blocks.
A locally adapted driver with retained BSD notice made fixes reviewable without
changing global Cosmos packages. Host e2fsck became a required independent oracle
alongside guest assertions. The upstream revision and adaptation live in
[the ext2 source record](../src/Core/Storage/Ext2/SOURCE.md).

## 2026-10-09: lifecycle, test app and ext2 hardening

`5fd9040` fixed resize/deletion reclamation, sparse/indirect lifecycle and remount
accounting. `a31de27` documented Bash feasibility: a filesystem does not supply the
POSIX runtime/process/terminal interfaces Bash needs. This was research, not a port.

`8581398` added a deliberately simple Files desktop app for testing filesystem operations
and validated multi-group allocation. `a65feb2` added guarded ext2 formatting with
host interoperability and partition-boundary checks. Native large I/O exposed physical
page exhaustion even while managed allocation could still succeed; `cfafa3d` added
proactive reserve collection and bounded scratch-buffer allocation behavior.
This improved the tested workload without claiming general out-of-memory recovery.

`6aa5bcb` added correct inline/block-backed symlinks and shell link operations.
`50f2d17` persisted high UID/GID halves, modes and truthful metadata display. It also
fixed CI keyboard injection: explicit held/released keys and GC-aware pacing avoided
lost commands, while larger workloads retained their documented timeouts. Ownership
metadata was implemented; real identities and access enforcement were not.

## 2026-10-11: directory identity and installed ext2

`0b4ff64` made directory moves preserve inode identity and correctly update `..` and
parent links across directories, with ancestry/cycle guards. Shell directory moves
use rename; the Files GUI retains its more limited folder-transfer scope.

`8389113` completed the supported ext2 scope: new ext2 roots behind FAT32 ESP,
root recognition, durability ordering, check-only fsck, open-handle lifetime probe,
clean/unclean mount-state handling and independent disk-only EFI boot testing.
Existing FAT installations remain recognized. The installer still needs the host
boot-artifact helper. The checker never repairs; crash-dirty roots need host repair.

Final local evidence: 374 host tests, eight harness tests, Release publish, 135 baseline
and 135 large QEMU assertions and seven UEFI disk-only assertions. Root data, ownership,
modes and relative links persist over installed reboots; host e2fsck is clean after
orderly shutdown. One host assertion initially expected debugfs's mode string with an
extra zero; the matcher was corrected and complete profiles rerun. Hosted CI awaits
push; local validation is not a hosted-run result.

## 2026-10-11: M2 accounts and access completion

Added installed ext2 account state, locked-account bootstrap, hardware entropy,
PBKDF2-SHA256 passwords and hidden login/passwd/su prompts. Identity now follows
shell workers and GUI windows rather than mutable environment variables. Mounted
filesystem guards enforce owner/group/other access, creation ownership, sticky
removal, setgid inheritance and privileged system/device operations. `/tmp` is a
1777 RAM ext2 volume. Live/legacy FAT remains root maintenance mode.

Native auditing found and fixed handle retention after denied metadata/truncation
operations, authorization before deferred unlink, mount traversal gaps and ordinary
user logging context. Local evidence: 414 host tests, eight Python tests, 135
filesystem/installed-root checks at 512 MiB with the large workload, 32 account/access
checks, seven disk-only EFI checks and clean independent host e2fsck. M2's exit
criterion is met within the documented supported ext2 profile. Kernel code remains
trusted; process isolation and separate desktop/apps belong to future userland work.
See [ACCOUNTS](ACCOUNTS.md) and [PROGRESS](PROGRESS.md); hosted CI awaits push.

## The next chapter: planned system and distribution

The requested direction expands Zenith to real headless and graphical DE/auto-tiling
WM sessions, shared settings and a future Files rework. Package management should
allow existing installations to receive built artifacts rather than reinstalling
from an ISO. [SYSTEM-PLAN](SYSTEM-PLAN.md) records delivery slices and acceptance gates;
[PACKAGE-MANAGER](PACKAGE-MANAGER.md) records proposed design and unresolved choices.

M2 account/authentication and filesystem access work is complete. Networking,
self-contained installation, independent programs and real-hardware validation remain
future milestones. No package manager/mirror, headless session, tiling WM or Settings
app is marked implemented by this documentation work.

## 2026-10-11: shared settings/session seam and early packaging

Following M2, retained the floating DE for testing and extracted shared initialization
from desktop startup. A typed system-settings service now owns keyboard/timezone
persistence and versioned next-boot session defaults, with legacy representation,
aliases, bounded input, fallback and future-version protection. CLI operations share
that service. `DesktopSessionHost` owns graphics/fonts/windows; only DE is available.
S1 serial headless, user preferences and the proper independent DE/WM remain later.

Started P0 with a Linux directory-bundle builder, bounded shared manifest reader and
host/guest dry-run planner. The current kernel/base/test-DE are explicitly identified
as bundled components. Payload hashes, exact dependencies, runtime/architecture and
unowned-path conflicts are checked; no installed DB, extraction, signatures, networking
or activation exists. Local /etc and user/boot state are excluded from data payloads.
The format is experimental; production container/verifier and P1 recovery remain gates.
See [settings/session guide](SETTINGS-SESSIONS.md), [P0 contract](PACKAGE-PROTOTYPE.md)
and [PROGRESS](PROGRESS.md) for the implementation and exact validation boundary.

## 2026-10-11: x64 COM1 headless and serial recovery

Added the S1 host behind the shared session seam, retaining the test DE. Kernel boot
bypasses Cosmos's graphical console initializer; missing supported display hardware
selects headless with a diagnostic. Serial input uses bounded line/script/queue state,
one cancellable credential-scoped worker and bounded output with backpressure.
Existing scripts, read, system/filesystem/settings and accounts run without Desktop.
Password callbacks hide input; configured installed roots start as nobody; exit resets
the console and revokes identity. Console output bypasses persistent kernel logs.

Fatal early/headless errors print serial diagnostics and accept R to restart; graphical
panic failures use the same fallback. No-GPU QEMU live/disk-only UEFI acceptance and
independent ext2 checks are recorded in [PROGRESS](PROGRESS.md). ARM64 receive, real
UART hardware, local text consoles, SSH, service supervision and independent userland
remain later work. Packaging remains the P0 data prototype; production container/trust
and transactional installation are still explicit gates.

## 2026-10-11: M3 IPv4 lab foundation

Added shared boot DHCP, static/off persistent configuration, interface/route/DNS
diagnostics, correlated ICMP/DNS, TCP connection probes, optional NTP software UTC
and staged bounded HTTP transfers. Native faults exposed skipped unwind-finally,
typed catch selection and a TCP partial-read/append race in Cosmos 3.0.89. Explicit
owning-frame cleanup and a signature-checked local immutable bounded receive buffer
address those paths without changing upstream source or dependency versions. Local
QEMU/server fixtures cover malformed/disconnected/cancelled transfers and installed
persistence. TLS/hardened Internet transport and actual home DNS/VPN routing remain
open; HTTP does not authenticate packages. See [NETWORKING](NETWORKING.md) and
[PROGRESS](PROGRESS.md) for current bounds and validation evidence.


### M3 completion for LAN testing

Added verified HTTPS with a pinned offline Mbed TLS 3.6.7 source archive and a
fixed-memory native bridge, fingerprint-checked CA import, crypto known-answer
tests and fail-closed entropy/date/name/chain/record checks. Shared NTP now feeds
BCL and filesystem time. Build adapters guard packet parsing and bound UDP/ICMP
queues; configuration reload/DNS saves and user/TAP/off launch profiles support
home testing. Home DNS supplied as 10.0.10.3; no package VM exists yet. Detailed
validation and any remaining acceptance gates are tracked in [PROGRESS](PROGRESS.md).

## 2026-10-11: signed offline packages and migration sequencing

User requested default core/extra repositories, grouped base tools, dependency/conflict
handling and delivery. Chose script/data installation first, then native tool unbundling.
Added RSA-PSS signed containers, pinned public-key import, an installed ownership DB,
range/conflict checks and multi-package undo/commit transactions. Grouped base-tools,
net-tools and fs-tools remain image inventory until the native execution boundary exists.
Online signed indexes and dedicated VM publication follow separately. See
[PACKAGES](PACKAGES.md) and [PROGRESS](PROGRESS.md) for actual scope and acceptance.

## 2026-10-11: installed-system delivery order and future applications

User accepted self-contained ISO/install first, then package recipes/builder, a
server deployment kit for their VM, useful packages, online repository client, and
native tool unbundling. Server preparation can overlap installation work. Added
future GUI Editor overhaul, Zenith-specific celestial naming and GCC-preferred,
Python, Vim/nano research goals. Names and additional app ideas remain proposals;
no third-party port or naming availability is claimed. See [APPLICATIONS](APPLICATIONS.md).

## 2026-10-11: self-contained UEFI installation foundation

The CLI installer now copies its own original ELF and Limine UEFI bootloader through
local boot protocol requests. New installations have a 128 MiB ESP, versioned kernel
paths, root/ESP inventory and exact root PARTUUID selection. Explicit RAM rescue
works with an installed disk attached. Host boot-file repair was removed from acceptance
harnesses and replaced with independent read-only artifact checks. The layout anticipates
signed kernel packages that stage a new version while the old kernel runs until reboot;
the activation executor and graphical installer remain future slices. See
[INSTALLATION](INSTALLATION.md) and [PROGRESS](PROGRESS.md).

## 2026-10-11: Sunrise and guided initial account setup

Added a bundled Sunrise installer with disk selection, hostname/account fields,
explicit erase confirmation and worker progress. Serial `install DISK --setup` uses
the same target configuration. Both prepare password hashes before erasure, create
private password records and homes, and preserve the live session. Files and Editor
now use the installed account's home. Native acceptance covers rejected setup,
secret-free transcripts, two disk-only UEFI boots and independent ext2 permissions.
The user requires working kernel package updates before actual installation; offline
signed staging, previous-kernel retention and reboot activation are now the next
deployment gate. This slice does not implement that updater.

## 2026-10-11: signed kernel package activation and recovery

Added a dedicated signed kernel container, host sealing/checking commands and an
installed-root coordinator. The new ELF is staged while the loaded kernel keeps
running; versioned paths and both Limine menu search paths select it after reboot.
Explicit confirmation retains the previous kernel, rollback preserves the version
floor, and a durable journal supports interrupted-update undo. P1 remains scripts/data.

Real ELF-sized delivery required TCP payload acknowledgements and receive-window
backpressure in signature-checked build copies of Cosmos. Forced-termination tests
also required a read-only consistency audit before booting a dirty ext2 root; corrupt
or error-marked roots remain refused. This is a shared-root kernel update foundation,
with manual failed-boot fallback and external ISO recovery, rather than snapshots or
automatic trial-boot rollback. See [KERNEL-UPDATES](KERNEL-UPDATES.md) and exact native
acceptance evidence in [PROGRESS](PROGRESS.md).
