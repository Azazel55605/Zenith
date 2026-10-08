# Implementation progress

## 2026-10-08 · Repository initialization

- Inspected the clean checkout, boot/storage architecture, host tests, smoke
  harness and milestone documentation. Existing Git history retained.
- Added shared `INSTRUCTIONS.md` with linked `AGENTS.md` and `CLAUDE.md` entry points.
- M0/M1 remain complete as recorded in the roadmap. M2 is the active milestone.

## M2 · Unix filesystem semantics (in progress)

| Slice | Status | Evidence / next step |
|---|---|---|
| `/proc`: meminfo, mounts, uptime, cmdline | Complete for initial scope | Read-only synthesized inodes, first-read snapshots, boot mount, six host tests and smoke coverage |
| `/dev`: null, zero, random, block devices | Partial: null/zero plus read-only block nodes | Dynamic disks/partitions implemented; entropy and raw writes pending |
| Read-write ext2 | Pending | On-disk metadata and driver tests before changing the default root |
| ext2 installer root | Pending | Depends on ext2 |
| Users/login, shadow, passwd, su, useradd | Pending | Requires persistent ownership and permission model |
| VFS permission enforcement | Pending | Audit every file-operation entry point |
| check-only fsck.ext2 | Pending | Depends on ext2 layout |
| Optional ext4 read-only | Deferred | After required M2 scope |

M2 is not complete until an installed ext2 system enforces real users and
permissions and reports accurate metadata through `ls -l`.

### First M2 slice · `/proc`

- Added a Cosmos `IVfsFilesystemType` implementation mounted after root and `/tmp`.
  `meminfo` reads page-allocator totals/free pages; `mounts` reads the VFS table;
  `uptime` uses the boot stopwatch; `cmdline` reads the Limine command-line pointer.
- Files are case-sensitive and read-only. Each open handle samples on its first
  nonempty read (or end-relative seek) and releases the snapshot on close. Supports
  partial reads, EOF and seek; metadata uses directory 0555 / file 0444 and zero virtual file size.
- Deliberate API limits: no CPU idle counter, so uptime has one elapsed-seconds
  field; no retained mount flags in Cosmos, so mount options describe the current
  proc (ro) and FAT (rw) drivers. No per-process entries until the process model.
- Validation: `dotnet restore tests/Zenith.Tests --ignore-failed-sources` passed;
  `dotnet test tests/Zenith.Tests --no-restore` passed **204/204**, including six
  filesystem tests. Existing xUnit1031 warnings remain in shell/script tests.
- `dotnet publish -c Debug -r linux-x64 -o output-x64 --no-restore` passed and
  produced the ISO/ELF. Existing Cosmos patcher/layer warnings remain.
- Both VSTest and the NativeAOT task host require local IPC unavailable inside
  the sandbox; rerunning outside it succeeded.
- The first QEMU run passed 29/30 checks: the expected `proc-write=1` was present
  in raw serial output but lost when the harness stripped an interleaved Cosmos
  log tag. Updated marker matching to check both raw and cleaned output; three
  Python regression tests pass and run in CI. The fresh full smoke run passed
  **31/31 checks**, including all six `/proc` checks, installation, panic and clean
  poweroff. Evidence: `/tmp/zenith-m2-smoke-final.log` and
  `/tmp/zenith-m2-smoke-final/serial.log` (local scratch artifacts, not committed).
  Nonempty boot-command-line contents are not yet covered by the guest harness.
- Next after this slice: `/dev` (null/zero first, then entropy and block-device
  semantics), followed by ext2 groundwork. M2's ext2/users/permissions exit remains open.

### Second M2 slice · `/dev/null`, `/dev/zero` and bounded binary copy

- Added a fixed `/dev` VFS mount after `/proc`, with character-device metadata
  (0666, conventional major/minor IDs) and read-only directory entries (0555).
  `null` always returns EOF; `zero` fills each requested buffer and never reaches
  EOF. Both discard writes, store no data and allow no-op seek. The pinned Cosmos
  PAL requires a zero-size truncation callback when opening for shell output;
  this is accepted as a no-op. Nonzero resizing and other metadata changes fail.
  Read-only mounts reject device writes. Namespace changes fail.
- Added `dd if=INPUT of=OUTPUT count=N [bs=N]`: binary copying with a required
  finite block count, 512-byte default blocks, a 1 MiB buffer limit, EOF handling,
  and cancellation between I/O calls. Operands are validated before output is
  truncated; identical and case-only aliases are rejected for current FAT roots. This is a documented subset
  with file/device operands, not a stdin/stdout implementation.
- Added a manual page and refreshed `man hier`. Text filters still read entire
  inputs; use bounded `dd` for `/dev/zero`, rather than `cat`/`head`.
- Validation: **223/223** C# tests pass (19 new cases), and **3/3** Python
  serial-matcher regressions pass. Existing xUnit1031 warnings remain unchanged.
  ISO build passed with the existing Cosmos warnings. Eight additional QEMU
  checks cover device I/O, denied namespace changes, FAT alias rejection and
  Ctrl+C cancellation. The initial guest run exposed the zero-truncation callback
  requirement (output redirection failed); it was stopped for the fix. The fresh
  full guest run passed **39/39 checks**, including all eight new checks, install,
  desktop interactions, panic and clean poweroff. Local evidence:
  `/tmp/zenith-dev-smoke-final.log`, `/tmp/zenith-dev-smoke-final/serial.log`,
  `/tmp/zenith-dev-tests.log` and `/tmp/zenith-dev-build.log` (not committed).
- Remaining `/dev` work: entropy source plus random-device contract, and block
  device I/O with partition bounds, mounted-volume safety and hot-unplug behavior.
  No random or raw-disk node is exposed by this slice.

### Third M2 slice · Read-only disks and partitions under `/dev`

- Added live disk/partition nodes using Cosmos names (`sata0`, `sata0p0`,
  `sata0p1`, etc.). The kernel captures storage tables once per enumeration and
  excludes partitions whose host is absent. Node identity persists while the
  device instance remains present; removed instances are evicted on enumeration.
- Block nodes expose 0444 block-device metadata, mount-local device IDs and byte
  capacity. Reads handle unaligned offsets and partial sectors, batch aligned
  sectors, return short reads at EOF, and cannot exceed device/partition bounds.
  Seeks accept positions from zero through capacity, rejecting overflow and
  out-of-range positions. Sector buffers are limited to 1 MiB; geometries whose
  byte capacity exceeds `long.MaxValue` are omitted.
- Raw writes and every truncation/metadata change are rejected, including when
  the source is unmounted. This avoids aliasing writes behind mounted filesystem
  caches. Writable raw devices need coordination with mounts/partition rescans
  before they can be enabled. Ordinary reads can observe concurrent filesystem
  changes; they are not a disk snapshot.
- Presence is checked by device reference before each backing read, seek and
  flush. Old handles fail after removal/rescan or same-name replacement; driver
  I/O failures propagate. Host tests simulate removal between sector calls.
  Physical USB unplug behavior still requires hardware/guest hotplug validation.
- Entropy finding: Cosmos' `InteropSysPlug` currently routes its secure-random
  plug to timer-mixed XorShift. No supported entropy service was found. Random
  nodes remain pending; do not use this plug for `/dev/random` or password salts.
- Validation: **240/240** C# tests pass (17 new block-device cases), and
  **3/3** Python harness tests pass. Restore succeeded from cache with NU1900
  because the NuGet vulnerability endpoint was unreachable; existing xUnit1031
  warnings remain. The first build permission review timed out before execution;
  its permitted retry built the ISO successfully (existing Cosmos warnings).
  The first guest run exposed a Cosmos cached-interface dispatch failure at cell
  `FFFFFFFF80380A60`, mapped by the ELF symbol table to the contravariant
  `ReferenceEqualityComparer` call through `IEqualityComparer<IBlockDevice>`.
  Replaced it with an explicitly typed identity comparer. The fresh full QEMU
  run passed **46/46 checks**, including all seven new disk/partition checks,
  existing character devices, installation, desktop interactions, panic and
  clean poweroff. Local evidence: `/tmp/zenith-block-tests.log`,
  `/tmp/zenith-block-build.log`, `/tmp/zenith-block-smoke-final.log`, and
  `/tmp/zenith-block-smoke-final/serial.log` (scratch artifacts, not committed).
- Remaining: kernel entropy service/random device, coordinated raw writes, then
  ext2 groundwork. Shell `stat`/`ls -l` still use their existing generic display;
  truthful type/owner/permission presentation remains part of the M2 exit work.
