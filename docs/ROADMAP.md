# Zenith roadmap

Consolidated implementation roadmap, planning baseline **2026-10-11**. Historical
M0–M7 identities are retained; package (P), session (S) and recovery (R) workstreams
have explicit dependencies and can advance alongside them. Requirements are agreed
product direction; specific ABI, package format and snapshot backend remain design
choices. See [SYSTEM-PLAN](SYSTEM-PLAN.md) for detailed acceptance and
[PROGRESS](PROGRESS.md) for implementation evidence.

**Sizes:** S = a day or two · M = about a week · L = several weeks · XL = months.
**↑ upstream** = a change to Cosmos itself (its HAL is internal; see "Upstream work" below).

## Where we are (reviewed 2026-10-11)

M0/M1/M2 are complete within their documented scope. Supported bounded ext2,
installed accounts/authentication and filesystem permission enforcement are verified.
See [ARCHITECTURE](ARCHITECTURE.md) for current behavior and [PROGRESS](PROGRESS.md)
for exact evidence. Version labels below are milestone targets, not release claims.

**Working:** floating desktop, terminal/scripting, editor, simple Files test app,
manual pages, persisted keyboard/timezone configuration; x64 COM1 no-GPU headless;
shared IPv4 DHCP/static diagnostics, NTP and bounded HTTP/verified HTTPS fetching; proc/dev virtual files;
new installed ext2 roots up to 512 MiB with FAT32 ESP (legacy FAT roots recognized);
guarded ext2 formatting, symlinks, numeric ownership/modes, directory moves and
check-only consistency auditing. Live root remains RAM-backed FAT; `/tmp` uses
RAM-backed ext2 (1777). Installed ext2 accounts support hidden login/password prompts, explicit worker/window
credentials and enforced owner/group/other access; see [ACCOUNTS](ACCOUNTS.md).
The CLI installer supplies its own boot files; installed entries use exact root UUIDs
and versioned kernels. Sunrise and serial account setup are implemented; signed kernel
activation/confirmation/undo are implemented; native acceptance is tracked in PROGRESS.

**Latest local installation validation:** 532 host tests, 38 guided GUI and 47 guided
serial checks, 135 desktop/filesystem checks, 10 disk-only desktop UEFI checks and
71 signed-package checks under TCG (KVM installed-boot failures recorded in PROGRESS).
Prior foundation acceptance includes 28 install/reboot/rescue checks, two duplicate-root
refusal checks. See PROGRESS for
exact evidence and environment limits. Hosted CI awaits push.

**Historical M2 validation:** 414 host tests, eight Python tests, Release publish,
135 filesystem/installed-root assertions at 512 MiB with the large workload,
32 account/access/reboot assertions and seven UEFI disk-only assertions. Independent
host e2fsck is clean after account updates. Hosted CI rerun awaits push; see PROGRESS.

| Current gap | Consequence |
|---|---|
| Buffered pipelines; no raw terminal mode | Unbounded producers do not terminate through a later head; no general full-screen TUIs |
| Kernel-linked apps/commands; no process loader/isolation | New compiled apps require a kernel build; no general jobs/ps/kill |
| Trusted in-kernel apps and no process isolation | Credential/access checks cannot contain arbitrary kernel code |
| Bounded ext2, no journal/guest repair | Unsupported layouts refused; crash-dirty volumes need host e2fsck |
| Deferred unlink in Cosmos VFS | Namespace remains until final open handle closes |
| Simple Files and no Settings app | Files remains a test client; settings use commands |
| Temporary DE and x64 serial headless; WM unavailable | Independent userland, automatic tiling, proper Settings and Files remain future work |
| Offline signed scripts/data only | Compiled apps remain bundled; signed online indexes and boot generations remain planned |
| Early Cosmos boot fault observed under nested KVM | Harness retries the classified pre-Zenith fault; other failures stay failures |

## Agreed product direction

- One system supporting headless operation and graphical DE/automatic-tiling WM sessions.
- Desktop, apps and appropriate system services ultimately outside the main kernel,
  independently packaged/updatable with controlled APIs and failure isolation.
- Proper shared Settings, a full future Files rework, desktop customization and
  explicit usability/accessibility checks.
- Package-managed installed updates: install a new kernel alongside retained versions;
  the loaded kernel keeps running until reboot. Future desktop/app updates require
  their own restart rather than a kernel rebuild where compatible.
- Official package repository on a dedicated home VM with its own IP; direct VM services
  avoid existing container/port constraints. Reach it on LAN and remotely through the
  user's existing VPN and DNS. Direct public internet exposure is outside the initial scope.
- Saved-system boot selection, non-destructive trial boot and restoration as recovery
  goals. Investigate backend feasibility; Btrfs itself is not yet selected.

Details: [system plan](SYSTEM-PLAN.md), [userland](USERLAND.md),
[package design](PACKAGE-MANAGER.md), [server](PACKAGE-SERVER.md),
[recovery](UPDATE-RECOVERY.md). Current in-kernel services/UI are transitional.

## Delivery order and dependency gates

| Order | Work | Gate / dependency |
|---|---|---|
| Done | M2 accounts/credentials and permission policy | Native allow/deny/reboot and cryptographic-vector checks passed; trusted-kernel boundary documented |
| Implemented P0/P1 | Package builder/parser/planner and signed offline scripts/data | Native signature/ownership/transaction implementation; validation in PROGRESS; native userland and boot recovery remain separate gates |
| Done foundations | S0 shared settings/session seam and S1 serial headless | Native DE/no-GPU live and installed tests; Settings GUI/per-user preferences remain later work |
| Done M3/P1 | LAN networking and offline scripts/data | Native transport and signed install/upgrade/remove/reboot checks passed; exact evidence in PROGRESS |
| Implemented: 1 | M4 x64 VM installer | Sunrise and serial guided account setup, self-contained UEFI install, UUID selection and RAM rescue; native validation in PROGRESS |
| Done: kernel deployment gate | P3 offline kernel upgrades | Dedicated signed container/builder, full-sized delivery, disk-only activation/rollback, explicit confirmation, interruption/ISO recovery and real failed-boot menu fallback; 169 native checks in PROGRESS |
| 2 | Package recipes and builder workflow | Declarative metadata/files/modes; reproducible validation and separate signing using the existing .zpk foundation |
| P2 kit implemented; deployment pending | Debian VM builds and distributes packages | Manual build/sign/verified immutable publication and nginx config; actual VM/LAN/VPN/DNS and HTTPS bootstrap remain pending |
| 4 | Useful initial scripts/data packages | Install, upgrade, remove and reboot on a persistent Zenith VM against the user's server |
| 5 | P4 signed online repository client | Signed index freshness/rollback checks, search, deterministic dependency resolution and bounded download/install by name |
| 6 | M5 native loader/process/ABI then compiled tool unbundling; P5 | Protected execution, libc/toolchain foundation and independent base-tools/net-tools/fs-tools updates |
| User reports success; confirmation details pending | VMware installed kernel package upgrade | User reports successful manual upgrade; boot-confirm status not supplied. Dedicated mirror deployment follows; physical hardware and native app ABI remain separate |
| 7 | M6 proper DE/WM, Settings, customization, Files and GUI Editor overhaul | Shared toolkit/operations can start earlier; independent desktop/apps require M5 |
| Later developer tools | Host GCC cross-SDK, terminal editor/Python feasibility, then on-system GCC | [Application/toolchain plan](APPLICATIONS.md); native ABI/libc/TTY and measured storage/resource gates |
| 8 | R1/R2 saved roots, trial boot and restore | R0 backend/scope decision, explicit root selector, matched kernel/root/package inventory and interruption tests |
| When ready | M7 physical hardware and optional R3 efficient CoW snapshots | Dedicated device matrix; measured backend feasibility; neither blocks basic QEMU kernel updates |

The order is a dependency guide, not a release calendar. Network/GUI/server research
can overlap; app/desktop independence must not be declared complete through a shared
address-space plugin demo. Basic kernel updates can ship before full-root snapshots.
Initial server OS/web server, package schema/crypto and root-generation format are
implementation decisions, not reasons to reopen the agreed product direction.

---

## M0 · Foundation: make change safe ✅ done

Infrastructure that every later milestone leans on.

| Item | Size | Notes |
|---|---|---|
| ✅ GitHub Actions: unit tests, ISO build, QEMU smoke test on every push; ISO release on `v*` tags | S | `.github/workflows/ci.yml`; GitHub's runners have KVM |
| ✅ Host unit tests for the parser, shell and commands, plus the terminal's ANSI buffer | M | `tests/Zenith.Tests`; 64 tests at the original M0 checkpoint |
| ✅ QEMU smoke test: run with hidden host display, type commands over the monitor, assert on serial output | M | `tools/smoke-test.py`; covers pipes, `/tmp`, exit status, install and panic |
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

## M2 · Unix filesystem semantics (v0.3) (done)

| Item | Size | Notes |
|---|---|---|
| `/proc` virtual filesystem: `meminfo`, `mounts`, `uptime`, `cmdline`, later per-process entries | M | Initial read-only driver implemented; validation and remaining work in [PROGRESS.md](PROGRESS.md) |
| `/dev` virtual filesystem: `null`, `zero`, `random`, block devices (Cosmos names such as `sata0`, `sata0p1`) | M | Null/zero, fail-closed hardware random and root-readable disk/partition nodes implemented; raw writes remain intentionally refused. See [PROGRESS.md](PROGRESS.md) |
| **ext2 driver, read-write** | L | Supported bounded ext2 profile: secondary and installed roots, guarded formatter, ownership/modes, symlinks and directory moves. Native root/UEFI persistence verified; mounted permission adapter and native held-handle/path/access probes verified; see [PROGRESS.md](PROGRESS.md) |
| Installer formats the root as ext2 (the ESP stays FAT32) | S | Verified: ext2-root reboot/persistence and disk-only EFI boot; ESP remains FAT32 |
| Secure entropy and password hash verification | L | Implemented x64 RDSEED and PBKDF2-SHA256; host vectors and native self-check verified, ARM64 fails closed |
| Users: console/graphical login, `/etc/shadow` with salted hashes, `passwd`, `su`, `useradd` | L | Implemented ext2 shadow, hidden terminal login in the desktop, passwd/su/useradd and explicit worker/window credentials; reboot authentication verified |
| Permission checks for file operations | L | Mounted driver guards cover GUI/shell/System.IO/inodes, ownership, sticky/setgid and privileged operations; native access audit and denied-operation handle cleanup verified |
| Recovery and access diagnostics | M | Implemented read-only/dirty-root errors, authentication failures and permission denials; hidden input and serial secret-leak checks verified |
| `fsck.ext2` (check-only first) | M | Read-only supported-profile consistency audit verified with corruption fixtures and native checks; repairs remain host-side |
| ext4 read-only (extents, 64-bit), to read Linux disks | L | Optional, after ext2 |

**Exit:** an installed system runs on ext2 with real users and permissions, and `ls -l` shows
the truth.

## M3 · Networking (v0.4) — complete for the bounded LAN client profile

The bounded IPv4 LAN client profile is implemented; [NETWORKING](NETWORKING.md)
records launch profiles, home DNS configuration, verified HTTPS and version-pinned
Cosmos adaptations. Native acceptance passes 176/176 checks, including queries through
home DNS 10.0.10.3; exact validation is tracked in [PROGRESS](PROGRESS.md).
Actual package VM/VPN acceptance follows provisioning in P2; the VM does not exist
yet. Internet/server protocol hardening remains future work. Fetching over HTTPS
does not authorize package installation.

| Item | Size | Notes |
|---|---|---|
| Network service: DHCP at boot, `/etc/hostname`, `/etc/hosts`, `/etc/resolv.conf` | S | Implemented on pinned Cosmos: asynchronous primary-NIC DHCP, bounded local files and resolver overrides |
| `ip`/`ifconfig`, `ping`, `nslookup`, `nc` | M | Implemented diagnostics, correlated ICMP/DNS and TCP probes; nc interactive streams remain later work |
| Network lifecycle/configuration | M | Implemented DHCP/static/off saves, bounded reacquisition, cancellation; QEMU link-loss/missing-NIC tests; real hardware remains unverified |
| NTP client: set the clock at boot | S | Implemented boot/manual software UTC shared by BCL/filesystems; no default public server, RTC write or authenticated time |
| Bounded streaming fetch and host test server | M | Implemented 8 MiB staged HTTP/verified HTTPS transfer and local fault/cancel server; native TCP storage adaptation caps pending data at 128 KiB |
| Home DNS/VPN route integration | M | Implemented user/TAP/off launcher and saved DNS overrides; home DNS 10.0.10.3 verified from the guest; actual VM/VPN acceptance waits for P2 provisioning |
| TLS | XL | Implemented pinned Mbed TLS 3.6.7 freestanding client, TLS 1.2 ECDHE AES-GCM, CA/hash import, required chain/name/date verification, bounded arena/queues and fail-closed RDSEED; no insecure bypass |

**Exit:** a fresh boot obtains usable address/DNS, supports clock synchronization and
bounded file fetching with fault/cancellation tests. Report clock trust limits explicitly;
unauthenticated time synchronization alone does not prove metadata freshness.

## M4 · Self-contained installer (v0.5)

| Item | Size | Notes |
|---|---|---|
| Limine executable/module access | Done | Repository-local native bridge supplies original ELF and EFI bytes; no upstream edits |
| Installer copies the boot files itself | Done | CLI x64 UEFI install; former host helper is read-only verification |
| Explicit stable root selection from Limine command line | Done | GPT PARTUUID; missing/ambiguous selected roots fail closed; explicit RAM rescue |
| Versioned kernel boot entries and rescue route | Implemented | Signed kernel coordinator, retained previous entry, explicit confirmation/rollback/recovery; native acceptance in PROGRESS |
| Recovery-aware install layout | Partial | 128 MiB ESP reserves current/previous/staged bounded kernels; explicit undo and secondary boot menu; general power-loss/root snapshots and R0 remain planned |
| Graphical installer window and initial account setup | Done | Sunrise and interactive serial setup share target configuration; password/erase validation and worker progress |
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
| Toolchain/sample documentation | M | Host GCC cross-SDK preferred, startup/syscall/libc contracts; kernel ELF is not an application ABI |
| Future developer environment | XL | GCC running on Zenith, CPython, nano/Vim after libc/process/terminal and storage feasibility; [scope](APPLICATIONS.md) |
| Independent service/desktop/app packages | L | API compatibility, restart requirements, dependencies and recovery |
| Optional WASM runtime | XL | Evaluate after core ABI choice; host APIs and sandbox behavior need proof |

**Foundation exit:** a standalone app installs/runs/updates without relinking the kernel,
with observable process identity, controlled file access and fault isolation.
**Migration exit:** desktop/apps run independently behind a versioned graphics/IPC
contract; app failure does not panic the OS and desktop restart has a diagnostic
fallback. A text executable alone completes neither desktop migration nor full M5.

## M6 · Desktop sessions and applications (v0.7 target)

| Item | Size | Notes |
|---|---|---|
| Widget toolkit: button, text box, list, scroll view, checkbox, layout | L | Every app afterwards gets cheaper |
| Files rework | L | Basic test app exists; full workflow/transfer rework needs toolkit and shared operation service |
| GUI Editor overhaul | L | Document model, safe saves, undo/redo, search, UTF-8, keyboard workflows; tabs/highlighting follow; [scope](APPLICATIONS.md) |
| Settings app: shared configuration, keyboard/timezone/system, later users/network/updates | L | Implement pages only as supporting capabilities become available |
| Damage tracking: recompose only changed regions | M | Smooth window drags without KVM |
| ↑ virtio-gpu hardware cursor and partial flush | M | Pointer moves cost nothing |
| ↑ virtio-tablet (absolute pointer) | S | Host and guest cursors stay in sync with no grab |
| DE session: notifications, Alt+Tab, keyboard shortcuts, workspaces | L | Shared focus/dialog/workspace infrastructure |
| Customization and usability | L | Per-user themes, wallpaper, sizing/fonts, panel, shortcuts and WM preferences; preview/reset, accessible workflows and upgrade preservation |
| Automatic-tiling WM session | L | Shared compositor/apps; proposed master-and-stack policy, keyboard controls and floating dialogs |
| Headless/shared session startup | L | Defined in S workstream below; implement before full desktop migration |

**Exit:** DE and auto-tiling WM support basic file/edit/settings tasks with keyboard and pointer;
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

## S · Shared configuration and sessions

| Slice | Deliverable | Exit evidence |
|---|---|---|
| S0 | Typed/versioned config, shared CLI/GUI operations, session selector | System keyboard/timezone/session operations and test-DE host seam implemented; native validation in PROGRESS. Per-user preferences/Settings UI remain future work |
| S1 | Graphics-independent serial headless | Implemented on x64 COM1: no-GPU live/installed EFI, scripts/files/read/cancellation, hidden authentication, permissions, clean shutdown and panic/restart; exact evidence in PROGRESS |
| S2 | Shared focus/dialog/workspace/layout-policy infrastructure | Keyboard and pointer routes, small displays/minimum sizes, app behavior in both policies |
| S3 | DE and auto-tiling WM | DE floating workflow; WM automatic layout, directional focus/move, workspaces, floating dialogs and fullscreen |
| S4 | Proper Settings and customization | Supported capabilities only; themes/wallpaper/fonts/sizing/panel/keybindings/WM preferences; preview/reset and upgrade preservation |
| S5 | Full Files rework | Breadcrumbs/places/detail metadata/multi-select, chunked recursive operations, conflict/cancel/error policy and symlink/mount safety |

Proposed defaults: DE, serial-first headless and master-and-stack WM; exact shortcuts/
layout policy remain adjustable. Session choice and live/installed storage choice are
independent. Start with boot/login selection; live DE/WM switching is a later option.
Settings pages expand with account, network and update services rather than simulating
unavailable capabilities. Test keyboard-only first-run and routine workflows, readable
contrast/scaling, predictable focus, progress/error recovery and config persistence.
Files tests cover large/nested/cancelled/conflicting operations and independent integrity
inspection in both graphical sessions. Avoid unrestricted in-kernel theme/plugins.

## P · Packages, home repository and installed updates

| Slice | Deliverable | Exit evidence / dependency |
|---|---|---|
| P0 | Format/builder/reader/dry-run, trust-library and guest feasibility | Experimental data bundle builder/shared reader/guest dry-run implemented; RSA-PSS/SHA256 bounded .zpk and native verification implemented; online metadata/trust roles remain open |
| P1 | Offline data/script packages and installed DB | Implemented and native verified; exact evidence in PROGRESS. Ownership/ranges/conflicts/local-edit preservation and undo/DB commit recovery; /etc config merges remain later |
| P2 | Dedicated Debian build/distribution VM | Kit implemented: build/test/sign workflow, verified atomic publication and nginx; user-provisioned VM/IP/SSH, LAN/VPN/DNS and HTTPS acceptance pending |
| P3 | Staged versioned kernel/system artifacts | New kernel saved on disk while old runs; verify/flush/boot entry, explicit reboot-required state; retain previous kernel and rescue boot |
| P4 | Signed online package refresh/download/apply | M3 transport; pinned trust, metadata/artifact verification, expiry/rollback/key rotation; never install partial/untrusted payload |
| P5 | Independent service/desktop/app packages | M5 ABI compatibility and dependencies; explicit restart needs; kernel rebuild not needed for a compatible app update |

Package classes: data/scripts now, kernel/system artifacts for reboot activation, then
independent userspace services/desktop/apps. Start with bounded parsing, exact/simple
dependencies, streamed files and no arbitrary maintainer hooks. Protect user data and
modified config; distinguish current/staged/pending-boot versions. Signature verification
is required even on LAN/VPN. Container format, compression, algorithms/libraries and
trusted clock policy remain design gates. Kernel reboot and program/session restart
are different activation operations.

The server is a dedicated home VM running a maintained server OS and native static
web service, with its own IP. Existing VPN/DNS provide remote reachability; no new
public router port or container integration is planned. VM provisioning and signed
publication are not performed by this roadmap. Publish immutable artifacts first,
then a complete metadata generation; retain supported releases, isolate signing keys,
back up and test server outage/stale/corrupted responses. Exact deployment is in
[PACKAGE-SERVER](PACKAGE-SERVER.md); client design is in [PACKAGE-MANAGER](PACKAGE-MANAGER.md).

**Exit:** a provisioned VM serves verified packages on the intended LAN/VPN routes;
an existing Zenith install upgrades without rebuilding an ISO, remains usable until
requested restart/reboot and can select a retained bootable kernel. Crash-safe root
rollback is a separate R gate, not implied by keeping an old kernel.

## R · Saved-system boot, trial and restore

| Slice | Deliverable | Exit evidence / dependency |
|---|---|---|
| R0 | Snapshot scope/backend feasibility and capacity model | Decide system/config/package DB vs persistent user/service data; compare full copies/A-B, block CoW and Btrfs |
| R1 | Consistent retained root generations with matching boot artifacts | Explicit root identity, coherent package inventory, quiesced/correct capture and protected retention; M4 boot selector |
| R2 | Boot-menu saved-state selection, non-destructive trial and restore | Trial uses read-only plus ephemeral writes or disposable clone; restore activates writable state; disk-only tests and interrupted activation/restore |
| R3 | Efficient persistent CoW snapshot backend, if selected | Native mapping/filesystem transaction/recovery implementation and corruption/space/fault tests; optional Btrfs port is substantial work |

Desired menu: Current, Previous, Saved systems and Recovery. The bootloader selects
matching kernel and root/generation arguments; Zenith/recovery tools mount and restore.
Keep kernels on the ESP so this design need not assume Limine can read the chosen
snapshot filesystem. Retain the matching ESP artifacts separately from root states.
Manual fallback comes first; automatic attempt accounting/boot confirmation requires
its own verified mechanism.

Default proposal: preserve personal files through system rollback; define account,
configuration and service-schema compatibility explicitly. Trial must leave the saved
copy and shared mutable data unchanged unless a clearly selected operation permits it.
Snapshot retention protects active/pending/known-good roots and kernels. A snapshot is
not an external backup. Current ext2 has no snapshots/journal/guest repair; host/QEMU
snapshots are test tools, not native functionality. Full copies/A-B can demonstrate the
experience before efficient CoW; backend choice is open, and does not block P3.

**Exit:** boot and inspect a saved complete system, restore it as the default writable
system and verify matching kernel/package/config state. Inject failed boot, power loss,
short writes and out-of-space across creation/upgrade/activation/restore; show a usable
retained state/recovery route. Details: [UPDATE-RECOVERY](UPDATE-RECOVERY.md).

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
7. Protected process address spaces, privilege transitions and executable/runtime support as required by M5
8. Controlled userspace display/input, IPC and descriptor interfaces where framework APIs are insufficient
9. Read-only root/recovery primitives and persistent snapshot backend support if selected (R)
10. Intermittent #GP during `LegacyPic.RemapAndDisable`/APIC init under nested KVM (seen in CI): reproduce and report

## Remaining implementation decisions

| Decision | Recommendation | Why it matters |
|---|---|---|
| Kernel/userland boundary | Independent desktop/apps accepted; investigate protected native ABI/process model | WASM optional; ABI/runtime and Linux compatibility level remain open |
| Native root filesystem | Bounded ext2 implemented; ext4 read-only remains optional | Retain host interoperability and explicit crash/feature limits |
| Package format/trust | Bounded offline-first foundation, staged versioned kernels, signed online client | Schema/container/crypto/clock and transaction details need feasibility evidence |
| Repository deployment | Dedicated home VM, own IP, native services, existing VPN/DNS | Agreed topology; choose OS/web server, address/name, disks/backup and publish credentials at deployment |
| Saved-system backend | Prototype bootable root generations; evaluate CoW/Btrfs later | Snapshot scope, root identity, trial write layer and service-data compatibility still open |
| Sessions | DE default, serial-first headless, shared DE/WM compositor | User requirements accepted; policy/keybindings and delivery slices are proposals |
| Target hardware | QEMU first; one or two real UEFI machines from M7 | Decides which NIC and input drivers come first |
| Contributing upstream vs. forking Cosmos | Fork locally, upstream as we go | Some fixes are needed before Cosmos would release them |

## Planning baseline and next implementation

The product and hosting direction above is consolidated. Further design decisions are
local implementation gates, not missing product requirements. M2 is complete. The
user selected shared settings/session foundations while retaining the current test DE,
with early offline packaging. S0 system settings/session seam, S1 x64 serial headless
and the P0 prototype are implemented. M3 provides bounded LAN networking and verified
HTTPS. P1 signed offline script/data installation is native verified; see [PACKAGES](PACKAGES.md).
The user selected scripts/data first, then unbundle compiled tools. The now-approved
next order is self-contained ISO/installation, recipes/builder, server deployment kit,
useful packages on a persistent install, signed online client, then M5 native ABI and
compiled tool unbundling. Server preparation can run in parallel. The test DE remains.
Future Files/GUI Editor overhauls and GCC-preferred/Python/nano/Vim investigation are
recorded in [APPLICATIONS](APPLICATIONS.md); celestial names remain proposals.
Further online metadata/trust and native-userland/R0 research may advance as explicitly
selected experiments; do not deploy the VM or begin a Btrfs port implicitly. Every slice updates
PROGRESS with code status, exact validation and unresolved limitations. Maintain the
architecture/history/user-facing docs alongside each delivered capability.
