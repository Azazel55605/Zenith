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

The Terminal opens at boot. Type `help` for the command list. Supported shell syntax:

- quoting, `$VAR` / `${VAR}` / `$?`, `~`, and `*`/`?` globs
- `|`, `>`, `>>`, `<`, `;`, `&&`, `||`
- history (Up/Down), Tab completion, Ctrl+C / Ctrl+L / Ctrl+U / Ctrl+D
- PageUp/PageDown or the mouse wheel for scrollback

`open notes` launches a desktop app, and `fps` shows compositor statistics.

## Documentation

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
  Storage/
    SystemMounts.cs        Finds the installed root (or makes a RAM root), mounts /tmp
    FileSystemLayout.cs    The /bin /etc /home … tree and default /etc files
    Installer.cs           GPT + ESP + root partition, format, copy system
    GptChecksums.cs        Fills in the GPT CRCs + backup header Cosmos leaves out
    MemoryBlockDevice.cs   RAM-backed block device
  Shell/
    Shell.cs               Interpreter: env, cwd, pipelines, redirection, globbing, completion
    Parser.cs              Tokenizer (raw words) + per-command word expansion
    Command.cs             Command, CommandContext (args, stdin/out/err, option parsing), ANSI
    Commands/              Builtin, File, Text, System commands
  KernelPanic.cs           Deliberate panics (`crash` command)
src/Gui/
  PanicScreen.cs           Full-screen report for uncaught exceptions; R restarts
  Graphics/                Surface (fills, AA shapes, shadows, cached glyph text), FontFace, Theme, …
  Terminal/                TerminalBuffer: ANSI-colored scrollback
  Shell/                   Desktop compositor, Taskbar, LauncherMenu, AppRegistry
  Window.cs, WindowManager.cs, Input.cs, FrameStats.cs
src/Apps/                  Terminal, Welcome, Notes, System
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
