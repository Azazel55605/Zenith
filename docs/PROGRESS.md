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
| Read-write ext2 | Partial: experimental secondary volumes | Explicit `mount -t ext2`, profile gate, resize/deletion reclamation, persisted allocator counters and two-group fixture; root migration pending |
| Files desktop app | Complete for initial scope | 292 host tests; native keyboard/mouse workflow and independent persisted-file check pass |
| ext2 formatter | Verified for bounded secondary partitions | 306 host tests; 88/88 baseline + 88/88 large-profile QEMU checks; clean independent e2fsck |
| Runtime heap under large ext2 I/O | Verified for current stress workload | Proactive physical-page reserve; 9 MiB write/delete and subsequent commands pass at 512 MiB; general graceful OOM remains open |
| ext2 symbolic links | Verified for bounded targets | 332 host tests and 101/101 QEMU checks; inline/block-backed targets, dangling links, loops, remount and deletion; .. resolution remains unsupported |
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

### Fourth M2 slice · Experimental ext2 secondary volumes

- Verified the installed Cosmos 3.0.89 package already contains an experimental
  ext2 driver. Initial integration exposed an allocator defect; Zenith now
  adapts the upstream source locally, retaining its BSD license and source
  revision (also available through `man licenses`). Package versions and the
  sibling Cosmos checkout are unchanged. `mount -t ext2 PARTITION DIRECTORY` explicitly selects it;
  the existing two-operand mount still defaults to FAT. The mount table now
  names proc/dev/ext2 correctly. Added `man mount` and updated developer docs.
- Added a host-testable superblock gate: clean revision 1, 1 KiB blocks,
  128-byte inodes, filetype-only features, basic geometry and capacity checks.
  Unsupported devices are rejected before reading; probing reads only the
  superblock and performs no writes. This is a profile check, not fsck or a
  guarantee of consistency for arbitrary images.
- The QEMU harness creates a second disposable disk with an MBR Linux partition,
  formats it independently with host `mke2fs`, and seeds a host-created file.
  Guest checks cover rejecting FAT as ext2, reading host content, file/directory
  creation, case-sensitive names, rename/delete, a 20 KiB file using indirect
  blocks, remount persistence and final unmount. After QEMU exits, check-only
  host `e2fsck -f -n` and `debugfs` verify consistency and persisted file content.
  CI installs e2fsprogs; `--keep` retains the ext2 disk and check log.
- Validation: **265/265** C# tests (25 new cases), **3/3** Python harness
  tests, ISO build and pristine host-fixture e2fsck pass. Existing xUnit1031,
  Cosmos build and cached NU1900 vulnerability-endpoint warnings remain.
  The first QEMU attempts exposed automatic IDE port conflicts and then a
  changed disk enumeration order. Both disks now have explicit free SATA
  ports and units. Startup stderr is reported, and failed boots count skipped
  command checks as failures instead of reporting them as passes.
- The initial complete guest run passed all guest assertions but failed host
  e2fsck (**55/56**): the first new file reused the host seed's block, with
  multiply-claimed blocks and a bitmap mismatch. The allocator omitted
  `FirstDataBlock` when translating group bitmap bits. The local adaptation
  corrects allocation/free offsets, last-group bounds and group counting.
  Regression tests preserve an existing block, verify allocate/free/reuse,
  exclude the pre-data block from group counts, and reject allocation beyond
  the final block. Host verification also checks the seed remains unchanged.
- The fresh full QEMU run with the allocator correction passed **56/56**
  checks, including independent e2fsck (exit 0), persisted content and unchanged
  host seed. The final ISO build also includes the bundled license manual page.
  Local evidence: `/tmp/zenith-ext2-tests.log`, `/tmp/zenith-ext2-build.log`,
  `/tmp/zenith-ext2-fixed-smoke.log`, and
  `/tmp/zenith-ext2-fixed-smoke/ext2-check.log` / `ext2-disk.img` (not committed).
- Root discovery and installation remain FAT32. Next ext2 work: validate broader
  inode semantics (symlinks, ownership/mode changes and timestamps), multiple
  block groups, allocation limits and formatter interoperability before moving
  the installer/root. Users/permissions, check-only guest fsck and entropy remain
  pending; M2 stays open.

### 2026-10-09 · Fifth M2 slice · Ext2 resize and deletion lifecycle

- Replaced truncation's logical-block scan with bounded-depth pointer-tree
  pruning. Shrinking/unlinking releases data and empty indirect tables, preserves
  retained sparse branches and keeps inode sector counts accurate. Both shrink
  and growth clear the partial-block tail so bytes beyond the old EOF cannot
  reappear. Fast symlink deletion never interprets inline target bytes as blocks;
  generic size changes on symlink inodes are rejected.
- Successful overwrites now update mtime and ctime as well as extending writes;
  actual size changes update both timestamps. Empty tables from failed/short
  allocation are reclaimed and persisted. Writes and size changes reject sizes
  beyond the direct/single/double mapper (67,383,296 bytes for 1 KiB blocks).
  Triple-indirect file I/O remains unsupported; this does not implement permissions.
- Added 13 host lifecycle cases. The initial seven cases reproduced **six
  failures** in the previous driver: leaked single/double tables, stale tail
  bytes across remount, unsafe fast-link block freeing and unchanged overwrite
  timestamps. The corrected full suite passes **278/278**; **3/3** Python
  harness regressions pass. Existing xUnit1031 and cached NU1900 warnings remain.
- Expanded the independent host fixture with a 20 KiB file to delete and a sparse
  file whose last block uses double indirection. Seven added guest checks cover
  truncation, deletion, a 300 KiB double-indirect file create/delete cycle,
  empty directory removal, and truncated contents after remount. Host verification requires clean check-only
  e2fsck plus exact single-data-block counts and absent indirect pointers in
  retained truncated files; it still checks persisted content and the host seed.
- The first integration run passed guest checks but failed host e2fsck
  (**61/62**): a freed inode had zero deletion time. Inode teardown now persists
  zero links and dtime before freeing its bitmap bit. Directory removal uses the
  same tree reclamation path rather than freeing only its first block. Added
  regressions for deletion metadata after remount and expanded empty directories.
- The final ISO build and fresh full QEMU run pass **63/63** checks, including
  all seven new guest checks, clean independent e2fsck (exit 0), exact retained
  inode block counts, persisted content, unchanged host seed, existing install,
  desktop, panic and clean poweroff. Local evidence:
  `/tmp/zenith-ext2-lifecycle-tests.log`, `/tmp/zenith-ext2-lifecycle-build.log`,
  `/tmp/zenith-ext2-lifecycle-smoke-fixed.log`, and
  `/tmp/zenith-ext2-lifecycle-smoke-fixed/ext2-check.log` / `ext2-disk.img`
  (scratch artifacts, not committed). Existing Cosmos build warnings remain.
- Next: formatter interoperability and multiple block-group allocation, then
  ownership/mode/symlink and open-handle unlink behavior before installer and
  root migration. Installed roots still use FAT32; M2 remains open.

### 2026-10-09 · Bash portability assessment (implementation paused for review)

- Inspected Zenith command dispatch, buffered pipelines, terminal input and the
  Cosmos native/PAL interfaces, and checked Bash execution sources plus GNU build
  options and wasi-libc headers. Recorded [BASH-PORT.md](BASH-PORT.md).
- Bash is technically feasible as a later program-runtime target, but current
  Zenith lacks the C/POSIX execution contract. Standard WASI preview 1 does not
  supply fork/exec or terminal job-control semantics; M5 needs an explicit design
  extension if real Bash is a requirement.
- Assessment only: no Bash cross-build or guest execution, no kernel changes,
  and no roadmap/runtime architecture switch. M2's next implementation slice
  remains formatter/multiple-group validation after this review.

### 2026-10-09 · Sixth M2 slice · Files desktop app and multi-group ext2

- Added a small Files window in the launcher and `open files [DIRECTORY]`, with
  folder navigation, selected-item opening in Editor, file/folder creation,
  rename, file copy/move, paste to the chosen location, refresh and confirmed
  deletion of files or empty folders. Errors remain visible in the status line.
  Name validation and overwrite refusal preserve existing files; transfer state
  belongs to each window. `man files` and README document the controls.
- Plain .NET `FileBrowser` keeps operations host-testable. File copying is bounded
  to 8 MiB, Editor opening to 128 KiB; known virtual/device paths and final
  symlinks are rejected for copy/open. Folder copying and moves between parent
  directories are intentionally unavailable while ext2 directory-move parent
  link accounting remains unvalidated. Folder rename stays available. Operations
  run synchronously; this app is built into the desktop, not a separate program
  runtime or a users/permissions boundary.
- Expanded the independently formatted scratch ext2 volume to 16 MiB/two block
  groups, with an 8 MiB host filler exhausting the first group. Host verification
  additionally requires the retained guest-created inode to point into group two,
  and exact file manager text content after unmount and VM shutdown.
- A new synthetic full-first-group/remount regression reproduced a persisted
  counter mismatch (expected 25 free blocks, observed 26). Block and inode
  allocate/free paths now update volume counts before persisting group descriptors
  and superblock. Regressions cover second-group allocation/free/reuse and inode
  counts across remount. The complete host suite passes **292/292**; harness
  regressions pass **3/3**. The native ISO build succeeds; existing Cosmos and
  xUnit warnings remain.
- Initial native create/edit/rename/copy/move checks passed, but Delete was ignored
  by the bundled Cosmos virtio keyboard driver (its Linux-keycode mapping lacks
  KEY_DELETE). Added Ctrl+D for the same confirmation, retaining the toolbar and
  Delete handling on supported inputs. The expanded harness checks actual toolbar
  mouse deletion, keyboard confirmation/cancellation, overwrite refusal and the
  saved result. All **72/72 native checks** pass, including existing install,
  desktop, panic and poweroff. Visual inspection confirms the toolbar, selected
  filename/size and visible result status.
- The full run initially reported **72/73** because the new host checker expected
  saved text without a newline; `TextDocument.Save` deliberately appends one.
  Corrected the exact expected content to `file-manager-data\n`, then reran only
  the independent host verification on the retained disk: **1/1** passes, with
  check-only e2fsck exit 0, unchanged host seed, exact truncated inode counts,
  second-group allocation (retained data block 8787) and saved Editor text.
  All **73 checks** are now validated; no guest code changed after that run.
  Evidence: `/tmp/zenith-files-tests.log`, `/tmp/zenith-files-build.log`,
  `/tmp/zenith-files-smoke-final.log` (initial checker result), and
  `/tmp/zenith-files-smoke-final/ext2-check.log`, `ext2-disk.img`, `files.ppm`
  / `files.png` (retained scratch artifacts, not committed).
- Next: formatter interoperability, ownership/mode/symlink semantics, directory
  parent-link accounting and open-handle unlink behavior before installer/root
  migration. Installed roots still use FAT32; users/permissions and check-only
  guest fsck remain pending. M2 remains open.


### 2026-10-09 · Seventh M2 slice · Guarded ext2 formatter

- Added a local formatter for the existing profile: revision 1, 1 KiB blocks,
  128-byte inodes, filetype-only features, 8192 blocks/1024 inodes per group.
  Geometry is bounded to 1..512 MiB and fully preflighted, including partial final
  groups. Labels are validated printable ASCII, at most 16 bytes. Counts include
  reserved inodes and root/lost+found; bitmap padding is allocated, and every
  group has a correctly numbered superblock and descriptor-table backup.
- Added `mkfs.ext2 PARTITION [--yes] [-L LABEL]`. Formatting requires explicit
  confirmation and a named partition, refuses mounted/overlapping partitions
  and the running root disk, and serializes with secondary mount/unmount calls.
  Generic VFS formatting remains disabled. The primary signature is invalidated
  and flushed before metadata writes, and clean primary metadata is published
  last after flushes; interrupted metadata-write coverage verifies mount refusal.
  This is not a journal or secure erase; UUID awaits entropy support.
- All **306/306** host tests pass, including six independent e2fsck format cases
  from 1 MiB to 264 MiB (33 groups/two descriptor blocks), first-data-block and
  partial-group boundaries, 512/1024-byte sectors, allocation/free across remount,
  six no-write preflight refusals, an injected write failure and a scratch
  allocation-budget regression. CI host tests now
  explicitly install e2fsprogs. Harness regressions pass **3/3**. Native ISO build
  succeeds; existing cached NU1900, xUnit and Cosmos build warnings remain.
- Expanded QEMU with an initially blank disposable partition, formatter refusal
  checks, guest creation/remount and 9 MiB allocation/deletion across group two.
  Independent host checks require clean e2fsck, saved files, label, group-two data
  and unchanged partition-exterior guards/MBR. The second boot now uses the
  installed FAT scratch disk to exercise running-root-disk protection and clean
  poweroff. The first refusal test incorrectly targeted the already-unmounted
  installed scratch partition; it now explicitly remounts before testing refusal.
- The 9 MiB stress write exceeded both the default 60-second and CI's 180-second
  command budget. Read-only inspection showed it still advancing but slowing
  substantially (5,951,488 bytes after about 230 seconds). A new host regression
  measured **4,292,608 allocated bytes for 1,024 blocks**, matching four temporary
  arrays per block. Bitmap/zero/GDT/superblock scratch storage is now reused;
  metadata is still reread and persistence ordering unchanged. The same host
  regression now passes a **256 KiB** allocation ceiling. The unchanged
  9 MiB/1 KiB-block workload was rerun with CI's existing 180-second budget;
  native results are recorded below.
- With scratch reuse, the 9 MiB write/delete completes in about 50 seconds.
  The next command failed after runtime exhaustion: `PageAllocator` reported two
  free pages, then a 65-page thread-stack allocation faulted. ELF symbolication
  points to `ThreadContext.Initialize` / `SystemNative_CreateThread` / `Thread.StartCore`.
  A read-only snapshot still passes e2fsck and contains saved text. This is a real
  Cosmos runtime/heap limit at 512 MiB, not a clean low-memory exit; it remains open.
  Added an explicit `--memory 1024 --large-ext2-stress` profile for the large
  workload, preserving baseline 512 MiB RAM/60-second timeout and CI's existing
  180-second timeout. CI runs both profiles and retains both sets of artifacts.
- The final native ISO passes **88/88 baseline checks at 512 MiB** and **88/88
  large-profile checks at 1 GiB**. Both profiles verify formatting/refusals, saved
  contents after remount, clean independent e2fsck on both ext2 volumes, unchanged
  partition-exterior guards and MBR, actual installed FAT root selection, root-disk
  formatting refusal and clean poweroff. The large profile additionally verifies
  a retained data block in the second group after 9 MiB allocation/deletion.
  Host tests pass **306/306** and harness regressions **3/3**. Evidence:
  `/tmp/zenith-mkfs-tests.log`, `/tmp/zenith-mkfs-build.log`,
  `/tmp/zenith-mkfs-allocation-before.log`, `/tmp/zenith-mkfs-baseline.log`,
  `/tmp/zenith-mkfs-large.log`; retained disks/check reports and installed-boot
  serial logs are under `/tmp/zenith-mkfs-baseline/` and `/tmp/zenith-mkfs-large/`.
  The earlier 512 MiB large-workload failure remains evidenced in
  `/tmp/zenith-mkfs-smoke-fixed/serial.log`; scratch artifacts are not committed.
- Installer/root discovery remains FAT32. Next: investigate runtime heap growth
  and low-memory failure handling, then ownership/mode/symlink semantics,
  directory parent-link accounting and open-handle unlink behavior before root
  migration. Users/permissions and check-only guest fsck remain pending. M2 stays open.

### 2026-10-09 · Eighth M2 slice · Physical-memory reserve

- Inspected the installed 3.0.89 Core/HAL/System assemblies with Mono.Cecil,
  rather than assuming the sibling reference checkout matches. `AllocObjectSlow`
  collects only after TLAB refill fails; SATA ReadBlock/WriteBlock use spans and
  the existing reusable DMA path. The earlier serial trace had no collections
  before physical-page exhaustion and the following native stack allocation fault.
- Added a desktop-loop pressure check at one eighth of physical pages free,
  throttled to one collection per second. The check allocates nothing; collection
  logs before/after free pages. This reserves headroom for native allocations
  during a responsive GUI workload; it is not a general graceful-OOM solution.
- Enabled the 9 MiB ext2 smoke profile at the normal 512 MiB memory setting.
  Host suite passes 309/309, including
  reserve boundary, throttling/recovery and unavailable-metric cases. Python
  serial matcher suite passes 3/3.
- Native publish succeeds. Full **88/88** smoke checks pass with
  `--large-ext2-stress --memory 512 --step-timeout 180`, including commands
  after the 9 MiB allocation/deletion, remount reads, installed FAT root reboot,
  root-disk format refusal and clean poweroff. Independent e2fsck checks pass
  for both volumes, and retained data is in block group two with partition
  guards intact. One pressure collection raised free pages **15,752 -> 94,302**
  (about 307 MiB reclaimed). Lowest logged allocation-time free count: **15,752**.
- Evidence: `/tmp/zenith-pressure-tests.log`, `/tmp/zenith-pressure-build.log`,
  `/tmp/zenith-pressure-smoke.log` and `/tmp/zenith-pressure-smoke/` (serial,
  installed serial, screenshots, disposable images and independent reports).
- CI now runs the large profile at 512 MiB. This addresses the observed
  late-collection failure; it does not repair every possible runtime allocation
  failure or establish arbitrary live-set OOM safety. M2 remains open. Next:
  inode ownership/mode/symlink semantics, then permissions and ext2 root migration.

### 2026-10-09 · Ninth M2 slice · Bounded ext2 symbolic links

- Audit found symlink creation storing long-target blocks directly in all 15
  i_block slots (including indirect slots, with a 16th out-of-range access), and
  treating 60 bytes as an inline target without room for the terminator. Changed
  creation to Linux ext2's one-block target limit (1..1023 UTF-8 bytes here), with
  targets below 60 bytes inline and larger targets in one data block; mode 0777.
- Reads use i_blocks to distinguish storage, including imported short links
  backed by a block, and bound sizes before allocation. Empty, embedded-NUL and
  oversized targets are refused. Creation and namespace mutation validate names
  as non-dot components of at most 255 UTF-8 bytes, preventing inode allocation
  before rejection. Added host regressions and independent fsck checks for
  1/59/60/61/1023-byte targets, remount and deletion reclamation, multibyte
  boundaries, invalid names/targets, corrupt sizes and disk-full cleanup.
- Added guest `ln -s TARGET LINK`, `readlink LINK` and `unlink FILE` using the
  installed 3.0.89 directory-handle API (verified via Mono.Cecil). Final-entry
  lookup does not follow links; dangling links can be inspected and removed.
  Existing destinations are refused; hard links/FAT symlinks remain unsupported.
- Extended QEMU coverage and independent debugfs/raw-block verification for
  relative, absolute, directory, dangling and block-backed targets, link loops,
  duplicate refusal, removal preserving the target and remount behavior.
- Ownership/mode mutation remains the next slice. The installed VFS rejects ..
  in symlink targets and limits resolution to eight hops; ls -l still needs real
  metadata. Documented these limits in README/man ln.
- Initial native run passed link creation/inspection but exposed a harness
  mistake: `cat` reports absent files yet currently returns status 0. Stopped
  that run and changed dangling/loop assertions to `test -f`, which exercises
  VFS target resolution without relying on cat's error status. Shell filter
  error-status propagation remains a separate follow-up. Interrupted-run
  evidence is retained under `/tmp/zenith-links-smoke/`.
- Final validation: **332/332 host tests**, **3/3 Python matcher tests**, and
  native Debug publish pass. Full **101/101 QEMU checks** pass with the 9 MiB
  workload at 512 MiB RAM, including all link operations, remount reads, installed
  FAT-root reboot and clean poweroff. Independent e2fsck is clean for both ext2
  volumes. debugfs/raw-block checks confirm link type, mode 0777, exact targets,
  terminating NUL for the slow link, and absence of deleted temporary/cyclic links;
  retained regular data remains in group two and partition guards are untouched.
- Evidence: `/tmp/zenith-links-tests.log`, `/tmp/zenith-links-build.log`,
  `/tmp/zenith-links-smoke-verified.log` and `/tmp/zenith-links-smoke-verified/`.
  CI's existing 512 MiB large profile now exercises these checks automatically.
- M2 remains open. Next: persisted ownership/mode updates and truthful stat/ls
  output. Directory cross-parent link accounting, open-handle unlink semantics,
  users/enforcement and installed ext2 remain pending before root migration.
