# Zenith roadmap

Milestones in recommended order. Each has a goal, its work items, and an exit criterion that
says when it's done. Background and effort estimates for the big items are in
[TECHNICAL-NOTES.md](TECHNICAL-NOTES.md).

**Sizes:** S = a day or two · M = about a week · L = several weeks · XL = months.
**↑ upstream** = a change to Cosmos itself (its HAL is internal; see "Upstream work" below).

## Where we are (v0.1)

**Working:**
- Desktop: compositor, windows, taskbar, launcher.
- Terminal with a POSIX-flavored shell and ~45 built-in commands.
- FAT32 root (installed disk) or RAM root (live), `/tmp` in RAM.
- Installer (GPT + ESP + root), finished by a host-side bootable step.
- Fast QEMU setup: KVM, virtio-gpu, virtio input.

**Known gaps in what exists:**

| Gap | Effect today |
|---|---|
| Shell commands run on the GUI loop | A long command (`install`, `find /`) freezes the desktop; Ctrl+C can't interrupt it |
| No unhandled-exception handling | An exception in `Run()` prints over the screen and halts the kernel |
| Clock and dates | Times are UTC, and the FAT driver doesn't store timestamps (`ls -l` shows 1970) |
| Keyboard layout | US only, although Cosmos ships DE, FR, ES, GB, TR and Dvorak |
| Notes | Can't open or save files |
| Windows | Can't be resized or maximized; the terminal is fixed at its startup size |
| Clipboard | None, and no text selection |
| Tests and CI | None; everything was verified by hand in QEMU |

---

## M0 · Foundation: make change safe (next)

Infrastructure that every later milestone leans on.

| Item | Size | Notes |
|---|---|---|
| GitHub Actions: build the ISO on every push, attach it to tagged releases | S | `dotnet tool install -g Cosmos.Tools && cosmos install` works headless on Linux |
| Host unit tests for the parser, shell, path handling and commands | M | These only use `System.IO`, so a normal xunit project can compile the same sources against a temp directory |
| QEMU smoke test: boot headless, type commands over the monitor, assert on serial output | M | Generalize the `vm.py` harness used during development; run it in CI (KVM if the runner has it, TCG otherwise) |
| Panic screen: catch exceptions from `Run()`, show the message and stack on a red screen, write it to the log | S | |
| Persist the kernel log to `/var/log/boot.log` on installed systems | S | |
| Local Cosmos source build + local NuGet feed | M | Needed for every ↑ upstream item; see `docs/cosmos/articles/dev/install-dev.md` |

**Exit:** a push builds and boot-tests automatically; a crash shows a readable panic screen.

## M1 · Usable daily shell (v0.2)

| Item | Size | Notes |
|---|---|---|
| Run each command line on a worker thread; Ctrl+C sets a cancellation flag that commands check | M | Cosmos has a preemptive scheduler and threads. Terminal output is marshalled back to the GUI thread. |
| Keyboard layouts: `loadkeys de`, persisted in `/etc/vconsole.conf` | S | Uses Cosmos `KeyboardManager.SetKeyLayout` and its ScanMaps |
| Time zone: `/etc/timezone` as a fixed offset (no tz database yet); `date`, the clock and `ls -l` use it | S | |
| Clean shutdown/reboot: unmount and flush every filesystem first; add `sync` and `umount` | S | |
| Shell scripts: `sh file`, `#!` files, `if`/`for`/`while`, `test`/`[`, `read`, functions | L | Turns `/etc/profile` into real init scripts |
| Text editor in the terminal (nano-like) and `edit` command; Notes gains open/save | M | |
| Manual pages: `man cmd` reading `/usr/share/man/*.txt`, generated from command metadata | S | |
| Window resize and maximize; the terminal reflows to the new size | M | |
| Text selection and clipboard (terminal + Notes), Ctrl+Shift+C/V | M | |
| More commands: `less`, `diff`, `sed` subset, `xargs`, `seq`, `basename`/`dirname`, `du` | M | |

**Exit:** you can work in the terminal for an hour (German layout, scripts, editing files)
without hitting a missing basic.

## M2 · Unix filesystem semantics (v0.3)

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

## Decisions to make

| Decision | Recommendation | Why it matters |
|---|---|---|
| How programs run | WASM + WASI | The alternative (native ELF, ring 3, syscalls) means months inside Cosmos internals |
| Native root filesystem | ext2, then ext4 read-only | Our own format would save time now but lose host tooling (`mkfs`, `e2fsck`, `debugfs`) |
| Target hardware | QEMU first; one or two real UEFI machines from M7 | Decides which NIC and input drivers come first |
| Contributing upstream vs. forking Cosmos | Fork locally, upstream as we go | Some fixes are needed before Cosmos would release them |
