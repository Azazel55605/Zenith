# Zenith project history

History through `8389113` (2026-10-11). This is a narrative and change map, not a claim
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
