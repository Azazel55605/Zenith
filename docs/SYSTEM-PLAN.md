# Zenith system plan

Product requirements and proposed delivery plan, 2026-10-11. Current behavior is
in [ARCHITECTURE](ARCHITECTURE.md). This document records the requested direction:
**desktop and headless operation; desktop DE and automatic-tiling WM sessions;
a proper Settings app; future Files and GUI Editor reworks; installed-system updates**.
Later clarification: **desktop and applications must ultimately move outside the kernel**;
customization is required alongside the proper desktop implementation. Home package
hosting uses a dedicated VM with its own IP, LAN access and existing VPN/DNS remote access. Snapshot/trial-boot/restore behavior is a requested investigation.
Implementation mechanisms below are proposals unless explicitly marked current.
See [userland separation](USERLAND.md), [home repository](PACKAGE-SERVER.md) and
[upgrade recovery](UPDATE-RECOVERY.md).

## 1. System shape

One operating system should support three session choices using the same filesystem,
accounts, settings, network and package services. Session selection should not require
separate ISOs or separate application implementations.

| Session | Intended user experience | Initial scope |
|---|---|---|
| Headless | Command-driven system without graphical initialization | Serial console, shell, service status, install/update/recovery commands |
| Desktop DE | Familiar graphical environment with floating windows | Launcher, taskbar, workspaces, Settings, Files, notifications and shortcuts |
| Desktop WM | Keyboard-oriented graphical environment with automatic tiling | Automatic layout, workspaces, directional focus/move, minimal panel and launcher |

Proposed default is DE, retaining today's familiar behavior. Headless must not load
fonts, allocate desktop frame surfaces or require framebuffer/input devices. WM
still needs graphics and applications; it changes window layout and shell chrome.
A graphical fallback failure should offer diagnostics and a headless/recovery route
when the console backend is available.

Separate **session mode** from **storage boot mode**: DE/WM/headless can each run
with live or installed root. Live sessions cannot promise persistent updates.
A proposed boot argument such as `session=headless|de|wm` should override a saved
system default. Names, config path and precedence require implementation review.
Start with selection at boot/login; switching an active session while preserving
windows is a later feature, not an initial requirement.

## 2. Shared boot and services

Refactor the current unconditional Desktop startup into shared initialization followed
by a session host. Keep the existing shell interpreter and core models reusable.
A service initially means a bounded in-kernel component with explicit lifecycle.
This is a transitional implementation. The accepted long-term direction is independently
loaded desktop/apps and appropriate userspace services behind controlled kernel APIs,
with process isolation. Shared compositor means shared functionality/protocols across
DE/WM; it does not require permanent linkage into the kernel.

Proposed boot phases:

1. Hardware discovery, storage/root selection, recovery checks and logs.
2. Configuration loading and validation; invalid settings fall back with a diagnostic.
3. Accounts/credentials and mount access policy using M2 enforcement.
4. Shared clock, networking and package state/recovery initialization as implemented.
5. Session selection, authentication and console/desktop startup.
6. Idle loop with shared memory-pressure policy and service work scheduling.

Services should expose start/stop/state/error information, cancellation, dependency
ordering and resource limits. An unavailable NIC should not stop local login.
A failed update should not silently activate incomplete files. Shutdown must stop new
writes, coordinate active jobs, flush/unmount storage and preserve current clean-state
behavior. Logs must remain usable in every session, with serial output during boot.

Initial proposed service interfaces: configuration, credentials/access policy,
clock, network status, packages/updates and sessions. Build them only when the
corresponding slice needs them; avoid a framework whose behavior cannot be tested.
CLI and GUI must call the same operation layer, validation and persistence paths.

## 3. Headless mode

Serial is the recommended first console because it permits genuinely graphics-free
QEMU tests and early recovery. A local text console can follow; its framebuffer
requirements must be explicit. Remote login is deferred until network transport,
authentication and encryption are supported. A serial login is not SSH.

The first slice should provide a console adapter for shell input/output, cancellation
and prompts, preserve scripts and exit status, and register applicable system commands
without invoking Desktop. GUI-only commands must explain their unavailable capability.
Do not make serial log output indistinguishable from interactive prompt output.
Bound line input, output buffering and worker concurrency.

Acceptance: boot a disposable installed and live system with no graphical session;
run scripts, inspect mounts/logs, manipulate test files, cancel a command, sync and
power off. Confirm no desktop canvas construction. Repeat with missing/invalid
session config and no graphical device. Later add authenticated console login,
service management and update commands using the same core services as the DE.

## 4. Desktop DE

Retain floating windows as the default, then add dependable keyboard focus, Alt+Tab,
workspace navigation, consistent menus/dialogs and shared widgets. Workspaces,
window ownership and modal dialogs should be common infrastructure used by WM too.

The first toolkit should cover buttons, labels, text fields, lists, scroll views,
checkboxes, selection/focus, validation/error messages and simple layouts. Apps should
work with keyboard and pointer, tolerate small windows and follow one theme. Define
minimum size and dialog-parent relationships before changing window placement.
Use explicit redraw invalidation; expand damage tracking only with measured benefit.

Initial DE completeness means opening applications, editing settings, managing files
and inspecting update/network state without entering shell commands. Display controls
must show supported capabilities: a fixed-resolution GOP display cannot offer an
unimplemented resolution switch. Customization is a required part of the proper desktop phase. Add per-user theme/colors,
wallpapers, fonts/UI sizing, cursor preferences, panel/launcher layout, shortcut editing,
and WM gaps/borders/master ratio/workspace preferences through the same settings service.
Use declarative, versioned theme/config data with safe defaults, previews/reset and
bounded resource loading. Imported themes must not execute arbitrary kernel code.
Preserve customized settings across package upgrades. Desktop extensions, if added,
need the userland/runtime trust model rather than unrestricted in-kernel plugins.

Usability gates: discoverable actions/shortcut help, consistent focus and dialogs,
keyboard-complete operation, readable contrast/scaling, visible progress/cancellation,
clear error recovery and restore-default controls. Test first-run workflows and small
windows in DE and WM. Multi-monitor and advanced effects remain later work.

Acceptance: a keyboard-only and pointer-only workflow across launcher, Settings,
Files, Editor and dialogs; correct focus after close/minimize; stable resize/maximize;
usable layout at supported small resolutions; settings survive installed reboot.

## 5. Automatic-tiling WM

Share window objects, application registry, input, compositor and settings with DE.
Introduce a layout-policy seam rather than embedding tiling rules in individual apps.
Window identity/focus should not depend solely on floating z-order.

Recommended first policy is **master-and-stack**: the first tiled window occupies a
master region and remaining windows divide the stack. It is simpler to learn/test
than a configurable tree of splits. A binary-split layout can be evaluated later.

Required first behavior:

- New normal windows tile automatically; close/minimize recomputes the layout.
- Workspaces maintain independent window sets and focused windows.
- Keyboard commands focus directionally, move/reorder windows, adjust master ratio,
  move a window to another workspace, toggle floating and toggle fullscreen.
- Dialogs/tool windows remain floating above their parent and retain keyboard focus.
- Floating apps preserve a usable size; minimum-size constraints never produce negative
  or inaccessible geometry. When too many windows fit poorly, use a documented stacked
  fallback rather than shrinking controls indefinitely.
- Reserve space for a minimal status panel and provide discoverable shortcut help.

Shortcut examples using a configurable modifier are proposals, not assignments;
check conflicts with current terminal/editor shortcuts and DE controls first.
Persist layout preferences and keybindings, not arbitrary window/process restoration
until programs have stable identities and restart behavior.

Acceptance: pure layout tests for 0/1/many windows, small displays and minimum sizes;
native keyboard sequences for creation, focus, move, workspace switching, dialogs,
floating/fullscreen and closing the focused window. Verify apps work in both policies
without app-specific layout changes. Switching DE/WM initially occurs at login/reboot.

## 6. Proper Settings application

Settings must be a view over a shared configuration service, also usable through CLI.
Use typed values, schema versioning, explicit defaults and invalid-value diagnostics.
Keep existing keyboard/timezone paths compatible or provide a migration. Separate
system defaults from per-user preferences once real users exist; avoid writing all
personal choices into root-owned global configuration.

| Page | First deliverable | Dependencies/later work |
|---|---|---|
| System/About | Build identity, boot/session mode, memory, storage and capability summary | Export diagnostic report; report actual version, not target roadmap version |
| Keyboard/Input | Existing layouts, shortcut help and session shortcuts | Repeat/pointer controls only where driver support exists |
| Date/Time | Existing timezone selection and current clock | NTP enable/state/error when networking works |
| Appearance/Desktop | Session default, theme/wallpaper selection, WM layout preference | User scope, workspaces and accessible sizing |
| Display | Current resolution/backend; supported operations | Mode switching and multiple monitors only after driver support |
| Storage | Mounts, capacity and filesystem status | Read-only diagnostics first; destructive operations need clear target confirmation |
| Users | Real account state and password changes | M2 credentials, secure entropy, access policy; not fake UI accounts |
| Network | Interface/link/address/DNS status and configuration | M3 service; mark unavailable capabilities explicitly |
| Updates | Available/installed versions, plan, progress, reboot/recovery state | Package service after its offline and recovery gates |

Persist only after validation. Apply immediately when reversible and supported;
clearly indicate login/reboot requirements. On save failure retain the previous
configuration and show an actionable error. Proposed config replacement must be
validated against ext2/FAT rename/flush behavior; do not assume crash-atomic writes.

Acceptance: invalid config, read-only/live roots, permission denial, unsupported
hardware, save failure and installed reboot. A CLI change must be reflected on page
refresh, and a GUI change must be readable through CLI. Passwords never enter logs.

## 7. Files rework

Keep the current Files app as a test tool until shared widgets and file-operation
services are ready. Rework the interaction and operation model together, rather than
adding many special cases to the existing toolbar.

First production-oriented scope: breadcrumb/location entry, places/sidebar (home,
root, mounts), detail list with actual metadata, stable selection, sorting, keyboard
navigation, visible error/progress state and multi-selection. File actions should use
one core operation planner also callable by CLI and future package services.

Follow-up operations: recursive copy/move, cross-filesystem moves by verified copy
then deletion, symlink-aware choices, cancellation, conflict choices (skip/rename/
overwrite only when explicitly selected), permission diagnostics and editor/app
associations. Chunk large files rather than keeping complete transfers in RAM.
Cancellation must identify partial results; do not delete source before verified copy.
Folder recursion must bound depth and avoid symlink cycles/mount escapes. Device and
virtual files need explicit behavior and should not be recursively copied by default.
Trash requires a persistent policy and space/account handling; permanent delete stays
clearly labeled until that exists. Thumbnails, tabs and search/indexing are later.

Acceptance: nested and large files, conflicting names, symlinks/dangling links,
read-only mounts, permission failure, out-of-space, cancelled transfers and reboot
persistence. Independent e2fsck remains required for new mutation paths. GUI tests
must cover both DE and WM; shell rename coverage is not proof of GUI workflow.

## 7a. Graphical Editor overhaul and application identity

User requested a future GUI Editor overhaul alongside Files: reliable open/save,
UTF-8/newline handling, selection/clipboard, undo/redo, find/replace, modified-state
and external-change handling, keyboard operation and clear permission/save errors.
Tabs/highlighting and optional recovery copies follow the core document model.
Compiler/IDE integrations require native program execution and IPC.

Zenith-owned packages/apps should share a celestial/daylight naming identity.
Sunrise/Sunset, Horizon (Files) and Aurora (Editor) are proposed names, not final
renames. Keep descriptive labels and familiar upstream gcc/python3/vim/nano names.
See [APPLICATIONS](APPLICATIONS.md) for proposals and toolchain feasibility gates.

## 8. Accounts, network, programs and updates

M2 implements installed ext2 credentials, salted password hashing with host/native
crypto vectors and hardware entropy checks, creation ownership and filesystem
operation access checks. See [ACCOUNTS](ACCOUNTS.md) for setup and scope. Shell identity alone
is insufficient if raw VFS/GUI calls bypass it. Carry credentials per operation/worker,
not through a shared mutable global user. Audit read/write/create/traverse/delete/rename,
chmod/chown, mount boundaries and privileged device/installer paths; test owner/group/other
allow/deny and directory search/write rules as new services are added. Sticky removal
and setgid creation inheritance are enforced; setuid/setgid executable privilege
is deferred until a userland loader exists. Until process isolation exists,
in-kernel code remains trusted and cannot be safely treated as third-party userland.

M3 should establish wired-QEMU network operation first: device/link lifecycle,
DHCP/static settings, DNS, routing, bounded TCP transfers, timeouts/retry/cancel and
status diagnostics. Check the actual installed Cosmos API; vendored networking docs
are a framework reference, not evidence of Zenith integration. Add test-server
fixtures for truncated/disconnected/oversized responses. Do not assume TLS or secure
random support from host .NET APIs. A public mirror needs an agreed trust design.

Installed updates and application packages are related but distinct. Kernel plus
matched system data can be published and applied without generating a new ISO;
compiled code still requires a build and reboot. Independent application packages
need the M5 runtime/API contract. The userland split is the accepted target; protected native ABI/process research takes
priority over assuming a WASM-only design. WASM remains an optional candidate runtime,
and Bash needs more than a basic WASI subset; see [Bash assessment](BASH-PORT.md). Package plans are
in [the separate design discussion](PACKAGE-MANAGER.md).

## 9. Delivery slices and gates

Existing M0–M7 names remain stable; workstreams can overlap once dependencies are met.
No dates or new release tags are promised by this plan.

| Slice | Deliverable | Gate before moving on |
|---|---|---|
| A: M2 completion (verified) | Credentials, creation ownership, enforced operation policy | Positive/negative access tests and native login/reboot; secure entropy/password design |
| B: shared configuration/session seam | Typed config service, session selector, graphics-independent startup | Existing desktop regression and installed setting persistence |
| C: headless console | Serial shell/session and shared diagnostics | Boot without graphics; files/scripts/cancellation/shutdown tested |
| P0: package research, can start now | Host package builder, schema, verifier feasibility, dry-run plan | Discussion decisions recorded; no guest activation |
| P1: offline data packages | Bounded reader, ownership DB, transactions, CLI | Untrusted input, conflicts, interrupted writes and reboot recovery verified |
| D: toolkit and Settings foundation | Basic widgets and existing keyboard/timezone/system pages | CLI/GUI consistency, errors and reboot persistence |
| E: DE/WM session policies | Workspaces, auto tiling, focus and shortcuts | Layout tests plus native interaction in both sessions |
| N: M3 network foundation | Wired config/DNS/TCP and bounded fetch | Native network faults and cancellation, not host-only fetch |
| P2: home repository VM | Native static service, own IP, LAN/VPN/DNS routes and separate publishing/signing | Provisioned endpoint and host/client route tests |
| P3: installed system update | Matched kernel/data generation and boot recovery | ESP update/recovery proof including interrupted activation; M4 boot plumbing |
| P4: signed mirror integration | Publisher/index/trust and bounded downloads | Guest signature verification, expiry/rollback policy and recovery proven |
| F: Files rework | New browser and chunked operations | Data integrity/error/cancellation tests in DE and WM |
| R: M5 userland foundation | Loader, isolation, kernel/API/IPC contract and independent desktop/app migration | App install/update without rebuilding kernel; failures/access boundaries tested |
| P5: app packages | Runtime compatibility and launcher registration | Install/run/remove independently of kernel rebuild |

P0 does not need networking. P1 cannot claim safe system upgrades. P2 is server preparation; P3 stages versioned
kernels and P4 adds signed online client integration. Snapshot recovery is tracked
separately as R0–R3 in ROADMAP and UPDATE-RECOVERY. D can begin with
existing settings while advanced pages await their services. Runtime and hardware
remain substantial work; advancing docs does not complete these gates.

## 10. Decisions for the next discussion

Proposed defaults: DE as boot default; serial-first headless; shared DE/WM compositor;
master-and-stack initial tiling; offline-first package foundation; matched kernel/data
system releases before independent apps; no third-party maintainer scripts initially.

Still to settle: headless local-console requirements, session switching expectations,
preferred tiling interaction/keybindings, user vs global settings, runtime choice,
package metadata/container/trust libraries, version/dependency policy, system rollback
layout and the repository VM deployment and separate signing process. Record chosen decisions
with rationale and acceptance criteria before implementing their dependent slices.

## 11. Decision tracking

| ID | Item | State / source |
|---|---|---|
| REQ-01 | Both desktop and headless operation | Requested by user, 2026-10-11 |
| REQ-02 | DE and automatically tiling WM desktop modes | Requested by user, 2026-10-11 |
| REQ-03 | Proper Settings application | Requested by user, 2026-10-11 |
| REQ-04 | Future full Files rework | Requested by user, 2026-10-11 |
| REQ-05 | Update existing installs; package/network/mirror direction | Requested by user; implementation design still to discuss |
| REQ-06 | Desktop/apps outside the kernel in the future | User clarified Linux-like architectural direction; ABI/runtime open |
| REQ-07 | Desktop customization and usability | Required; customization scheduled with proper desktop work |
| REQ-08 | Dedicated home repository VM, own IP, LAN and existing VPN/DNS access | User confirmed topology; OS/address/storage choices at deployment |
| REQ-09 | Snapshot upgrades, trial boot and restore | Requested investigation; backend and semantics open |
| DES-01 | Shared compositor/apps, DE default, boot/login session selection | Proposed; live switching deferred |
| DES-02 | Serial-first headless | Initial x64 COM1 host implemented and QEMU no-GPU verified; local-console requirements open |
| DES-03 | Master-and-stack initial WM | Proposed; layout/shortcuts open |
| DES-04 | Offline package foundation before online distribution | Experimental P0 data builder/inspect/plan implemented; production trust/container gates open |
| DES-05 | Coordinated kernel/data releases before runtime apps | Proposed; priority and recovery layout open |
| DES-06 | Package trust/container and userland ABI/runtime | Open; userland split accepted, WASM-only direction not assumed |

Update this register after discussion with the choice and rationale. Record completed
implementation and its evidence in PROGRESS, then reconcile ARCHITECTURE and ROADMAP.
Do not mark a design accepted merely because its prototype builds.
