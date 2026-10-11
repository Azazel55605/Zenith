# Zenith roadmap

Milestones in recommended order. Each has a goal, its work items, and an exit criterion that
says when it's done. Background and effort estimates for the big items are in
[TECHNICAL-NOTES.md](TECHNICAL-NOTES.md).

**Sizes:** S = a day or two · M = about a week · L = several weeks · XL = months.
**↑ upstream** = a change to Cosmos itself (its HAL is internal; see "Upstream work" below).

## Where we are (reviewed 2026-10-11)

M0/M1 are complete. Supported bounded ext2 is implemented and verified;
**M2 is still open for real users/authentication and permission enforcement**.
See [ARCHITECTURE](ARCHITECTURE.md) for current behavior and [PROGRESS](PROGRESS.md)
for exact evidence. Version labels below are milestone targets, not release claims.

**Working:** floating desktop, terminal/scripting, editor, simple Files test app,
manual pages, persisted keyboard/timezone configuration; proc/dev virtual files;
new installed ext2 roots up to 512 MiB with FAT32 ESP (legacy FAT roots recognized);
guarded ext2 formatting, symlinks, numeric ownership/modes, directory moves and
check-only consistency auditing. Live root and `/tmp` remain RAM-backed FAT.
Installer boot files still require a host helper.

**Latest local validation at `8389113`:** 374 host tests, eight Python tests,
Release publish, 135 baseline + 135 large-profile QEMU assertions and seven
UEFI disk-only assertions. Hosted CI rerun awaits push.

| Current gap | Consequence |
|---|---|
| Buffered pipelines; no raw terminal mode | Unbounded producers do not terminate through a later head; no general full-screen TUIs |
| Kernel-linked apps/commands; no process loader/isolation | New compiled apps require a kernel build; no general jobs/ps/kill |
| Ownership/modes without credentials/enforcement | Metadata is truthful but cannot restrict access yet |
| Bounded ext2, no journal/guest repair | Unsupported layouts refused; crash-dirty volumes need host e2fsck |
| Deferred unlink in Cosmos VFS | Namespace remains until final open handle closes |
| Simple Files and no Settings app | Files remains a test client; settings use commands |
| Guest always constructs Desktop | Hidden-window QEMU testing is not a headless OS session |
| No integrated network/package service | Online installed updates and mirror management remain planned |
| Early Cosmos boot fault observed under nested KVM | Harness retries the classified pre-Zenith fault; other failures stay failures |

## Expanded product workstreams

The user-requested product scope is detailed in [SYSTEM-PLAN](SYSTEM-PLAN.md):
headless plus desktop DE/automatic-tiling WM, future independently loaded desktop/apps,
customization/usability and shared configuration/services,
proper Settings and a future Files rework. Package architecture is a
[design discussion](PACKAGE-MANAGER.md), not a settled implementation.

Keep milestone identities stable. Add offline package research/foundation alongside
M2/M3; coordinate installed-system activation with M4 and independent apps with M5.
Expand M6 to include both desktop session policies. Shared session/configuration
seams and serial headless can start earlier once dependencies are defined. Each
workstream has acceptance gates; documentation does not complete them.

---

## M0 · Foundation: make change safe ✅ done

Infrastructure that every later milestone leans on.

| Item | Size | Notes |
|---|---|---|
| ✅ GitHub Actions: unit tests, ISO build, QEMU smoke test on every push; ISO release on `v*` tags | S | `.github/workflows/ci.yml`; GitHub's runners have KVM |
| ✅ Host unit tests for the parser, shell and commands, plus the terminal's ANSI buffer | M | `tests/Zenith.Tests`; 64 tests at the original M0 checkpoint |
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

## M2 · Unix filesystem semantics (v0.3) (in progress)

| Item | Size | Notes |
|---|---|---|
| `/proc` virtual filesystem: `meminfo`, `mounts`, `uptime`, `cmdline`, later per-process entries | M | Initial read-only driver implemented; validation and remaining work in [PROGRESS.md](PROGRESS.md) |
| `/dev` virtual filesystem: `null`, `zero`, `random`, block devices (`sda`, `sda1`) | M | Null/zero and read-only live disk/partition nodes implemented with bounded `dd`; entropy and raw writes pending. See [PROGRESS.md](PROGRESS.md) |
| **ext2 driver, read-write** | L | Supported bounded ext2 profile: secondary and installed roots, guarded formatter, ownership/modes, symlinks and directory moves. Native root/UEFI persistence verified; permission enforcement remains pending; see [PROGRESS.md](PROGRESS.md) |
| Installer formats the root as ext2 (the ESP stays FAT32) | S | Verified: ext2-root reboot/persistence and disk-only EFI boot; ESP remains FAT32 |
| Users: console/graphical login, `/etc/shadow` with salted hashes, `passwd`, `su`, `useradd` | L | Pending: validated entropy/hash primitives, credential context and operation policy first |
| Permission checks for file operations | L | Pending: GUI, shell, System.IO and direct-VFS entry points; ownership on creation |
| `fsck.ext2` (check-only first) | M | Read-only supported-profile consistency audit verified with corruption fixtures and native checks; repairs remain host-side |
| ext4 read-only (extents, 64-bit), to read Linux disks | L | Optional, after ext2 |

**Exit:** an installed system runs on ext2 with real users and permissions, and `ls -l` shows
the truth.

## M3 · Networking (v0.4)

| Item | Size | Notes |
|---|---|---|
| Network service: DHCP at boot, `/etc/hostname`, `/etc/hosts`, `/etc/resolv.conf` | S | Framework networking is documented; validate installed APIs and native Zenith integration before relying on them |
| `ip`/`ifconfig`, `ping`, `nslookup`, `nc` | M | |
| NTP client: set the clock at boot | S | Fixes wrong time in VMs |
| Plain-HTTP `fetch` (curl-like) and a tiny HTTP server for testing | M | TLS is not integrated/verified in Zenith; public update transport requires a separate design |
| TLS | XL | Evaluate guest-compatible maintained libraries and upstream support; validate crypto and entropy natively |

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

## M5 · Independent userland and programs (v0.6 target)

The accepted long-term direction is Linux-like separation: desktop and apps outside
of the main kernel, independently installable/updatable and isolated. This does not
commit Zenith to Linux binary compatibility. [USERLAND](USERLAND.md) defines migration
and acceptance. Native ABI/process feasibility is the primary investigation; WASM/WASI
remains optional rather than an assumed final desktop runtime.

| Item | Size | Notes |
|---|---|---|
| Kernel/userland ABI and loader design | XL | Executable/startup/runtime choice; protected native processes require Cosmos work |
| Process isolation/lifecycle and credentials | XL | Memory, descriptors, args/env/cwd, wait/exit, cancellation, bounded resources |
| IPC, byte pipes and terminal contract | L | Versioned controlled APIs; Bash dependencies in [assessment](BASH-PORT.md) |
| First independent text and graphical apps | L | Application faults must not panic kernel; controlled display/input interface |
| Desktop/compositor/DE/WM migration | XL | Separate packages/processes behind shared graphics/toolkit protocols |
| Toolchain/sample documentation | M | Build for chosen ABI; kernel ELF is not an application ABI |
| Independent service/desktop/app packages | L | API compatibility, restart requirements, dependencies and recovery |
| Optional WASM runtime | XL | Evaluate after core ABI choice; host APIs and sandbox behavior need proof |

**Exit:** a standalone app installs/runs/updates without relinking the kernel, with
observable process identity, controlled file access and fault isolation. Desktop
migration is an explicit subsequent gate; a text executable alone does not complete it.

## M6 · Desktop sessions and applications (v0.7 target)

| Item | Size | Notes |
|---|---|---|
| Widget toolkit: button, text box, list, scroll view, checkbox, layout | L | Every app afterwards gets cheaper |
| Files rework | L | Basic test app exists; full workflow/transfer rework needs toolkit and shared operation service |
| Settings app: shared configuration, keyboard/timezone/system, later users/network/updates | L | Implement pages only as supporting capabilities become available |
| Damage tracking: recompose only changed regions | M | Smooth window drags without KVM |
| ↑ virtio-gpu hardware cursor and partial flush | M | Pointer moves cost nothing |
| ↑ virtio-tablet (absolute pointer) | S | Host and guest cursors stay in sync with no grab |
| DE session: notifications, Alt+Tab, keyboard shortcuts, workspaces | L | Shared focus/dialog/workspace infrastructure |
| Customization and usability | L | Per-user themes, wallpaper, sizing/fonts, panel, shortcuts and WM preferences; preview/reset, accessible workflows and upgrade preservation |
| Automatic-tiling WM session | L | Shared compositor/apps; proposed master-and-stack policy, keyboard controls and floating dialogs |
| Headless/shared session startup | L | Can begin earlier: graphics-independent serial shell and shared service/configuration seam |

**Exit:** DE and auto-tiling WM support basic file/settings tasks with keyboard and pointer;
headless boots without graphical initialization and exposes the shared shell/services.
See [SYSTEM-PLAN](SYSTEM-PLAN.md) for detailed acceptance gates.

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
| Kernel/userland boundary | Independent desktop/apps accepted; investigate protected native ABI/process model | WASM optional; ABI/runtime and Linux compatibility level remain open |
| Native root filesystem | Bounded ext2 implemented; ext4 read-only remains optional | Retain host interoperability and explicit crash/feature limits |
| Installed updates and packages | Offline-first data packages, coordinated kernel/data releases, signed mirror later | Format, trust, recovery and runtime choices remain open; [design](PACKAGE-MANAGER.md) |
| Sessions | DE default, serial-first headless, shared DE/WM compositor | User requirements accepted; policy/keybindings and delivery slices are proposals |
| Target hardware | QEMU first; one or two real UEFI machines from M7 | Decides which NIC and input drivers come first |
| Contributing upstream vs. forking Cosmos | Fork locally, upstream as we go | Some fixes are needed before Cosmos would release them |

## Update hosting and snapshot direction

Initial official repository: user's home infrastructure, LAN-only; deployment details
pending. [PACKAGE-SERVER](PACKAGE-SERVER.md) records the plan. Requested recovery
experience includes saved-system boot/trial/restore. [UPDATE-RECOVERY](UPDATE-RECOVERY.md)
compares root generations/A-B, block CoW and Btrfs. Ext2 snapshots are not implemented,
and keeping an old kernel is not a full root rollback. Kernel-only staged updates do
not require a snapshot backend; complete rollback does require a defined root/data scope.
