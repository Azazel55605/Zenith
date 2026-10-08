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

1. **Run QEMU x64**. In the Terminal: `install` lists target disks, and `install sata0 --yes`
   partitions (GPT: ESP + root), formats (FAT32), and copies the system.
2. **Make Disk Bootable** (the kernel can't copy itself or the bootloader yet; see roadmap M4).
3. **Run QEMU x64 (installed disk)**. Zenith finds its root partition and changes now persist.

Without an installed disk, Zenith boots **live**: the root is a 64 MiB RAM disk that is lost on reboot.
If the attached `disk.img` already holds Zenith, even an ISO boot uses it as the root (and `install`
refuses to overwrite it). Delete `disk.img` to start over.

## Using it

The Terminal opens at boot. `help` lists the commands, `man zenith` is the introduction and
`man sh` the shell language:

- quoting, `$VAR` / `${VAR}` / `$?` / `$1..$9`, `$(command)`, `$((arithmetic))`, `~`, `*`/`?` globs
- `|`, `>`, `>>`, `<`, `;`, `&&`, `||`, `!`
- `if`/`elif`/`else`, `for`, `while`/`until`, functions, `test`/`[`, `read`
- scripts with `sh file`, by path, or by name from `$PATH` (`/bin`); unfinished lines continue with `> `
- history (Up/Down), Tab completion, Ctrl+C (stop), Ctrl+L, Ctrl+U, Ctrl+D
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

Settings: `localectl set-keymap de`, `timedatectl set-timezone Europe/Berlin`.
`edit file` opens the text editor, `open system` any desktop app, `fps` shows compositor statistics.
Windows resize from their edges; double-click a title bar to maximize.

## Documentation

- [`INSTRUCTIONS.md`](INSTRUCTIONS.md): shared contributor and agent guidance (also linked from `AGENTS.md` and `CLAUDE.md`).
- [`docs/PROGRESS.md`](docs/PROGRESS.md): active M2 work, validation evidence and next steps.

- [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md): toolchain, tests, CI and releases, debugging
  crashes, and working on Cosmos itself.
- [`docs/ROADMAP.md`](docs/ROADMAP.md): milestones M0–M7, known gaps, upstream work and
  open decisions.
- [`docs/TECHNICAL-NOTES.md`](docs/TECHNICAL-NOTES.md): performance findings, the Unix model,
  ext4/btrfs effort, the installer, and the driver landscape.
- [`docs/cosmos/`](docs/cosmos/index.md): the vendored Cosmos Gen 3 docs (source commit in
  `docs/cosmos/SOURCE.md`).

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
src/Apps/                  Terminal, Editor, Welcome, System
Resources/Fonts/           Inter (UI) and Hack (terminal); embedded via tools/gen-fonts.py
tests/Zenith.Tests/        xunit tests compiled against the kernel's plain-.NET sources
tools/                     run.sh, make-bootable.sh, smoke-test.py, use-cosmos.sh, gen-fonts.py
.github/workflows/ci.yml   Unit tests, ISO build, QEMU smoke test, releases
```

## Adding things

- **A command:** add a `Command` in one of `src/Core/Shell/Commands/*.cs`. It receives a
  `CommandContext` with its args and stdin, writes to `Out`/`Err`, and returns an exit status.
- **An app:** subclass `Window` (`DrawContent`, `OnKey`, `OnMouseDown`, `OnScroll`,
  `Update`) and add it to `AppRegistry.Apps`.
