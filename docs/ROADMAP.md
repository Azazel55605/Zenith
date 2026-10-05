# Zenith roadmap

Milestones in recommended order. Each has a goal, its work items, and an exit criterion that
says when it's done. Background and effort estimates for the big items are in
[TECHNICAL-NOTES.md](TECHNICAL-NOTES.md).

**Sizes:** S = a day or two · M = about a week · L = several weeks · XL = months.
**↑ upstream** = a change to Cosmos itself (its HAL is internal; see "Upstream work" below).

## Where we are (v0.2)

**Working:**
- Desktop: compositor, resizable/maximizable windows, taskbar, launcher, clipboard.
- Terminal: a POSIX-flavoured shell with scripting (if/for/while, functions, `$(...)`,
  `$((...))`, `test`, `read`), ~70 commands, text selection, reflow on resize.
- Text editor (`edit file`), manual pages (`man`), keyboard layouts, time zones.
- FAT32 root (installed disk) or RAM root (live), `/tmp` in RAM, clean shutdown.
- Installer (GPT + ESP + root), finished by a host-side bootable step.
- CI: 198 unit tests and a 25-step QEMU smoke test on every push.

**Known gaps in what exists:**

| Gap | Effect today |
|---|---|
| Pipes are buffered, not streamed | Each stage runs to completion before the next starts: `yes \| head` never ends (Ctrl+C stops it) |
| No processes | Every command is built into the kernel; no background jobs (`&`), no `ps`/`kill` (M5) |
| Terminal has no raw mode | No full-screen terminal programs (`less`, `top`, a nano-style editor); the editor is a window |
| FAT | No permissions, owners or symlinks; names are case-insensitive; Cosmos doesn't store timestamps (`ls -l` shows 1970) |
| Editor | No undo, no search |
| Cosmos boot fault under nested KVM | On GitHub's runners about one boot in seven stops with a general-protection fault (exception 13) inside Cosmos' APIC/PIC setup, before Zenith code runs. Never seen locally. The smoke test retries such boots and reports it. |

---

## M0 · Foundation: make change safe ✅ done

Infrastructure that every later milestone leans on.

| Item | Size | Notes |
|---|---|---|
| ✅ GitHub Actions: unit tests, ISO build, QEMU smoke test on every push; ISO release on `v*` tags | S | `.github/workflows/ci.yml`; GitHub's runners have KVM |
| ✅ Host unit tests for the parser, shell and commands, plus the terminal's ANSI buffer | M | `tests/Zenith.Tests`, 64 tests |
| ✅ QEMU smoke test: boot headless, type commands over the monitor, assert on serial output | M | `tools/smoke-test.py`; covers pipes, `/tmp`, exit status, install and panic |
| ✅ Panic screen: exceptions from boot or `Run()` show the message and stack trace, are logged, and R restarts | S | `crash` triggers it |
| ✅ Kernel log mirrored to `/var/log/boot.log`, previous boot kept as `boot.log.1` | S | `logger` writes to it |
| ✅ Local Cosmos source build + switch between local and released packages | M | `../Cosmos`, `tools/use-cosmos.sh`; see [DEVELOPMENT.md](DEVELOPMENT.md) |

**Exit:** a push builds and boot-tests automatically; a crash shows a readable panic screen. ✅

Found and fixed along the way: pipes and redirections now carry plain text, and `ls` prints
one entry per line when it isn't writing to the terminal.

## M1 · Usable daily shell (v0.2) ✅ done

| Item | Size | Notes |
|---|---|---|
| ✅ Each command line runs on a worker thread; Ctrl+C cancels (status 130); type-ahead | M | Cancellation is a flag, not exceptions: on Cosmos a typed `catch` before `catch (Exception)` wasn't selected |
| ✅ Keyboard layouts: `loadkeys`, `localectl set-keymap` → `/etc/vconsole.conf` | S | us, de, fr, es, gb, tr, dvorak |
| ✅ Time zones: `/etc/timezone`, `timedatectl`, `date [-u]`; built-in table with EU/US/AU daylight saving rules | S | Better than planned: real DST rules, not just fixed offsets |
| ✅ Clean shutdown/reboot (sync, unmount, flush); `sync`, `umount`, `mount part dir` | S | |
| ✅ Shell scripting: syntax tree + interpreter, functions, `$(...)`, `$((...))`, `test`/`[`, `read`, `sh`, `source`, scripts on `$PATH`, `> ` continuation prompt | L | |
| ✅ Text editor and `edit` command; replaces Notes | M | A desktop window rather than nano-in-the-terminal: that needs a raw-mode terminal first |
| ✅ `man`: pages for every command, hand-written `man sh`, `zenith`, `hier`, `install` | S | |
| ✅ Window resize (edges/corners), maximize (button, double-click), terminal reflows | M | |
| ✅ Selection and clipboard: editor (Shift+arrows, mouse, Ctrl+C/X/V), terminal (mouse, Ctrl+Shift+C/V) | M | |
| ✅ `diff`, `sed`, `xargs`, `seq`, `basename`, `dirname`, `du`, `printf`, `tr`, `cut`, `grep -E` | M | `less` left out until the terminal has a raw mode |

**Exit:** you can work in the terminal for an hour (German layout, scripts, editing files)
without hitting a missing basic. ✅

Possible follow-ups, not blocking M2: streaming pipes (one thread per pipeline stage), a raw
terminal mode with cursor addressing (enables `less`, `top`, a TUI editor), editor undo/search.

## M2 · Unix filesystem semantics (v0.3) (next)

| Item | Size | Notes |
|---|---|---|
| `/proc` virtual filesystem: `meminfo`, `mounts`, `uptime`, `cmdline`, later per-process entries | M | Implement `IVfsFilesystemType` with synthesized inodes |
| `/dev` virtual filesystem: `null`, `zero`, `random`, block devices (`sda`, `sda1`) | M | |
| **ext2 driver, read-write** | L | The native root filesystem: owners, permissions, symlinks, case-sensitive names, timestamps |
| Installer formats the root as ext2 (the ESP stays FAT32) | S | Depends on ext2 |
| Users: login screen, `/etc/shadow` with salted hashes, `passwd`, `su`, `useradd` | M | |
| Permission checks in the VFS path for file operations | M | |
| `fsck.ext2` (check-only first) | M | |
| ext4 read-only (extents, 64-bit), to read Linux disks | L | Optional, after ext2 |

**Exit:** an installed system runs on ext2 with real users and permissions, and `ls -l` shows
the truth.

## M3 · Networking (v0.4)

| Item | Size | Notes |
|---|---|---|
| Network service: DHCP at boot, `/etc/hostname`, `/etc/hosts`, `/etc/resolv.conf` | S | The Cosmos stack already has DHCP, DNS, UDP and TCP |
| `ip`/`ifconfig`, `ping`, `nslookup`, `nc` | M | |
| NTP client: set the clock at boot | S | Fixes wrong time in VMs |
| Plain-HTTP `fetch` (curl-like) and a tiny HTTP server for testing | M | No TLS in Cosmos yet |
| TLS | XL | Track Cosmos's plans (it's on their "Future" list) before writing our own |

**Exit:** a fresh boot gets an address and the right time, and can fetch a file over HTTP.

## M4 · Self-contained installer (v0.5)

| Item | Size | Notes |
|---|---|---|
| ↑ Limine module request in `Cosmos.Kernel.Boot.Limine` | S | Lets the ISO hand `BOOTX64.EFI` and the kernel to the running system |
| Installer copies the boot files itself; `make-bootable.sh` retires | S | Depends on the above |
| Root selection via `root=UUID=…` on the Limine command line | S | |
| Graphical installer window: disk picker, summary, confirmation, progress | M | Built on `Installer.Install` (already reports progress) |
| Install to a USB stick (Cosmos supports USB mass storage) | S | First step toward real hardware |
| ↑ GPT writer computes CRCs and the backup header | S | Then drop `GptChecksums` |
| BIOS boot (BIOS-boot partition + Limine stage 1) | M | Optional; UEFI covers modern PCs |

**Exit:** boot the ISO, click through the installer, reboot into the installed system. No
host tools involved.

## M5 · Running programs (v0.6)

The biggest architectural step: from "commands compiled into the kernel" to real programs.

| Item | Size | Notes |
|---|---|---|
| WebAssembly interpreter in the kernel | XL | Sandboxed by design; matches Cosmos's planned "userland WASM VM" |
| WASI preview 1 subset over our VFS: files, dirs, args, env, clocks, stdout/stderr | L | |
| Process table: `ps`, `kill`, exit codes, background jobs (`&`, `jobs`, `fg`) | L | Each program runs on its own thread |
| `/bin` holds `.wasm` programs; the shell looks up `PATH` before built-ins | S | |
| Toolchain doc + sample: build C/Rust/Zig to `wasm32-wasi`, copy into the image | S | |
| Package format and `pkg install` from a local directory or HTTP (needs M3) | L | |

**Exit:** a C program compiled with wasi-sdk on the host runs from `/bin` in Zenith, reads and
writes files, and shows up in `ps`.

## M6 · Desktop polish (v0.7)

| Item | Size | Notes |
|---|---|---|
| Widget toolkit: button, text box, list, scroll view, checkbox, layout | L | Every app afterwards gets cheaper |
| File manager | M | Needs the toolkit |
| Settings app: keyboard layout, time zone, wallpaper, display info | M | |
| Damage tracking: recompose only changed regions | M | Smooth window drags without KVM |
| ↑ virtio-gpu hardware cursor and partial flush | M | Pointer moves cost nothing |
| ↑ virtio-tablet (absolute pointer) | S | Host and guest cursors stay in sync with no grab |
| Notifications, Alt+Tab, keyboard shortcuts | M | |

**Exit:** the desktop is usable without the terminal for basic file and settings tasks.

## M7 · Real hardware (v0.8)

| Item | Size | Notes |
|---|---|---|
| ↑ USB HID mouse driver | M | Almost every real PC needs it |
| ↑ Realtek RTL8111/8168 and Intel I219 NIC drivers | L | Covers most desktop boards |
| Hardware test matrix: boot from a USB stick on 2–3 real UEFI machines; record what works | M | |
| ↑ Intel HDA audio | L | Optional |

**Exit:** Zenith boots from a USB stick on a real PC with working keyboard, mouse, storage and
wired network.

---

## Upstream work (Cosmos)

Cosmos keeps drivers and boot plumbing `internal`, so these belong in Cosmos, either as pull
requests or in our local fork until they land:

1. Limine module request (M4)
2. GPT CRC32s and backup header (M4)
3. virtio-tablet / `EV_ABS` (M6)
4. virtio-gpu cursor queue and partial `TransferToHost2D` (M6)
5. FAT timestamps (M1/M2; currently not persisted)
6. USB HID mouse, RTL8168, I219 (M7)
7. Intermittent #GP during `LegacyPic.RemapAndDisable`/APIC init under nested KVM (seen in CI): reproduce and report

## Decisions to make

| Decision | Recommendation | Why it matters |
|---|---|---|
| How programs run | WASM + WASI | The alternative (native ELF, ring 3, syscalls) means months inside Cosmos internals |
| Native root filesystem | ext2, then ext4 read-only | Our own format would save time now but lose host tooling (`mkfs`, `e2fsck`, `debugfs`) |
| Target hardware | QEMU first; one or two real UEFI machines from M7 | Decides which NIC and input drivers come first |
| Contributing upstream vs. forking Cosmos | Fork locally, upstream as we go | Some fixes are needed before Cosmos would release them |
