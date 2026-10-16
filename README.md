# Zenith

A Cosmos Gen 3 (NativeAOT) kernel with a Unix-style system layer and a small graphical shell.

## Build & run

VS Code tasks:

| Task | What it does |
|---|---|
| **Build x64** | `dotnet publish` → `output-x64/Zenith.iso` |
| **Run QEMU x64** | Live system from the ISO, with `disk.img` (512 MiB, created if missing) attached |
| **Make Disk Bootable** | Adds Limine + the kernel to `disk.img` after `install` |
| **Run QEMU x64 (installed disk)** | Boots `disk.img` with UEFI (OVMF), no ISO |

Tests: **Unit Tests** (`dotnet test tests/Zenith.Tests`, host-side, no QEMU) and
**Smoke Test (QEMU)** (`tools/smoke-test.py`: boots the ISO headless, runs shell commands,
installs to a scratch disk and triggers a panic, checking the serial log). CI runs both on
every push; tags `v*` publish the ISO as a GitHub release.

From a shell: `tools/run.sh live` / `tools/run.sh installed`. QEMU uses KVM when available,
virtio-gpu, and virtio mouse/keyboard. The pointer is grabbed on hover; **Ctrl+Alt+G** releases it.

## Installing to a disk

1. **Run QEMU x64**, then open **Sunrise** from the launcher (`open Sunrise`). Choose
   a disk, configure hostname/user/passwords, review and explicitly confirm erasure.
   Headless: `install sata0 --setup` provides the same prompts with hidden passwords.
   Minimum target: 256 MiB; layout: GPT, 128 MiB FAT32 ESP and ext2 root up to 512 MiB.
   Zenith copies the system, kernel and EFI loader itself. The old `install DISK --yes`
   copy workflow remains available for tests.
2. `poweroff` (automatically syncs and unmounts), eject the ISO and **Run QEMU x64 (installed disk)**.
   The disk boots independently; the verification task only checks the guest-written files.

Without an installed disk, Zenith uses a 64 MiB RAM root, lost on reboot. The ISO's
normal entry automatically discovers an installed root. Select **Zenith live rescue
(RAM root)** to use RAM even with an installation attached. Installed boot entries bind
an exact root PARTUUID to a versioned kernel. See [installation layout](docs/INSTALLATION.md).
Signed kernel upgrades use `pkg kernel install FILE.zpk`, followed by a reboot,
testing and `boot confirm`. The previous kernel is retained; `boot rollback` and
`boot recover` provide explicit recovery. Follow the [kernel update/test guide](docs/KERNEL-UPDATES.md)
and keep an external live ISO. Physical hardware acceptance remains separate.

## Using it

The Terminal opens at boot. `help` lists commands; `man zenith` and `man sh` include
small recovery guides. Install `zenith-man-pages` for their full independent manuals.
The shell supports:

- quoting, `$VAR` / `${VAR}` / `$?` / `$1..$9`, `$(command)`, `$((arithmetic))`, `~`, `*`/`?` globs
- `|`, `>`, `>>`, `<`, `;`, `&&`, `||`, `!`
- `if`/`elif`/`else`, `for`, `while`/`until`, functions, `test`/`[`, `read`
- scripts with `sh file`, by path, or by name from `$PATH` (`/bin`); unfinished lines continue with `> `
- session history (Up/Down, restoring the current draft), Tab completion, Ctrl+C (stop), Ctrl+L, Ctrl+U, Ctrl+D
- mouse selection, Ctrl+Shift+C/V, PageUp/PageDown or the wheel for scrollback

`/proc` exposes read-only `meminfo`, `mounts`, `uptime` and `cmdline` files.
Values are sampled on the first read (or end-relative seek) of each open handle;
reopen to refresh.
`uptime` reports elapsed seconds only (no CPU idle counter is available), and
`cmdline` is empty when the bootloader supplies no command line.

`/dev/null` discards writes and reads EOF; `/dev/zero` discards writes and fills
reads with zero bytes. Use `dd if=/dev/zero of=/tmp/zeros bs=512 count=2` for a
bounded binary copy (`man dd`). Text filters currently read entire inputs, so
use bounded `dd` when reading `/dev/zero`.
Disks and partitions also appear under `/dev` using their Cosmos names, such as
`/dev/sata0` and `/dev/sata0p1`. These nodes support read-only byte access and
seeking within capacity; writes and truncation fail even when the disk is unmounted.
For example, `dd if=/dev/sata0p1 of=/tmp/sector count=1` reads its first 512 bytes.
Nodes refresh after partition rescans; removed-device handles fail instead of
following a replacement with the same name. Random devices await a kernel entropy source.

Experimental secondary ext2 volumes can be mounted with
`mkdir /mnt/ext; mount -t ext2 sata1p0 /mnt/ext` (use `lsblk` for the actual partition
name). `mount` lists volumes; `umount /mnt/ext` flushes and detaches one. The initial
profile requires clean Linux-layout revision-1 ext2 with 1 KiB blocks, 128-byte inodes and only
the filetype feature. See [ext2 validation](docs/DEVELOPMENT.md#ext2-secondary-volumes)
for the disposable fixture and remaining limitations. New installed roots use ext2; older FAT installations remain recognized.

`mkfs.ext2 PARTITION --yes [-L LABEL]` formats an **unmounted secondary partition**,
erasing its existing filesystem. Use a partition name from `lsblk`, not a whole disk.
It refuses mounted partitions and the running root disk; omitting `--yes` makes no
changes. The initial formatter supports 1..512 MiB partitions in the profile above;
a very small final block group is rejected. Labels use up to 16 printable ASCII
characters. It creates root and `lost+found`, with backups in every group.
For example: `mkfs.ext2 sata2p0 --yes -L SCRATCH`, then
`mkdir /mnt/new; mount -t ext2 sata2p0 /mnt/new`. See `man mkfs.ext2`.
This does not securely erase data; UUID assignment remains a separate formatter follow-up.

On ext2, `ln -s TARGET LINK` creates a symbolic link and `readlink LINK` prints
its stored target, including dangling links. `unlink LINK` removes the link
without deleting the target. Relative targets refer to the link's parent;
existing names are refused. Targets are limited to 1..1023 UTF-8 bytes.
The installed VFS cannot follow `..` in targets and stops after eight link hops.
Hard links and FAT symlinks are unsupported; see `man ln`.

On ext2, `chmod OCTAL FILE...` persists permission bits (0000..7777), and
`chown UID[:GID] FILE...` persists numeric 32-bit ownership. Both follow final
symlinks and reject FAT/virtual filesystems. `ls -l` and `stat` read actual VFS
attributes, display numeric IDs and link targets, and can inspect dangling links.
FAT uses `?` for unavailable Unix fields. Owner/group/other permissions now restrict access. New objects use caller UID/GID
and umask 022; chmod requires owner/root, chown requires root. Installed accounts
use hidden password prompts: initial setup runs `passwd root`, `passwd user`, then
`login user`. Later boots require login. See [accounts and access](docs/ACCOUNTS.md)
and `man accounts` for commands, setup, recovery and security limits. New installed roots use ext2; older FAT installations remain recognized.

`fsck.ext2 PARTITION` performs a read-only consistency check of an unmounted
supported ext2 partition (up to 512 MiB); it reports errors and never repairs.
Use host `e2fsck` for repairs and crash-dirty volumes; mounted volumes stay
marked unclean until unmount. Files use direct/single/double-indirect blocks
(up to about 64 MiB per file); triple-indirect I/O is unsupported. `fsprobe DIRECTORY` tests shared file I/O and
open-file deletion in a unique temporary directory, then cleans it up. Cosmos
currently defers namespace removal until the final handle closes; it retains
open-file data but does not provide Unix immediate unlink semantics.

Directory `mv` uses a filesystem rename, preserving the inode and its ownership,
permissions and contents. On ext2, moves across parents update `..` and both
parent link counts; moves into the directory's own descendants are refused.
Existing directory destinations and moves between filesystems are refused.
The Files app currently keeps its folder moves within the current directory.

Settings: `localectl set-keymap de`, `timedatectl set-timezone Europe/Berlin`.
`edit file` opens the text editor, `open system` any desktop app, `fps` shows compositor statistics.
Windows resize from their edges; double-click a title bar to maximize.

**Files** in the launcher (or `open files /mnt/ext`) is a small file manager.
Select an item and choose Open, or press Enter; text files open in Editor.
The toolbar creates files/folders, renames, copies/moves and deletes files or
empty folders after confirmation. Copy/Move stores a selection in that window;
browse to the destination and choose Paste. Existing names are never overwritten.
Errors appear in the bottom status line. Ctrl+L changes location, F5 refreshes,
F2 renames, Ctrl+N creates a file and Ctrl+Shift+N a folder. Ctrl+C/X/V
copies/moves/pastes; Delete (or Ctrl+D) asks for confirmation; Escape cancels; Ctrl+Q closes.
On the bundled virtio keyboard, use Ctrl+D or the toolbar for deletion.
In name/location prompts, Ctrl+A clears the current value. Copies are limited to
8 MiB and opening in Editor to 128 KiB. Moving folders between parents and copying
folders, devices, virtual files or symlinks are unavailable. See `man files`.

## Independently packaged file tools

The 0.0.13 source extends protected native execution with read-only filesystem
calls. After installing/confirming the compatible kernel and upgrading Sunbeam
0.2.0, use `sunbeam cat FILE`, `sunbeam ls [-a] [-l] DIR`, `sunbeam pwd` and
`sunbeam stat FILE`. Tools inherit your identity and working directory and update
through their own signed package. Existing builtins remain recovery commands.
The current input/write candidate also supports `echo hello | sunbeam tee FILE`,
`sunbeam tee -a FILE` and `sunbeam cat < FILE`, with explicit kernel capability
dependencies. See [native programs](docs/NATIVE-PROGRAMS.md) for limits, validation
state and upgrade order.

## Settings, sessions and packages

The 0.0.11 source adds installed graphical login and Desktop/local text Headless
selection with serial fallback. Ctrl+Alt+T opens Terminal, Ctrl+Alt+L requests logout;
`session switch de|headless` returns to login after busy/unsaved-work checks.
`zenith-session` supplies branding and `zenith-style` selects independent theme/font
resources, reloaded at login. The trusted session host remains kernel-linked.
See [login/session guide](docs/LOGIN-SESSIONS.md).

`settings show` and `settings set keyboard|timezone|session VALUE` use the same
system-settings service as localectl/timedatectl. Changes require root; installed
settings persist, live settings are volatile. `session status/list/set-default`
reports available hosts and next-boot selection. The floating test DE and x64
Headless are available; WM is planned. `session=headless` selects local text when
a supported display exists, otherwise COM1 serial;
`session set-default headless` saves it for the next boot. No supported display
falls back to headless. See [settings/session guide](docs/SETTINGS-SESSIONS.md).

P0 adds `pkg list`, `pkg inspect MANIFEST` and `pkg plan MANIFEST`, plus a Linux
host data-bundle builder. Plans verify payload integrity and refuse unowned-path
conflicts without changing the root. P1 adds signed offline script/data installation,
upgrades and removal on installed ext2; compiled apps remain kernel-linked. See
[package guide](docs/PACKAGES.md) and [prototype contract](docs/PACKAGE-PROTOTYPE.md).

## IPv4 networking

M3 now has shared DHCP/static/off configuration, `ip`, `ifconfig`, `ping`,
`nslookup`, TCP probes (`nc -z`), optional boot/manual NTP and staged bounded HTTP/verified HTTPS
`fetch`. See [networking](docs/NETWORKING.md) for commands, saved settings and limits.
Use `NETWORK=user tools/run.sh live` for outbound LAN access, or `UI=headless` for
serial testing. Root can select your home DNS with `netcfg dns 10.0.10.3`.
HTTPS uses an explicitly imported CA (`tls trust FILE SHA256`); no roots are trusted
by default. The Debian package VM and named HTTPS proxy are user-confirmed.
Downloads alone carry no package-install trust.

## Documentation

Start with the [documentation hub](docs/README.md).

- [Current status](docs/STATUS.md): what works, known limits and release/installation acceptance.
- [Active progress](docs/PROGRESS.md): current task, concrete steps and validation.
- [Roadmap](docs/ROADMAP.md): next slices, dependencies and milestone exits.
- [Component guides](docs/README.md#components): implementation checklists and technical references.
- [Architecture](docs/ARCHITECTURE.md) and [development](docs/DEVELOPMENT.md): code boundaries, building and testing.
- [System requirements](docs/SYSTEM-PLAN.md) and [package design](docs/PACKAGE-MANAGER.md): accepted direction and open decisions.
- [History](docs/HISTORY.md) and [archive](docs/archive/README.md): past decisions and detailed validation evidence.
- [Shared instructions](INSTRUCTIONS.md) and [documentation rules](docs/MAINTAINING.md): contributor workflow.
- [Cosmos reference](docs/cosmos/index.md): pinned upstream documentation.

## Layout

```
Kernel.cs                  Boot: mounts, fonts, desktop; Run() ticks the desktop and halts until the next IRQ
src/Core/                  UI-independent system layer
  Log.cs                   Kernel log → serial, ring buffer (dmesg), /var/log/boot.log
  Input/, Time/           Keyboard layouts; time zones and the system clock
  Text/TextDocument.cs     Editor model (cursor, selection, editing), unit-tested
  PowerControl.cs          Reboot/power-off after sync + unmount
  Storage/
    SystemMounts.cs        Finds the installed root (or makes a RAM root), mounts /tmp
    FileSystemLayout.cs    The /bin /etc /home … tree and default /etc files
    Installer.cs           GPT + ESP + root partition, format, copy system
    GptChecksums.cs        Fills in the GPT CRCs + backup header Cosmos leaves out
    MemoryBlockDevice.cs   RAM-backed block device
  Shell/
    Shell.cs               Interpreter: runs the syntax tree; variables, functions, scripts, completion
    ScriptParser.cs        Recursive-descent parser → syntax tree (if/for/while/functions/pipelines)
    Parser.cs              Tokenizer (raw words) + word expansion, $(...), field splitting
    Arithmetic.cs          $((...)) evaluator
    Command.cs             Command, CommandContext (args, stdin/out/err, option parsing), ANSI
    Commands/              Builtin, File, Text, System commands
  KernelPanic.cs           Deliberate panics (`crash` command)
src/Gui/
  PanicScreen.cs           Full-screen report for uncaught exceptions; R restarts
  Graphics/                Surface (fills, AA shapes, shadows, cached glyph text), FontFace, Theme, …
  Terminal/                TerminalBuffer: ANSI-colored scrollback
  Shell/                   Desktop compositor, Taskbar, LauncherMenu, AppRegistry
  Window.cs, WindowManager.cs, Input.cs, FrameStats.cs
src/Apps/                  Terminal, Files, Editor, Welcome, System
Resources/Fonts/           Inter (UI) and Hack (terminal); embedded via tools/gen-fonts.py
tests/Zenith.Tests/        xunit tests compiled against the kernel's plain-.NET sources
tools/                     run.sh, boot_image.py, install-smoke.py, smoke-test.py, use-cosmos.sh
.github/workflows/ci.yml   Unit tests, ISO build, QEMU smoke test, releases
```

## Adding things

- **A command:** add a `Command` in one of `src/Core/Shell/Commands/*.cs`. It receives a
  `CommandContext` with its args and stdin, writes to `Out`/`Err`, and returns an exit status.
- **An app:** subclass `Window` (`DrawContent`, `OnKey`, `OnMouseDown`, `OnScroll`,
  `Update`) and add it to `AppRegistry.Apps`.

### Signed offline script/data packages

0.0.7 adds signed online discovery and named installation on installed ext2:
`pkg mirror HTTPS_URL`, `pkg refresh`, `pkg search`, `pkg plan NAME`,
`pkg install NAME` and `pkg upgrade`. Interactive application asks for confirmation;
`--yes` supports reviewed noninteractive plans. `pkg kernel update` stages a newer
signed core kernel while retaining reboot/confirmation. See the
[online package contract](docs/ONLINE-PACKAGES.md) for operator commands and limits.

`pkg repos` lists default core/extra definitions; `pkg list` distinguishes bundled
base-tools/net-tools/fs-tools from independently installed scripts/data. Root can
pin a public signing key, check/install/upgrade/remove .zpk files on installed ext2,
verify ownership and recover interrupted transactions. Dependencies, declared
conflicts and local file edits can block a whole transaction. See
[PACKAGES](docs/PACKAGES.md) for building/signing, delivery and exact limitations.
Online indexes and independent compiled tool execution are subsequent work.


Independent package source recipes include `zenith-docs`, `zenith-man-pages`,
`zenith-fonts` and `sunbeam-tools`. In 0.0.8, `pkg update` refreshes signed catalogs,
updates all installed signed data packages and stages a newer kernel, followed by
explicit reboot and `boot confirm`. [Online package workflow](docs/ONLINE-PACKAGES.md).
Sunbeam is the first protected freestanding x64 text executable; run it with
`native run sunbeam echo hello`. Existing base/net/fs builtins and the test desktop
remain kernel-linked pending the remaining process/filesystem/network/GUI APIs.
[Native executable contract and limits](docs/NATIVE-PROGRAMS.md).
