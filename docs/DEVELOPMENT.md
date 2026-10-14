# Developing Zenith

For a desktop progress screenshot and a completed local transfer, run
`python3 -B tools/terminal-progress-smoke.py obj/progress-ui-visual` (requires
Pillow). It serves a deliberately paced 2 MiB fixture and retains `progress.png`
for visual inspection; its guest byte-count check tolerates wc's padded output.

## Validation disk cleanup

Smoke runs retain disposable disk/partition images under `obj/`; sparse images can
appear much larger than their allocated space. Compare `du -sh obj` and
`du -sh --apparent-size obj`. After all validation processes have stopped, run:

```sh
python3 -B tools/prune-validation.py          # preview generated images
python3 -B tools/prune-validation.py --apply  # remove only listed validation images
```

This retains logs, screenshots, PCAPs, build caches, package archives, the local
update relay and `vmware-recovery-*` directories. Old retained images are no longer
available for rerunning offline audits; rerun the harness to recreate them. Do not
remove all of `obj/` during an update: the local relay and recovery copies live there.

## Toolchain

For local download performance, run `python3 -B tools/transfer-bench.py obj/transfer-bench`.
It compares a burst HTTP payload to RAM ext2 and a disposable installed disk,
records timings/throughput and Ethernet PCAPs, verifies local TCP windows and exact stored bytes, and independently audits
ext2 after shutdown. `--disk-mib 16` exercises kernel-sized transfers while keeping
the RAM test at 4 MiB. Timings include guest shell completion and are diagnostic,
not a portable hardware performance guarantee. Set `QEMU` to the selected emulator
or an explicit TCG wrapper for like-for-like measurements.

| Tool | Used for |
|---|---|
| .NET SDK 10 (system `dotnet`) | Building Zenith and running the unit tests |
| System Clang and `nm` (binutils), OpenSSL for tests | Freestanding TLS compilation/import checks and host certificate fixtures |
| `Cosmos.Tools` + `Cosmos.Patcher` global tools, `cosmos install -y --tools` | The Cosmos build pipeline (ILC, patcher, clang/lld, xorriso) |
| `qemu-system-x86_64`, OVMF (`edk2-ovmf`), mtools, parted, e2fsprogs | Running, installing, smoke testing and host formatter validation |
| Python 3.10+ | `tools/smoke-test.py`, `tools/gen-fonts.py` (needs `fonttools`) |

## Everyday loop

```sh
dotnet publish -c Debug -r linux-x64 -o output-x64   # → output-x64/Zenith.iso   (VS Code: Build x64)
tools/run.sh live                                    # boot it                    (Run QEMU x64)
dotnet test tests/Zenith.Tests                       # host unit tests            (Unit Tests)
python3 -B tools/test_smoke_test.py                  # serial matcher regressions
tools/smoke-test.py                                  # boot test in QEMU          (Smoke Test (QEMU))
```

If the build says `cosmos.patcher: command not found`, add `~/.dotnet/tools` to `PATH`.

**What to test where:**
- **Unit tests** (`tests/Zenith.Tests`) compile the kernel's plain-.NET sources directly: the
  shell, parser, commands and terminal buffer. Anything under `src/Core/Shell` must stay free of
  Cosmos types for this to work; Cosmos-dependent commands live in `SystemCommands.cs`, which
  the kernel registers at boot.
- **Filesystem contract tests** link the proc/dev drivers, including the block
  byte-stream adapter and ext2 superblock profile gate, and download only the Cosmos HAL contract assemblies via `PackageDownload`. This exercises the actual driver
  interfaces on the host without importing Cosmos build targets or calling hardware.
  Formatter tests also require `e2fsck` (e2fsprogs) and check independently readable
  sparse scratch volumes from 1 MiB through 33 groups, including partial final
  groups, 512/1024-byte sectors and a descriptor table spanning two blocks.
- **The smoke test** boots the real ISO headless, types into the Terminal through the QEMU
  monitor, and checks the kernel log on the serial port. Commands report back with `logger`.
  Add a `(command, expected log text)` pair to `STEPS` in `tools/smoke-test.py` for new
  end-to-end behavior. The serial port is ASCII-only, so avoid box-drawing characters in
  expected text.

## Ext2 secondary volumes

`mount -t ext2 PARTITION DIRECTORY` explicitly selects Zenith's adaptation of the experimental Cosmos
ext2 driver. The local source and BSD license are recorded in
[src/Core/Storage/Ext2/SOURCE.md](../src/Core/Storage/Ext2/SOURCE.md).
The bitmap allocator fixes account for the first data block; `man licenses`
also carries the upstream notice in live and installed systems. `mount PARTITION DIRECTORY` still selects FAT. The mount
point must exist; already-mounted partitions and unsupported filesystem types
are rejected. New installations and root discovery support the bounded ext2 profile; older FAT roots remain recognized.

The first supported profile is deliberately narrow: revision 1, 1 KiB blocks,
128-byte inodes, filetype-only features, and a clean superblock. The gate checks
these fields plus basic geometry before invoking the driver. It is **not fsck**
and does not establish consistency of arbitrary images. Field definitions follow
the [Linux filesystem superblock documentation](https://docs.kernel.org/filesystems/ext4/super.html).
Resize/deletion reclaim data and empty indirect tables, including sparse files;
freed inodes retain deletion time with zero links, and
partial-block tails are zeroed before later growth can expose them. Failed or
short writes clean unfinished pointer tables. Writes and size changes are limited
to 67,383,296 bytes at the supported block size (direct, single and double indirect
mapping); triple-indirect file I/O remains unsupported. Size changes to symlink
inodes are rejected, and deletion treats fast symlink targets as inline bytes.
Symlink resolution, ownership/mode changes, permission enforcement, directory
parent-link accounting is validated; journal-free crash recovery still requires host repair.

The smoke harness creates a second disposable SATA disk with an MBR Linux
partition. `tools/ext2_fixture.py` formats only that scratch image using
`mke2fs -t ext2 -b 1024 -I 128 -O none,filetype`, with host-created seed files.
The 16 MiB volume has two block groups;
an 8 MiB filler exhausts the first group before guest allocation.
Guest checks cover reads, writes, case-sensitive names, rename/delete, indirect
blocks, truncation, sparse and double-indirect deletion, and unmount/remount.
Files window checks create a directory and file, edit/save, cancel deletion,
rename, reject overwrite, copy, move, and delete a file and empty folder.
Host `debugfs` also checks that truncated files have exactly one allocated data
block and no indirect tables, that the retained guest-created file has a data
block in the second group, and that the file manager's saved text persists.
After QEMU exits, the harness extracts the partition,
runs **check-only** `e2fsck -f -n`, and uses `debugfs` to verify persisted content.
Both guest checks and the independent host check must pass. `--keep DIR` retains
`ext2-check.log` and `ext2-disk.img` along with the serial log, final screenshot
and Files window screenshot (`files.ppm`).
The fixture requires `mke2fs`, `e2fsck` and `debugfs` from e2fsprogs; CI installs it.

A third disposable disk holds an initially blank ext2-target partition. Only the
guest formats it, via `mkfs.ext2`. The harness checks refusal without confirmation,
invalid labels, whole disks and mounted volumes; creates nested files; allocates
small files and checks contents after remount. The optional
`--large-ext2-stress --memory 512 --step-timeout 180` profile additionally
allocates/deletes 9 MiB and requires a retained data block in group two.
The baseline retains its 512 MiB RAM and 60-second local command timeout;
CI uses its existing 180-second command timeout.
Host `e2fsck -f -n`, `debugfs` and `dumpe2fs` check the new volume, its label,
saved data (including second-group placement in the large profile) and untouched MBR/guard sectors outside the partition.
`--keep DIR` also retains `format-check.log` and `format-disk.img`.
Two subsequent boots use the installed ext2 scratch disk: they select the root,
verify data/ownership/symlink persistence, refuse root-disk formatting and checking
a mounted root, and power off cleanly. Host e2fsck inspects the resulting root.
It retains `installed-serial.log` when requested. No real disks are formatted.
The desktop checks physical-memory pressure before each tick. When free pages
fall to one eighth of the total, it requests a collection through the installed
Cosmos `MemoryInfo` API and logs free pages before/after. Attempts are limited to
one per second. Cosmos 3.0.89 otherwise collects only when managed allocation
fails; native thread stacks can exhaust physical pages first. The large profile
runs at 512 MiB in CI to exercise this path. This is a headroom policy, not a
guarantee of graceful recovery when the live working set exceeds available RAM.
The guest symlink checks create relative, absolute, directory, dangling and
block-backed links; reject duplicate names and unsupported FAT creation; bound
loop resolution; remove links while preserving their target; and reread after
remount. The host checks link type/mode, inline target text, raw block target and
terminator, and absence of deleted links using debugfs, alongside e2fsck.
`ln -s TARGET LINK`, `readlink LINK` and `unlink FILE` use directory handles so
inspection/removal does not follow the final link. Target lengths use UTF-8
bytes: 1..59 inline, 60..1023 in one block. Names reject dot components, slash,
NUL and more than 255 UTF-8 bytes before allocation. The installed VFS rejects
`..` inside target paths and caps resolution at eight hops. No hard links or
permission enforcement is claimed by this slice.
The formatter is currently limited to the profile above and 1..512 MiB partitions;
preflight rejects a final group too short for metadata. UUID remains unassigned;
this is not a secure wipe or a journal. Installer/root migration uses the bounded supported profile; installed account/access behavior is described in [ACCOUNTS](ACCOUNTS.md).

## Ownership, modes and CI input pacing

The shell's plain metadata contract is bridged to the installed VFS at boot and
inherited by scripts. `ls -l`/`stat` inspect the final entry without following it,
including dangling links; the bridge treats exact mount points as their mounted
roots. Ext2 reports persisted 32-bit UID/GID halves, modes/special bits, link
counts, size and inode/block data. Virtual proc/dev nodes report their defined
attributes. FAT's Unix mode/ownership fields are unavailable and shown as `?`.
`chmod OCTAL FILE...` accepts 0000..7777; `chown UID[:GID] FILE...` accepts numeric
unsigned 32-bit IDs. They follow final links and reject non-ext2 nodes. This
checks caller credentials and changes stored metadata; ls/stat display numeric ownership.
The writable profile requires Linux creator layout for OS-dependent UID/GID
fields. SetAttr preflight rejects unsupported flags/timestamp precision before
mutating data; unselected fields and file type survive updates, and ctime changes
with mode/ownership. Creation uses caller UID/GID and umask 022; see [ACCOUNTS](ACCOUNTS.md).
Host tests check remount, inode reuse, independent debugfs/e2fsck, parsing and
special-bit formatting. Guest checks update files and directories, follow links,
refuse invalid/unsupported changes, remount and retain the original file contents;
the host independently inspects persisted owners and modes.

CI run 37935747572 lost keyboard input during a collector pause: a link target
became `per` instead of `persist.txt` despite a clean filesystem. The installed
virtio keyboard has 32 event buffers, which are not recycled with interrupts
disabled. The smoke harness incrementally watches serial GC start/end messages
and stops key injection during collection, with a bounded failure diagnostic if
collection never finishes. Keys use explicit 20 ms holds instead of overlapping
100 ms default releases. Assertions and CI timeout budgets stay unchanged.
Validate Release, as CI does; Debug alone did not reproduce this input-loss race.

## Debugging a crash

Uncaught exceptions end on the panic screen. The same report goes to the serial port (`-serial
stdio` in `tools/run.sh`) and, on installed systems, to `/var/log/boot.log`. The previous
boot's log survives as `boot.log.1`, so after pressing R you can `cat /var/log/boot.log.1`.
`crash` triggers the panic path on purpose. For step debugging, use the "Debug x64 Kernel"
launch configuration (GDB via QEMU), described in `docs/cosmos/articles/user/debugging.md`.

## CI and releases

`.github/workflows/ci.yml` runs on every push and pull request:
1. host unit tests
2. ISO build with the Cosmos toolchain
3. the QEMU smoke test (KVM on GitHub's runners); logs and the final screenshot are uploaded
   as the `smoke-test-logs` artifact

Pushing a tag `v*` additionally creates a GitHub release with `Zenith.iso` and `Zenith.elf`:

```sh
git tag v0.2.0 && git push origin v0.2.0
```

## Working on Cosmos itself

Some roadmap items need changes inside Cosmos (its HAL and boot code are internal). The Cosmos
`gen3` source lives next to this project in `../Cosmos`.

**One-time setup** (already done on this machine):

```sh
git clone -b gen3 https://github.com/CosmosOS/Cosmos.git ../Cosmos
cd ../Cosmos && git submodule update --init --depth 1 --recursive   # dotnet/runtime (~1 GB) + lai
```

Cosmos pins a newer .NET SDK than Zenith uses (`global.json`: 10.0.201+). It's installed
user-locally in `~/.dotnet` and used only for Cosmos builds; the system `dotnet` stays the default.

**Build Cosmos packages** after changing Cosmos:

```sh
cd ../Cosmos
git checkout global.json   # the script rewrites the Cosmos.Sdk version in it
DOTNET_ROOT=~/.dotnet PATH=~/.dotnet:~/.dotnet/tools:$PATH ./.devcontainer/postCreateCommand.sh
```

The script packs every Cosmos package as `3.0.89.<yyyymmdd>` into `../Cosmos/artifacts/package/release`.
It also has machine-wide side effects:
- it clears `~/.nuget/packages/cosmos.*`
- it registers that folder as a user-level NuGet source named `local-packages`
- it replaces the global `Cosmos.Tools`/`Cosmos.Patcher` tools with the local builds

**Point Zenith at the local packages:**

```sh
tools/use-cosmos.sh local     # writes cosmos.local.props (git-ignored) with the local version
tools/use-cosmos.sh status
tools/use-cosmos.sh release   # back to nuget.org; CI always uses the release
```

**Contributing back:** fork `CosmosOS/Cosmos` on GitHub, add it as a remote in `../Cosmos`,
and open pull requests from topic branches. The roadmap's "Upstream work" section lists the
planned changes.

## Ext2 directory moves

Directory `mv` now uses `Directory.Move`, rather than copying a tree and deleting
its source. The ext2 rename path refreshes parent inode snapshots and identifies
same-parent handles by inode number. It preserves the moved inode, adjusts both
parent link counts for cross-parent directory moves, finds the actual `..`
record, and updates directory modification/change times and moved inode change
time. Destination ancestry is checked on disk before writes; missing or cyclic
parents, traversal beyond 1024 levels, and link-count overflow are refused.
Existing destinations and foreign filesystem handles are refused. Same-entry
rename is a successful no-op when the source exists. This is not crash-atomic:
ext2 has no journal and interruption between writes can require repair.

Host regressions check remount metadata, parent links, descendant/corrupt-cycle
refusal, overflow refusal, same-parent aliases, existing destinations, and foreign
volumes. Independent e2fsck validates the resulting directory structure. Native
smoke checks move the populated, owned directory across parents and back, verify
metadata and parent links across remount, reject a cycle, then inspect the disk.
The Files app folder-move restriction remains until its own cross-parent UI path
is validated. Open-file deletion uses Cosmos deferred namespace removal; installed ext2 roots are implemented.

## Ext2 installation and consistency checks

New installations create a FAT32 ESP (128 MiB) and a Linux-type GPT ext2 root
(up to 512 MiB); extra disk space remains unallocated. Old FAT roots remain
recognized. Installer refuses disks with mounted partitions before writing GPT.
The guest supplies its own kernel and Limine to the ESP. To validate
actual disk-only UEFI boot, run `python3 tools/test-installed-boot.py
smoke/installed-disk.img --keep smoke-efi` after a Release smoke run. This uses a
clone, private OVMF variables, no ISO, and independently checks the disk afterward.
CI installs OVMF, mtools and parted and runs this path automatically.

`fsck.ext2 PARTITION` is check-only and refuses mounted/overlapping partitions.
It audits supported-profile geometry, bitmaps/counters, metadata allocation,
block ownership including indirect tables, inode sectors, directory structure,
parents/reachability and link counts. Diagnostics are capped at 32 entries with
a total error count. It makes no changes and is not a complete e2fsck replacement;
use host e2fsck for repairs, unsupported layouts and crash recovery.

`fsprobe DIRECTORY` exercises two shared handles, write/read after deletion,
closing one handle, final-close deletion, inode reuse and scratch cleanup.
Installed Cosmos 3.0.89 retains the directory entry until final close; this is
verified compatibility behavior, not Unix immediate namespace removal. All
normal deletion callers must use VFS lifetime management. Mounted adapters enforce credentials and VFS plugs authorize deletion before
marking handles pending; final close completes already-authorized deletion. Superblock writes now precede device Flush
at the sync durability point; a volatile-device regression checks this ordering.

Writable mounts durably mark the primary ext2 superblock unclean. Sync leaves it
unclean; successful unmount restores clean state and flushes. Volumes left dirty
by a crash require a full read-only consistency pass before automatic root mounting.
Inconsistent/error-marked volumes and dirty secondary CLI mounts remain refused and
need external e2fsck; the boot path does not repair or override the clean flag. The
checker also reports files exceeding the driver's double-indirect size range;
triple-indirect file I/O remains unsupported. This profile is deliberately bounded.


## Security integration and released patcher compatibility

The HAL-only `Zenith.Security` assembly contains credential/access policy without
referencing Cosmos.System or the kernel executable. VFS plug bodies call it;
placing these helpers in Zenith itself would create initializer dependencies.
The released 3.0.89 patcher resolves enum references relative to its working
directory and imports a redundant AssemblyRef to the target itself. The local
`tools/cosmos-patcher.py` wrapper runs it beside copied kernel dependencies and
`tools/PatcherFixup` removes/rebinds redundant self references, preserving real edges
and matching PDB output. It also prepends the HAL-only policy's permission check to
the released PAL descriptor `Open` method, before handle allocation: a denied
truncation must not leak a tracked handle. The fixup checks the expected method
signature and fails the build if it changes. Global tools and the sibling Cosmos
checkout are unchanged.
The fixup uses pinned Mono.Cecil 0.11.6; its host sources are excluded from the kernel.
Native builds and QEMU tests are required to validate patched code.

Run `python3 tools/security-smoke.py /tmp/zenith-security` for the disposable M2
account/access suite. x64 RDSEED must be available (QEMU `-cpu max`); ARM64 password
creation fails closed until an entropy backend exists. See [ACCOUNTS](ACCOUNTS.md)
for bootstrap, record/update rules and the trusted-kernel security boundary.

## Shared settings/session and package foundation

`SystemRuntime` initializes shared services; `SessionRuntime` selects the available
host; `DesktopSessionHost` retains today's test DE. The x64 COM1 headless host bypasses
Cosmos's graphical console bootstrap and works without a GPU. See
[settings/session guide](SETTINGS-SESSIONS.md).

```sh
python3 -B tools/test_package_tool.py
python3 tools/foundation-smoke.py /tmp/zenith-foundation
QEMU=/usr/bin/qemu-system-x86_64 python3 tools/headless-smoke.py /tmp/zenith-headless
```

The package-tool tests build the plain .NET Linux host tool, exercise deterministic
bundles/dry-runs, corruption, conflicts, symlinks/FIFOs, output preservation and tree
limits. The guest foundation harness uses disposable live/installed ext2 roots and
checks shared settings, aliases, privilege denials, migration/persistence, future
versions, capability fallback and package inspection/dry-runs. US/GB keymap tests
restore US using common keys before injecting US pipe punctuation. The whole existing
smoke suite must still pass: extracting startup is not permission to weaken GUI tests.
Package format and activation boundaries are in [P0 contract](PACKAGE-PROTOTYPE.md).
Add `--test-session-override` to the installed EFI check on a disk retained by the
baseline smoke suite. It edits a disposable disk copy's boot arguments and verifies
session selection and the new manual pages; it preserves the source disk.

The headless suite creates disposable disks and uses a paced serial socket instead
of keyboard injection. It omits VGA/GPU/input devices, covers live fallback and
disk-only UEFI saved/invalid/override selection, hidden authentication, permissions,
scripts/read/cancellation, persistence, clean shutdown and serial panic restart.
It checks transcripts for randomly generated test passwords and runs independent
host e2fsck. These test credentials are not printed or committed.

## M3 network lab

Run `python3 -B tools/network-smoke.py /tmp/zenith-network` after Release publish.
It serves HTTP/DNS/NTP on ephemeral loopback ports and uses disposable QEMU/slirp
e1000e guests. No public network server is needed. Socket access must be permitted.
Preserve wrong-ID DNS, timeout, failed-download preservation and Ctrl+C cases: Cosmos
3.0.89 skips unwind-finally handlers, so host-only cleanup tests miss native leaks.
[NETWORKING](NETWORKING.md) describes application bounds versus framework queue limits.

Run `python3 -B tools/dhcp-receive-smoke.py /tmp/zenith-dhcp-receive` to replay the
observed VMware DHCP Offer/ACK profile over QEMU's raw Ethernet socket backend.
It checks both unicast and broadcast Ethernet replies through the real E1000E
driver, receive IRQ diagnostics, lease/route/DNS and outgoing broadcast headers.
An additional guest sets the E1000E PCI Interrupt Disable bit through QEMU qtest after
boot, then reacquires DHCP. It requires unchanged NIC interrupt counts and real
Offer/ACK delivery through polling; production builds contain no fault switch.
This is a controlled receive fixture; VMware interrupt routing remains a separate
acceptance check. The x64 HAL build-copy adaptation runs before ILC cache hashing.


M3 HTTPS builds offline from the checked Mbed TLS archive under `vendor/mbedtls`.
`tools/build-tls.py` invokes clang for freestanding objects before link cache checks;
use clang and nm (binutils) on PATH. Native headers are isolated from host libc and
an import check rejects unsupported dependencies before linking. The post-patcher has its own cache invalidation,
so changing PatcherFixup/wrapper forces Cosmos to repatch. OpenSSL is needed for
host/native TLS fixture certificates. See [NETWORKING](NETWORKING.md) for bridge,
launcher and native acceptance commands, including optional home DNS testing.

## Signed offline package validation

See [PACKAGES](PACKAGES.md) for key generation, sealing and guest commands. Native
package acceptance uses `python3 -B tools/package-smoke.py obj/package-smoke` and
creates its own ext2 disk plus disposable signing keys outside the HTTP served tree.
Host package faults are covered by PackageManagerTests; actual tool/native signatures
by `tools/test_signed_package_tool.py /path/to/libzenith-tls.so`. Preserve journal,
modified-file, dependency/conflict and root/user cases when changing extraction.


## Native text tools and independent data generators

```sh
python3 tools/build-userland.py obj/sunbeam-payload --version 0.1.0
# GCC preferred; ZENITH_USER_CC=clang requires LLD.
dotnet run --project tools/Zenith.PackageTool -- native-check obj/sunbeam-payload/usr/share/zenith/native/sunbeam.elf
dotnet run --project tools/Zenith.ManualBuilder -- obj/manual-payload
python3 tools/build-font-package.py obj/font-payload
python3 -B tools/test_distributor_maintenance.py
python3 -B tools/native-process-smoke.py /var/tmp/zn-native-check
```

Build the Release package tool and current ISO before native acceptance. Use an
actual short keep directory for QEMU Unix sockets. The harness generates disposable
keys/disks, tests protected code and app upgrade without kernel replacement, installs
full manuals/fonts, audits the clean main ext2 disk, and renders a disposable virtio
GUI clone. It explicitly stops the GUI clone after capture; that clone is not a
clean-poweroff audit. Never use a production VM image for these tests.

## Experimental CPU workers

See [MULTICORE](MULTICORE.md) before adding virtual CPUs or enabling `smp=native`.
After the Release build, run `python3 -B tools/native-cpu-smoke.py /tmp/zn-cpu`
and `python3 -B deploy/distributor/validation-runner.py tools/native-process-smoke.py /tmp/zn-native --cpu-workers`.
These use disposable ISO/disk clones and never modify an installed VM. Idle native
APs currently poll; managed runtime/GC work stays on the BSP. CI covers the 1/2/4
CPU worker boundary and native application containment with workers enabled.

## Installed boot interrupt handoff

The copied Cosmos.Core.X64 IDT import is redirected by PatcherFixup to the local
native `zenith_load_idt_preserve_irq` function. It loads the IDT with IRQs masked
and restores the caller's IF; it does not enable interrupts early. Unknown import
signatures fail the build. The dependency cache and upstream checkout are untouched.
After a Release build, run:

```sh
python3 -B tools/test_idt_fixup.py
python3 -B tools/idt-boot-smoke.py /tmp/zn-idt
```

The tests verify real released-import adaptation/idempotence/signature refusal,
then install onto a disposable disk and repeat UEFI boots with DHCP, mounted ext2,
masked initial IDT state, runtime IF preservation and clean shutdown. Root can run
`cpu irq-test` on a running system; ordinary users are refused. Keep this gate when
updating Cosmos or changing early interrupt/platform initialization.

## Native gate timing and serial overhead

Controller validation records per-VM `wait-times.jsonl` and prints each wait's
label, wall time, result, serial byte growth and observed accelerator. KVM is
reported from live QEMU KVM descriptors; fallback stderr identifies TCG. Unknown
access remains unknown. Raw serial data, passwords and command text are excluded.
The dashboard's existing phase log displays these entries; whole-phase totals
remain in `phase-times.json` in the release workspace.

Successful main-loop and per-page allocation prints from pinned Cosmos build
copies are quieted with instruction/control-flow checks. Startup, errors, kernel
logs, scheduler diagnostics and actual allocator/lifecycle operations remain.
Compare disposable workloads with:

```sh
python3 -B tools/serial-overhead-bench.py --iso /path/to/before.iso --keep /tmp/zn-before
python3 -B tools/serial-overhead-bench.py --iso output-x64/Zenith.iso --keep /tmp/zn-after
```

The broken-startup fixture is a small signed higher-half ELF compiled by Clang/GNU
ld. It prints an entry marker then loops. Acceptance requires that actual marker,
checks startup never completes, then boots the retained previous generation using
real Limine keyboard selection. The full-sized healthy kernel transfer, actual
activation, data persistence, confirmation/rollback, crash recovery and filesystem
audits still run. This avoids an unnecessary second full-size failed-kernel
transfer and stage; it is not a reduced-size substitute for healthy kernel updates.
Status observation does not queue readbacks while the exact submitted command is
still visibly busy. A completed prompt only permits reading the saved status;
it never counts as success. Original deadlines and exit-code assertions remain.
Both logged kernel-test statuses and plain repository-test statuses save the
actual exit code before reporting it. Recovery only rereads that invocation's
unique temporary file; it never repeats an install or update. Repository waits
also appear under their `P4_*` labels in timing output. A still-running download
or kernel copy at the deadline remains a failure, even if data updates committed.

### Nested KVM storage comparison

An enabled KVM accelerator can still be slow for emulated SATA I/O inside another
VM. Compare the same ISO with explicit accelerators before another full release:

```sh
comparison=$(mktemp -d /var/tmp/zenith-io.XXXXXX)
export PATH="$PATH:/usr/sbin:/sbin"
iso="$HOME/.local/state/zenith/distributor/build-kernel-v0.0.10/output-x64/Zenith.iso"
python3 -B tools/storage-accelerator-bench.py --iso "$iso" --accelerator kvm --keep "$comparison/kvm"
python3 -B tools/storage-accelerator-bench.py --iso "$iso" --accelerator tcg --keep "$comparison/tcg"
```

The tool creates new disposable disk files, mounts a separate FAT32 partition,
writes/reads 4 MiB, syncs and powers off. It audits the FAT allocation and verifies
the host-extracted file bytes. FAT32's stale optional FSInfo free-space hint is
reported explicitly; other audit errors fail. It needs dosfstools/mtools and never
uses an existing VM disk. Each I/O observation is bounded to 300 seconds. Failed
runs retain their transcript and partial timing JSON. This is an I/O comparison,
not a replacement for signed kernel or repository acceptance.

Controller validation accepts `ZENITH_VALIDATION_ACCELERATOR=auto|kvm|tcg`.
`auto` retains the existing KVM/TCG fallback; explicit choices have no silent
fallback and keep the same CPU/device configuration and acceptance assertions.
For the service, set this variable through a systemd drop-in only after comparing
the server's measurements. Controller changes apply to unchanged tagged harnesses;
no new kernel tag is needed. Future phase timing also includes QEMU CPU seconds
and host read/write byte deltas when `/proc` permits observation. Host I/O counts
are process counters, not guest disk throughput; missing counters remain absent.

The home distributor's operator comparison on 2026-10-14 measured TCG at 23.468
seconds versus nested KVM at 95.765 seconds, with 4 MiB writes taking 11.228 versus
56.651 seconds. For that server, persist TCG independently of the managed unit:

```sh
sudo install -d /etc/systemd/system/zenith-distributor.service.d
printf '[Service]\nEnvironment=ZENITH_VALIDATION_ACCELERATOR=tcg\n' | sudo tee /etc/systemd/system/zenith-distributor.service.d/accelerator.conf >/dev/null
sudo systemctl daemon-reload
python3 -B deploy/distributor/poll.py --queue-retry kernel/v0.0.10
sudo systemctl start --no-block zenith-distributor.service
```

Run this from the updated controller checkout while the distributor is idle. The
native phase logs must report `Validation accelerator policy: tcg`; all release
gates still run. This deployment choice is specific to the measured host, not a
universal preference for TCG. Remove the drop-in and reload systemd to restore
the original auto policy. Full release runtime/publication still needs validation.

## Installed login/session and appearance acceptance (0.0.11)

After building the Release kernel and package tool, run the disposable graphical
and local-console gate through the distributor adapter:

```sh
ZENITH_VALIDATION_ACCELERATOR=tcg python3 -B deploy/distributor/validation-runner.py tools/login-session-smoke.py /tmp/zn-login-check
python3 -B tools/test_meta_package_tool.py
```

The native fixture creates its own disk and signing keys. It checks authentication,
logout refusal, local input/history, DE/headless switching, signed styling reload
without a kernel replacement, metapackage removal and independent ext2 integrity.
Never point the fixture at an operator VM disk. Tagged kernel release validation
includes this gate alongside kernel-update, repository and protected-program gates.
