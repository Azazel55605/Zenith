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
| `/dev`: null, zero, random, block devices | Partial: null/zero implemented | Null/zero and bounded dd validated; full suite 39/39. Entropy and block devices pending |
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
