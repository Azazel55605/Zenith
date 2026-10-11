# Current Zenith architecture

Reviewed on 2026-10-11 against the current repository, including M2 and the
S0/S1/P0 foundation slices. This document describes current code.
Future structure is in [SYSTEM-PLAN](SYSTEM-PLAN.md); update design is in
[PACKAGE-MANAGER](PACKAGE-MANAGER.md).

## Build and execution model

Zenith targets .NET 10 using Cosmos Gen 3 / NativeAOT. `Zenith.csproj` references
Cosmos.Kernel and Cosmos.Kernel.System; `Directory.Build.props` and `global.json`
pin the default framework/SDK version to 3.0.89. The local package override is
explicit and ignored by Git. Publishing produces a native kernel ELF and boot ISO.
CI uses Release builds; developer tasks also support Debug. See [build details](DEVELOPMENT.md).

All current applications and built-in commands are C# linked into the kernel.
They run in kernel mode in a shared address space. An application window is not
an isolated OS process. Shell worker threads keep the desktop responsive, but do
not create a userspace ABI, process isolation, an ELF application loader or a DLL
plugin mechanism. Scripts in `/bin` are interpreted by the built-in shell.
Changing a compiled command or GUI app requires a new kernel build today.
This is transitional: [USERLAND](USERLAND.md) records the accepted future separation
of desktop/apps from the kernel. No process/loader change is implemented by that plan.

## Boot and steady-state loop

`Kernel.BeforeRun()` performs shared initialization and session startup inside a
panic boundary:

1. `SystemRuntime` prepares storage/root, virtual filesystems and M2 accounts.
2. `SystemConfiguration` loads validated keyboard/timezone/session settings.
3. Shared commands and the VFS metadata bridge are registered independently of UI.
4. `SessionRuntime` selects an available host from boot arguments/saved settings.
5. `DesktopSessionHost` loads fonts/canvas and opens Welcome/Terminal for the test DE,
   or `HeadlessSessionHost` starts the bounded COM1 shell without graphical allocation.

`OnBoot` bypasses Cosmos's optional graphical `KernelConsole` initializer; HAL and
system managers are initialized earlier by Cosmos. The DE owns its later canvas.

`Kernel.Run()` checks requested panics and physical-memory pressure, ticks the
selected session host and halts until an interrupt. Keyboard/mouse/timer interrupts
wake it. The memory reserve policy requests collection near one-eighth free pages,
at most once per second. It protects the tested native workload; graceful behavior
for arbitrary workloads larger than RAM remains unproven.

The test DE and x64 serial headless hosts exist; WM remains unavailable. Selection
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
| `src/Core/Input`, `src/Core/Time` | Layout/clock configuration and persistence |
| `src/Core/Files`, `src/Core/Text` | File-browser and editor behavior without graphical presentation |
| `src/Gui` | Input, windows, terminal buffer, graphics, clipboard and shell composition |
| `src/Apps` | Welcome, Terminal, Files, Editor and System windows |
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
| ESP | 64 MiB FAT32, Limine configuration and boot artifacts |
| Secondary ext2 | Explicit supported-profile mounting and guarded partition formatting |

Root discovery probes for `/etc/os-release` identifying Zenith. Multiple matching
roots have no documented user-controlled UUID selection yet. Installed ext2 uses
validated passwd/group/shadow records and authenticated worker/window credentials.
`Installer` writes GPT, formats root/ESP and lays out/copies system files.
`GptChecksums` completes GPT checksums/backup structures missing from the framework
path. `tools/make-bootable.sh` still copies the kernel ELF and EFI bootloader from
host artifacts: installation is not self-contained. Disk-only OVMF boot is verified.

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
can require **host e2fsck repair** before writable mounting. There is no journal.
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
one terminal. Kernel code remains trusted, with no process isolation.

Passwords use bounded versioned PBKDF2-SHA256 records and fail-closed x64 RDSEED
entropy; `/dev/random` reads the same hardware source. Cosmos's timer-mixed
XorShift random plug is not used for passwords. ARM64 entropy is unavailable.
See [ACCOUNTS](ACCOUNTS.md) for setup, access rules and recovery limits.

## Shell, terminal and application behavior

The interpreter supports variables, quoting, globs, conditionals, loops,
functions, command/arithmetic substitution, scripts, redirection and pipelines.
Each submitted command line runs on a worker with cooperative cancellation;
Ctrl+C produces status 130. Pipeline stages buffer output before running the next
stage. Unbounded producers therefore do not stop when a later `head` would have
enough data. No jobs, general process table or full-screen raw terminal mode exist.

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
Settings currently exist as keyboard/timezone commands and persisted configuration;
the System window reports information but is not a Settings application.

## Verification baseline

The final ext2 implementation was locally validated with 374 host tests, eight
Python harness tests, Release publishing, 135 baseline native checks, 135 large
native checks at 512 MiB RAM and seven disk-only UEFI checks. Installed-root data,
mode 0640, UID 70000/GID 80000 and relative symlinks survive reboots; independent
e2fsck is clean after clean shutdown. These are dated results at `8389113`, not
counts guaranteed for every later revision. Hosted CI rerun awaits push.
See [PROGRESS](PROGRESS.md) for failures, corrections and artifact references,
and [DEVELOPMENT](DEVELOPMENT.md) for reproduction commands.

## Offline packaging foundation

P0 shares a bounded experimental manifest reader and dry-run planner between the
Linux host bundle builder and the guest `pkg inspect/plan` commands. It verifies
lengths/digests, exact dependencies, architecture/runtime and existing-path conflicts.
`pkg list` explicitly reports bundled kernel/base/test-DE components; no installed
package database or independently loadable application is implied. No extraction,
activation, signature verification or repository client exists. Local settings stay
under /etc and outside data payloads. See [P0 contract](PACKAGE-PROTOTYPE.md).

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
with BCL/filesystem timestamps. Current apps remain kernel-linked.
