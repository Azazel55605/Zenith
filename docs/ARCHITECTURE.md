# Current Zenith architecture

Audited 2026-10-27 against source `a558f66`. This document describes
current code; release/environment acceptance is tracked in [Master roadmap](ROADMAP.md#state-and-evidence).
Future structure is in [the master product definition](ROADMAP.md#product-definition); update design is in
[package architecture](PACKAGES.md#architecture-and-activation).

## Build and execution model

Zenith targets .NET 10 using Cosmos Gen 3 / NativeAOT. `Zenith.csproj` references
Cosmos.Kernel and Cosmos.Kernel.System; `Directory.Build.props` and `global.json`
pin Cosmos to 3.0.89; the host .NET SDK is a separate toolchain requirement. The local package override is
explicit and ignored by Git. Publishing produces a native kernel ELF and boot ISO.
CI uses Release builds; developer tasks also support Debug. See [build details](DEVELOPMENT.md).

Remaining shell evaluation/administrative commands, several GUI applications,
compositor/greeter and privileged services share the trusted C# kernel address
space. Worker threads improve responsiveness, not isolation. Selected base/fs/net
and graphical C/ASM clients run as independently packaged protected static x64 ELF.
Resource packages independently deliver manuals/fonts/branding/palettes. Compatible
app/resource updates do not replace kernel bytes; remaining linked code does.

[USERLAND](components/USERLAND.md) describes the remaining migration. A window is not an
isolated process, and resource delivery does not make the consuming renderer an
independent executable.

## Boot and steady-state loop

`Kernel.BeforeRun()` performs shared initialization and session startup inside a
panic boundary:

1. `SystemRuntime` prepares storage/root, virtual filesystems and M2 accounts.
2. `SystemConfiguration` loads validated keyboard/timezone/session settings.
3. Shared commands and the VFS metadata bridge are registered independently of UI.
4. `SessionRuntime` selects an available host from boot arguments/saved settings.
5. Configured installed accounts reach `LoginSessionHost` before apps open.
   Authentication selects `DesktopSessionHost` or the local text console; no
   supported display uses `HeadlessSessionHost` COM1 fallback. Bootstrap/live
   flows remain available. Appearance loads from verified resources or fallback.

`OnBoot` bypasses Cosmos's optional graphical `KernelConsole` initializer; HAL and
system managers are initialized earlier by Cosmos. The DE owns its later canvas.

`Kernel.Run()` checks requested panics and physical-memory pressure, ticks the
selected session host and halts until an interrupt. Keyboard/mouse/timer interrupts
wake it. The memory reserve policy requests collection near one-eighth free pages,
at most once per second. It protects the tested native workload; graceful behavior
for arbitrary workloads larger than RAM remains unproven.

The test DE, local text and x64 serial fallback hosts exist; WM remains unavailable. Selection
prefers DE as fallback when supported display hardware is present, then headless.
Saving an unavailable mode through the CLI is refused. Supported display detection
checks the boot framebuffer and known virtio GPU/VMware SVGA PCI IDs without a canvas.
COM1 input is polled on the main loop; one worker runs shell commands with explicit
credentials. Console output is bounded and bypasses persistent logs. Fatal headless
and early boot errors use serial diagnostics and R-to-restart; a failed graphical
panic renderer also falls back to serial. See [settings and sessions](SETTINGS-SESSIONS.md).

## Code ownership and seams

| Location | Responsibility |
|---|---|
| `Kernel.cs` | Shared/session boot composition, idle loop, panic and collection boundaries |
| `src/Core/Shell` | Parsing, interpreter, command contracts, cancellation and metadata abstraction |
| `src/Core/Shell/Commands/SystemCommands.cs` | Cosmos-dependent shell operations |
| `src/Core/Storage` | Root/layout/install/mount adapters, proc/dev, probes, ext2 integration |
| `src/Core/Storage/Ext2` | Locally adapted ext2 driver, formatter, profile and consistency checker |
| `src/Core/Boot` | Limine artifacts, root selection and dedicated signed kernel generations/recovery |
| `src/Core/Packages` | Signed scripts/data, dependencies/conflicts, ownership and undo transactions |
| `src/Core/Input`, `src/Core/Time` | Layout/clock configuration and persistence |
| `src/Core/Files`, `src/Core/Text` | File-browser and editor behavior without graphical presentation |
| `src/Gui` | Input, windows, terminal buffer, graphics, clipboard and shell composition |
| `src/Apps` | Welcome, Terminal, Files, Editor, System and Sunrise windows |
| `tests/Zenith.Tests` | Host-linked core sources and filesystem-contract regressions |
| `tools` | Build/run switching, disposable disk fixtures and native smoke/UEFI tests |

Keep business rules in Core and presentation in Apps/Gui. Host tests cannot
replace native validation of Cosmos plugs, dispatch, scheduling or device I/O.
Driver/HAL changes often require work in Cosmos because its device plumbing is
internal. The sibling `../Cosmos` checkout is a separate project, not automatically
part of a Zenith change. The vendored [reference docs](cosmos/SOURCE.md) are pinned.

## Storage and installation

| Mount/resource | Current behavior |
|---|---|
| Installed `/` | New installs: supported ext2 root up to 512 MiB; older FAT roots recognized |
| Live `/` | 64 MiB RAM-backed FAT; lost on reboot |
| `/tmp` | Separate 16 MiB RAM-backed ext2, mode 1777, including installed boots |
| `/proc` | Read-only `meminfo`, `mounts`, `uptime`, `cmdline`; per-open first-read snapshots |
| `/dev` | Null/zero, hardware random and root-readable disk/partition streams using Cosmos device names |
| ESP | 128 MiB FAT32, versioned kernels, inventory and EFI bootloader |
| Secondary ext2 | Explicit supported-profile mounting and guarded partition formatting |

ISO automatic root discovery probes `/etc/os-release`. Installed boot entries select
an exact GPT PARTUUID and verify the loaded kernel hash; missing/ambiguous roots fail
closed. `root=live` forces RAM rescue. Installed ext2 uses validated account records.
`Installer` validates Limine-provided ELF/EFI bytes before erasing, writes GPT and
filesystems, copies/read-verifies boot artifacts, then publishes the menu. The running
root disk and mounted targets are refused. `/boot/efi` exposes the ESP on installed
boots. `GptChecksums` completes framework GPT checksums and backup structures.
Host helpers no longer supply boot files. See [INSTALLATION](INSTALLATION.md) for
layout and capacity. [KERNEL-UPDATES](KERNEL-UPDATES.md) describes the dedicated
signed coordinator: stage a new hash path, retain the previous kernel, publish a
next-boot menu and require explicit confirmation after reboot. A durable undo journal
and secondary Limine menu support the tested interrupted-activation boundaries.
Kernel rollback shares the root; it does not restore root data or replace the EFI loader.

### Ext2 contract and recovery boundaries

The writable profile is clean Linux-layout revision-1 ext2, 1 KiB blocks,
128-byte inodes and the filetype feature only. Zenith formatting is bounded to
1..512 MiB; unsupported geometry/features are rejected. Byte I/O maps direct,
single and double-indirect blocks, about 64.26 MiB maximum per regular file;
triple-indirect I/O is unsupported. The [source record](../src/Core/Storage/Ext2/SOURCE.md)
explains adaptation, upstream revision, license and fixes.

Implemented operations include allocation/reclamation across groups, sparse and
resize behavior, directories/rename, bounded symlinks and persisted numeric
32-bit IDs/mode bits. Cross-parent directory moves maintain `..` and parent link
counts, preserve inode identity and reject cycles/replacement. They are not
crash-atomic. Symlink targets are 1..1023 UTF-8 bytes; the installed VFS rejects
`..` in targets and follows at most eight hops. Hard-link creation is unsupported.

Writable mounts durably mark the primary superblock unclean. `sync` flushes
metadata but leaves it unclean; completed unmount restores clean state. A crash
can require **host e2fsck repair** before writable mounting. Automatic root boot
audits a dirty supported volume with the bounded read-only checker first and mounts
it only if consistency passes. It does not clear the flag or repair metadata to
bypass refusal. Error-marked or inconsistent roots remain refused. There is no journal.
`fsck.ext2` is a read-only audit of supported unmounted volumes, bounded to
512 MiB/65536 inodes; it reports at most 32 detailed diagnostics, never repairs,
and is not a general replacement for e2fsck.

Cosmos VFS defers namespace deletion until the last open handle closes. `fsprobe`
verifies shared I/O and retained data after deletion, then final-close removal.
This differs from Unix immediate unlink. Calling raw driver Unlink while handles
are held bypasses the required VFS lifetime management.

Installed ext2 accounts use immutable worker/window credentials. Mounted adapters
check read/write, directory traversal, creation ownership, sticky removal and
metadata changes; VFS plugs authorize deferred unlink and traversal across mounts.
Only root can install/format/mount or read raw disks. Held I/O rechecks permissions.
FAT has no corresponding persisted metadata; unknown fields display `?` and only
root may mutate it. Login closes old windows and clears the clipboard; su changes
one terminal. Kernel-linked code remains trusted; protected native ELF has a separate boundary.

Passwords use bounded versioned PBKDF2-SHA256 records and fail-closed x64 RDSEED
entropy; `/dev/random` reads the same hardware source. Cosmos's timer-mixed
XorShift random plug is not used for passwords. ARM64 entropy is unavailable.
See [ACCOUNTS](ACCOUNTS.md) for setup, access rules and recovery limits.

## Shell, terminal and application behavior

The interpreter supports variables, quoting, globs, conditionals, loops,
functions, command/arithmetic substitution, scripts, redirection and pipelines.
Each submitted command line runs on a worker with cooperative cancellation;
Ctrl+C produces status 130. Pipeline stages exchange bytes through 8 KiB channels
with backpressure; early reader exit stops the producer. Child stages isolate
producer state and the final stage retains parent state. Eight stages/workers are
allowed per shell tree, including nested pipelines. Compatibility builtins and
post-command file redirection buffers are bounded at 1 MiB. Owner-filtered native process snapshots/cancellation, four shell background jobs and foreground raw leases exist; a full PTY/signal/process-group contract does not.

Desktop owns input routing, launcher/taskbar and the window manager. Windows
float, cascade, focus, resize, minimize and maximize. There are no workspaces or
auto-tiling policies. A cached scene avoids recomposition on plain cursor motion;
changed scenes are recomposed and presented through the full-screen canvas.
The clipboard is shared across in-kernel apps, not an interprocess protocol.

The Files app is intentionally a filesystem test client: one selection, location
entry and basic create/open/rename/copy/move/delete with confirmation. Copies are
bounded to 8 MiB, Editor opens to 128 KiB; directory copies, cross-parent folder
moves in the GUI and symlink/device transfers are unavailable. This is the
baseline to replace later, not the intended final file manager.
Settings includes shared keyboard/timezone/session commands, typed root-only Appearance persistence and independent Meridian/Colors clients. Current source has native Welcome/System frontends; Terminal, Editor, Sunrise, greeter and compositor remain trusted. WIP Meridian pages are navigation, not management backends.

## Verification and delivery

The [master roadmap](ROADMAP.md#state-and-evidence) owns release/environment
acceptance; [development](DEVELOPMENT.md) owns reproduction commands and the
[archive](archive/README.md) preserves exact results. Full clean-source Release
qualification is required before a new kernel tag. Historical test totals are not
current architecture facts and must not be combined across revisions.

## Offline packaging foundation

P0 remains a bounded unsigned bundle inspection/dry-run experiment. P1 adds RSA-PSS
signed scripts/data containers, dependency/conflict planning, a protected ownership
database and installed ext2 staging/undo transactions. The host tool builds/seals
packages; the root-only guest executor checks a fingerprint-pinned public key and
preserves local edits. Boot/account files are excluded from P1. Local settings stay
under /etc and outside data payloads. See [PACKAGES](PACKAGES.md).

P3 uses a separate signed kernel container and activation state. Installer, package
and kernel mutations share a maintenance gate; normal shutdown refuses active work.
Selected base/filesystem/text/network tools and graphical clients are independently packaged; shell evaluation, privileged services, compositor and several apps remain kernel-linked. Signed
online catalogs, named installs and all-installed updates are implemented. Matched
kernel/root generations remain future work. Manifest-3 metapackages and extended
catalog compatibility are documented in [ONLINE-PACKAGES](ONLINE-PACKAGES.md).

## Shared IPv4 service

`SystemRuntime` starts `NetworkService` after mounts/accounts/settings. It configures the
primary NIC on a root service worker; `Kernel.Run` schedules bounded DHCP reacquisition.
Both session hosts use the same diagnostic/configuration commands. Pure configuration,
DHCP/DNS/NTP and incremental HTTP codecs have host tests; native adapters and staged
downloads require QEMU fault/cancellation tests. Native cleanup catches in the resource
owner frame because Cosmos 3.0.89 skips unwind-finally handlers. Optional boot NTP
corrects shared software UTC. See [NETWORKING](NETWORKING.md) for bounds, framework
limitations, fixed-memory verified TLS and home acceptance. Signature-checked build
adapters guard IPv4 ingress, bound UDP/ICMP/TCP storage and share NTP corrections
with BCL/filesystem timestamps. TCP payload ACKs and contiguous sequence checks use
signature-checked adapters; unread data shrinks the advertised receive window and
application reads reopen it. Explicit fetch --kernel permits bounded ELF-sized
transfers while ordinary downloads keep their smaller limit. Selected native clients call owned services; trusted legacy clients still exist.


## Protected native executables

`Core/Processes` validates bounded static x86_64 ELF ABI 1 and prepares private user
page tables/stack. `Native/x64/Process.s` enters ring 3 and contains faults, invalid
memory/stack returns and watchdog expiry. Signed installed file ownership, mode
and digest gate entry. Native resource caps and full-range copy validation bound
services. This is a foreground BSP bridge, not a general process scheduler.

Filesystem requests yield to the managed caller's original kernel context before
VFS/driver execution and resume user registers/FP state afterward. Per-invocation
handles carry caller credentials and cwd; reads/listings/stat recheck access and
cleanup runs on exit/fault/timeout/cancel. Managed scheduling points between request
groups permit cancellation. Raw user windows still suspend normal managed dispatch.
The stream extension adds canonical host stdin, live stdout/separate stderr and
bounded byte pipelines. Up to eight rooted native contexts may wait on managed
streams; a raw-entry gate serializes their user windows on the BSP. Per-invocation
file write/create/truncate/append/flush services remain bounded. Owned TCP/TLS, raw leases, jobs and private graphical surfaces/events are implemented. The optional interactive profile renews bounded quotas only with a live owned surface. Full PTY/POSIX services, independent compositor/session processes, surface resizing and backdrop composition remain open.

Freestanding C/ASM clients build into separate base/fs/net and graphical packages; explicit trusted recovery commands remain available. [Native programs](NATIVE-PROGRAMS.md)
owns exact wire formats, limits and capability dependencies. [Multicore](MULTICORE.md)
owns opt-in isolated integer workers; those APs do not execute managed services.
