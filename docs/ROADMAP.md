# Zenith — master roadmap

**Single work tracker · audited 2026-10-27 · source baseline `a558f66`.**
Start here for priorities, current state, outstanding work and release acceptance.
Component pages expand these work IDs; technical guides explain contracts, not a
second schedule. [Documentation map](README.md) · [Maintenance rules](MAINTAINING.md).

## Product definition

A usable, independently updateable desktop OS: authenticated Desktop and local
Headless sessions with serial recovery; a full floating DE and later selectable
automatic-tiling WM; dependable Terminal, Editor, Files and Sunrise installation;
Meridian settings with consistent CLI services; signed packages and recoverable
updates. Applications must update and fail independently of the kernel. No Linux
binary compatibility, POSIX completeness or particular filesystem is implied.

The accepted neutral light/dark design is the target, including optional transparent,
translucent and blurred surfaces. **Opaque mode is the same design with effects off.**
[Visual specification and parity gaps](DESIGN-LANGUAGE.md).

**A finished core product** requires the core rows below to pass their exits,
usable install → login → configure → edit/save → manage files → update → recover
workflows, and a published supported-device matrix. A growing menu/app count is not
completion. Later hardware capabilities and developer ports have explicit separate
exits; optional experiments do not block the core product.

## State and evidence

States: **accepted** = operator exercised that scope; **local** = implemented and
locally tested; **partial** = some behavior exists; **planned** = implementation
missing; **verification** = evidence needed, not an assumed reproducible defect.
A row's implementation state does not imply publication or installed acceptance.

| Baseline | Established | Boundary |
|---|---|---|
| Boot/storage/accounts/packages | Installed ext2, permissions, authentication, signed named updates, retained kernels and distributor/dashboard | 512 MiB root; no general journal/repair, complete system snapshot or hardware certification |
| CLI extraction | Base/filesystem/text/network tools, owned streams/jobs/raw leases; operator exercises through 0.0.23 | Shell evaluation and privileged administration remain trusted; no full PTY/POSIX runtime |
| Graphical ABI / 0.0.24 | Qualified private surfaces/events and independent probe; operator installed/tested | Trusted compositor/greeter; fixed opaque client surfaces |
| Appearance / 0.0.25 | Tags at qualified `12ded91` pushed: Settings 0.1.0, theme 0.1.1, manuals 0.1.6 | Last operator report: building. Distributor completion and installed acceptance unconfirmed (**Q1**) |
| Meridian/materials / 0.0.26 | Full clean Release qualification at `1487c93`: 765 host/239 controller, all ten native gates and light materials passed. Release set: kernel 0.0.26, Settings 0.2.0, desktop-tools 0.1.0. [Evidence](archive/2026-10-28/desktop-material-integration.md#release-qualification) | Operator installed 0.0.26; reported rapid wheel/move freezes remain under investigation (**Q2/U1**). Most Meridian pages remain WIP until their backends exist |
| Shared settings / 0.0.27 | Clean Release qualified at `0d86635`: 782 host/240 controller, all ten native gates. Release set: kernel 0.0.27, Settings 0.3.0, desktop-tools 0.2.0. [Evidence](archive/2026-10-28/shared-settings-stage.md#clean-release-qualification) | Distributor exposed input/handoff failures. Public-prompt controller fixes now pass hidden cancellation and shared settings there. A later mouse failure is reproduced as a stranded virtio queue on QEMU 10.0.13. [Kernel recovery candidate](archive/2026-10-28/virtio-mouse-drain.md) is clean-source qualified at `5306703`: 322 controller/782 host/eight adapter tests and all ten native gates; the same ELF passes 20 restricted-CPU QEMU 10 cycles. Successor publication and installed acceptance pending (**Q2/H1**). 0.0.27 remains immutable; driver-dependent pages remain WIP |

Evidence: [CLI](archive/2026-10-26/cli-process-foundation.md),
[text](archive/2026-10-26/native-text-traversal.md),
[network](archive/2026-10-26/native-network-clients.md),
[graphics](archive/2026-10-26/protected-graphical-clients.md),
[Appearance](archive/2026-10-27/appearance-settings.md),
[Meridian foundation](archive/2026-10-27/meridian-shell.md).
Old 0.0.13/0.0.19 build failures are historical investigations, not current retry tasks.

## Current focus and execution order

**Operator follow-up for installed 0.0.26:** rapid wheel navigation and movement
can freeze the VM (no panic reported). Root cause found 2026-10-29: device IRQs dropped during native user windows; see H1. U1/D3
own reproduction, bounded redraw/input handling and continued host responsiveness.
S1/D1 own persistent hierarchical navigation and inset sidebar/detail containers;
U1/D2 own the explicit native resize/maximize contract. Files remains excluded.
These corrections and the first typed Settings slice are being validated together. The untagged local follow-up adds
persistent inset navigation, owned resize/maximize and a system-wide whole-app
background preference. The visual follow-up removes stacked rectangular underlays,
unifies the title/sidebar treatment and adds bounded reduced-grid whole-window blur
([visual evidence](archive/2026-10-28/meridian-material-simplification.md)).
Targeted native tests pass; the operator freeze is not yet
proven resolved. [Evidence and limits](archive/2026-10-28/meridian-workspace-input.md).

1. **G1 / documentation — audited:** tracking is consolidated here; stale blockers
   are reconciled and accepted requirements preserved. Keep this ownership model.
2. **D1 / shared design — local follow-up:** smooth native shape coverage and a
   completed-window corner mask fix the reported edge/rectangle bleed. Terminal,
   Editor and Sunrise now use the design tokens/shared host controls; Files stays
   excluded. [Evidence](archive/2026-10-28/smooth-edges-and-system-apps.md).
3. **D3 / advanced rendering — software integration locally qualified:** saved
   transparency/blur controls, host chrome/taskbar, Meridian material regions,
   contrast and bounded resource/work fallback now share one policy. See
   [desktop integration](archive/2026-10-28/desktop-material-integration.md).
   Q2 owns installed acceptance; sustained device performance and D2 resize/scale
   qualification remain. Protected authentication and document planes stay solid.
4. **S1 / shared Meridian settings — Release qualified:** Personal appearance profiles
   inherit system defaults until overridden; Colors exposes both scopes and an
   explicit reset. Keyboard/time-zone/session defaults share revisioned root-only
   services with CLI; searchable selectors use real catalogues. Network and account
   pages show authorized read-only service data. Q2 owns installed acceptance.
   [Scope and evidence](archive/2026-10-28/shared-settings-stage.md). Unsupported
   edits/drivers retain WIP banners; richer controls and display modes belong to
   U1/D2 and the remaining S2–S5 stages.
5. **U2–U4 / finish native app migration:** Editor, Terminal and Sunrise over typed
   service contracts, using the shared presentation already applied to their trusted
   windows. Welcome/System have local native frontends. **Files is excluded** and
   gets its own later overhaul, A1.
6. **Q2 / release acceptance:** qualify coherent batches, publish compatible
   kernels first, then clients; record actual installed observations. Q1 confirmation
   can happen in parallel; do not mutate existing tags.
7. **W1–W3 / desktop and workflows:** full DE/lifecycle, notifications/workspaces,
   automatic tiling, Files overhaul and shared app behavior. Activate Meridian pages
   alongside their services rather than waiting for every backend to exist.
8. **PK1–PK4, R1–R3, H1–H3 / product hardening:** package/config/trust, storage and
   whole-system recovery, supported hardware, secure sessions and real media/devices.
   Backend dependencies below determine the detailed order, not calendar promises.
9. **C1–C4 and X1–X4 / larger tracks:** managed multicore and developer ports follow
   their safety/resource prerequisites. They are not prerequisites for the next UI app.

## Core delivery work

Each row is a deliverable with a concrete exit. Update this table when its state
changes; put smaller implementation steps in the linked component, using the same ID.

| ID | State | Work, dependency and exit |
|---|---|---|
| G1 | local, complete | One tracker; obsolete guides consolidated and open items mapped. [Audit and checks](archive/2026-10-27/documentation-and-material-contract.md) complete; future agents follow [maintenance](MAINTAINING.md). |
| D1 | local; installed verification pending | [Shared visual parity](DESIGN-LANGUAGE.md): neutral segments, antialiased swatches/switches, label/help rows, restrained navigation and completed-window rounded masks implemented. Remaining system windows share the design; [quality follow-up](archive/2026-10-28/smooth-edges-and-system-apps.md). Both modes/six accents and draft/permission behavior locally qualified; [evidence](archive/2026-10-28/shared-widget-parity.md). Q2 owns installed confirmation; U1/D2 own richer widgets/hover transport, Unicode and scale; D3 owns effects. |
| D2 | partial | Bounded native client resize/maximize implemented and locally exercised; display mode/scaling remains separate. Display identity/mode enumeration → guarded mode change → integer layout/text/surface scaling → driver-supported orientation/refresh. Requires richer client layout/resize contract. Smaller viewport, timeout/crash and explicit Keep/Revert must restore a usable mode. |
| D3 | software implementation local; device acceptance pending | System-wide whole-app materials for native/trusted app bodies, a contrast-clamped translucency level, compatible preferences and adaptive tint/blur caches locally tested. Explicit format-2 foreground/regions, cached tint/box diffusion, atomic saved/effective/available preferences, Meridian controls/sidebar and host chrome/taskbar integrated. Shared contrast, 8 MiB cache and per-frame work budgets fall back without changing preferences/layout. [Contract](GRAPHICAL-CLIENTS.md#opt-in-material-frames-d3-first-slice), [local evidence](archive/2026-10-28/desktop-material-integration.md). Q2: installed on/off/light/dark/overlap acceptance and sustained performance; D2: resize/scale qualification. Hardware/GPU acceleration is not implemented. Protected credentials remain solid. |
| U1 | partial | Owned viewport generation/acknowledgement, pane-aware saturated wheel handling and drag keyboard routing locally tested; operator freeze root cause found under H1. [Shared native UI/runtime](components/USERLAND.md): lists, fields, selectors, sliders, dialogs, multiline text, clipboard, resizing/hover, runtime fonts/Unicode/IME; long-duration quota and teardown acceptance beyond old invocation limits. Existing profile renewal alone is not full long-lived-app qualification. |
| U2 | native migration planned | Shared-design Editor is locally implemented; native extraction still needs: bounded UTF-8/LF/CRLF, selection/clipboard, undo/redo/search, safe staged save/save-as and external-change checks. Depends on U1 + caller-scoped files/dialogs; prove permission/out-of-space/cancel/dirty-close and independent update. |
| U3 | native migration planned | Shared-design Terminal/appearance-aware scrollback is locally implemented; extraction still needs: owned PTY/shell session, byte streams, modes/resize, raw/canonical input, hidden secrets, type-ahead, jobs/EOF/cancel. Depends on U1; preserve all three recovery hosts. No privileged command-string RPC as a substitute. |
| U4 | native migration planned | Shared-design Sunrise frontend is locally implemented; extraction still needs: native wizard over typed root/live-only disk/account/progress service. Preserve pre-erase asset/target checks, explicit destructive consent, secret handling, maintenance exclusion and teardown. Validate disposable install/reboot/recovery before replacing recovery UI. |
| U5 | partial | Welcome/System: native frontends and signed shell/menu launch locally pass; finish D1 parity, live preferences and independent upgrade/cancel/other-user acceptance with Q2. Keep recovery start windows and fail-closed executable validation. |
| U6 | planned | Separate service/compositor/session processes: owned/versioned IPC, authority and restart/compatibility contracts → fault isolation → independent restart/upgrade. Existing typed syscalls are a foundation, not completion. Keep drivers/GC/BSP limits explicit. |
| S1 | partial | Shared-settings slice Release qualified at `0d86635`: per-UID appearance/inheritance, searchable keyboard/time-zone/session catalogues, root-only system saves and authorized read-only network/account summaries. [Meridian shared services](MERIDIAN-PLAN.md): per-user overrides/system defaults, CLI parity, revision conflicts, typed keyboard/timezone/network/account summaries and actual About metadata. Two-account/reboot/error tests; never grant authority via environment or a page name. |
| W1 | partial | [Full DE](components/SESSIONS-DESKTOP.md): usable keyboard-searchable launcher/panel, consistent dialogs, window identity/focus/move/snap, notifications/OSD, task switcher and virtual desktops. Requires U1/U6; prove app faults and busy/unsaved/session teardown. |
| W2 | planned | Selectable automatic-tiling WM sharing apps/services with DE: layout/focus/keyboard policy, floating exceptions, monitor/workspace handling and recovery. Requires W1 service boundaries; does not replace the DE choice. |
| W3 | planned | Secure lock/idle/grace, guarded auto-login, autostart, restore and confirmation policies. Requires session/app lifecycle; lock retains applications and blocks content/input leakage, never masquerades as logout. Password/elevation remains trusted. |
| A1 | planned, later | [Files overhaul](APPLICATIONS.md#files-and-graphical-editor-requirements): places/navigation, sort/metadata, multiselect, transfer jobs/cancel/conflicts/permissions/links, then tabs/associations/trash/search/archives. Requires shared dialogs/operations; excluded from current port batch. |
| A2 | planned, later | Extend Editor after U2: tabs/documents, syntax support, optional private crash recovery, project tools only after process/SDK support. Basic dependable editing comes first. |
| PK1 | planned | [Configuration and activation](components/PACKAGES.md): `/etc` merge/conflict policy, shared defaults, restart notices and compatibility preflight across active apps/services/kernel. Retain local changes and recover interrupted operations. |
| PK2 | planned | Signing-key rotation/revocation/roles, repository and transport certificate renewal procedures, compromise/rollback/expiry tests and independent security review before broader exposure. Private working HTTPS is not a complete public trust lifecycle. |
| PK3 | verification | Distributor backup **restore** drill, deployment recovery and useful failure/phase/storage diagnostics. Build retries, version grouping, reporting/automatic cleanup already exist; improve only from measured gaps. Never clean active/pinned evidence. |
| PK4 | partial | Combined update/recovery compatibility: kernel-first capability handling, schema/rollback rules and truthful partial-commit state. Requires PK1 + R2; current `pkg update` coordinates separate commits, not an atomic snapshot. |
| R1 | planned | [Storage capacity/durability](components/STORAGE-SECURITY.md): measure limits → coordinate formatter/checker/inode/memory/installer expansion beyond 512 MiB; decide repair/journal or generation strategy. Keep traversal/symlink/held-handle authorization regressions. |
| R2 | planned | [Matched system recovery](UPDATE-RECOVERY.md): define kernel/root/package/config/account/service/home scope → select/budget backend → retain consistent generations → trial/restore/boot identity and retention. `/home` and mutable schemas need explicit policy; no filesystem choice is assumed. |
| R3 | planned | Recovery failure matrix: short writes/out-of-space/power cuts, trial without modifying preserved state, interrupted activation/restore, damaged root/ESP and external recovery/backup. Requires R1/R2; kernel retention alone does not meet this exit. |
| N1 | verification | [Sustained networking](components/NETWORKING.md): repeatable VMware/proxy/DNS/TCP/TLS transfer/cancel/replacement benchmarks and actionable counters. Port-allocation mitigation is installed; stale-flow location remains unproved. No current reproduction is assumed from old traces. |
| N2 | planned, later | Beyond primary wired IPv4 clients: socket/listener/UDP/server contracts, connection/device profiles and additional NICs; Wi-Fi/IPv6 only after explicit driver/protocol scopes. Reconnect/link loss/authz tests per supported profile. |
| H1 | partial; input candidates local | [Supported device matrix](components/BOOT-RECOVERY.md): publish actual QEMU/VMware boot/storage/display/input/network support and repeatable recovery scenarios; verify physical/USB install separately. Operator VMware all-input freeze reproduced with PS/2 under KVM; root cause is the native user window acknowledging device IRQs without their drivers. [Forwarding candidate](archive/2026-10-29/native-device-irq-forwarding.md) passes the reproducer and the Settings, graphics and process gates locally; clean-source qualification, release and installed VMware acceptance pending (**Q2**). [Stranded virtio input](archive/2026-10-28/virtio-mouse-drain.md) reproduced; bounded IRQ-scoped periodic drain is clean-source qualified at `5306703`, with all ten native gates and the exact QEMU 10 restricted-CPU reproducer passing; distributor/installed acceptance remains with Q2. PS/2 and installed wheel/drag anomalies remain unverified; absolute pointer/hardware cursor/damage presentation need driver work. |
| H2 | planned | Audio playback/capture/mixer/device inventory and removable-device broker, then meters/stream routing and safe automount. Requires bounded buffers, stable identities and permissions; tests need real supported devices. |
| H3 | planned, later | True suspend/resume, Bluetooth, printers and specialized accessibility. Define each scope/driver dependency first; WIP pages stay visible. Never count a menu or VM-only demo as device support. |
| Q1 | verification | Confirm 0.0.25 distributor result and [Appearance installed procedure](testing/SETTINGS-VM-TEST.md); retain current release immutability. |
| Q2 | focused launcher repair confirmed; full distributor retry pending | The operator's focused Settings and repaired materials gates passed; the status parser fix was verified against the failed server log. The subsequent parallel run fails at start-menu client retirement, matching the retained local failure: its weak shared-colour predicate accepted Terminal screenshots before the native app appeared. [Strict native-frame witness and no-build launcher replay](archive/2026-10-29/launcher-frame-readiness.md) locally pass 40 half/quarter-CPU launches, all 347 controller regressions and the full unchanged Settings desktop sequence. The operator confirmed the five-cycle launcher probe passes on the distributor (2026-10-29). Preserve 0.0.28 and retry the normal parallel release pipeline with the current controller. Earlier VM-registry selection and installed wheel/drag acceptance remain open; do not infer release acceptance from standalone successes. |
| Q3 | continuous; parallel controller local | [Parallel native validation and exact retry receipts](archive/2026-10-28/parallel-native-validation.md) locally implemented; four actual gates pass concurrently. Default up to four, configurable up to eight within CPU/RAM budget; distributor timing pending.  Measure boot/app responsiveness, idle memory/CPU, transfer/build phase times and stability across repeated workflows. Keep CI/controller/toolchain drift checks, fault injection and installed evidence distinct; enforce supported-profile performance budgets before claiming product readiness. |

## Meridian — every requested destination

All **10 categories / 43 pages** exist as local navigation. Colors/material preferences, keyboard layout, time zone and selected About data
currently have backends. The table includes every
page so an apparently finished menu cannot hide unfinished product work.
[Detailed controls and scope](MERIDIAN-PAGES.md) · [Service contracts/phase details](MERIDIAN-PLAN.md).
The M0–M10 names in that guide are Meridian phases, not separate project milestones.

| Category / page IDs | Backend state | Remaining work / master owner |
|---|---|---|
| Quick Settings — `quick.overview` | partial | Manual theme links exist; quick controls, persistent history and shared automatic schedule (**S1**, **S3**) |
| Mouse & Touchpad — `input.mouse`, `input.touchpad` | WIP | Stable device selection, primary buttons, pointer/scroll speed, acceleration/inversion (**S2**) |
| Keyboard — `input.keyboard`, `input.shortcuts` | partial | Typed layout selector, save/discard and CLI parity implemented for system defaults. Model/repeat/Num Lock/test, layout switching and conflict-safe shortcut capture (**S1**, **S2**) |
| Sound — `audio.playback`, `audio.recording`, `audio.streams` | WIP | Real devices, formats/test, permissioned levels, stream inventory then advanced routing (**H2**) |
| Display — `display.configuration`, `display.night-light` | WIP | Resolution/scaling/mode rollback first; supported orientation/refresh/brightness/tearing, then global temperature transform/schedule (**D2**, **S3**) |
| Accessibility — `input.accessibility` | WIP | Baseline keyboard/contrast/text in U1; specialized features need scoped implementation (**H3**) |
| Devices — `devices.bluetooth`, `devices.external`, `devices.printers` | WIP | Bluetooth/printer services later; stable safe automount policies and actual states (**H2**, **H3**) |
| Networking — `network.connections` | partial | Actual primary NIC/IP/DNS information works; typed wired configuration/autoconnect next; multiple profiles/priorities/link negotiation as supported (**S1**, **N2**) |
| Wallpaper — `style.wallpaper` | WIP | Color/image/slideshow picker, categories and scheduled light/dark pairs (**S4**, **S3**) |
| Colors — `style.colors` | partial | Personal/system modes, six accents, materials/level and inherited defaults work; arbitrary-color picker and automatic switching (**S1**, **S4**, **S3**) |
| Style — `style.application`, `style.decorations`, `style.icons`, `style.cursors` | WIP | Shared widget/button and host decoration variants, icon/cursor packs; optional materials without layout redesign (**D1**, **D3**, **S4**) |
| Sounds/splashes — `style.sounds`, `style.splash`, `style.login`, `style.boot-splash` | WIP | Sound events/packs after audio; authenticated transition/login/boot styles preserve diagnostics and trusted auth (**H2**, **S4**, **W3**) |
| Text — `style.fonts`, `style.font-management` | WIP | Shared native font loading/rendering/AA/hinting; All/Personal/System catalog/import (**U1**, **S3**) |
| Animations — `style.animations` | WIP | Global speed and per-operation controls only for implemented effects; reduced motion (**S4**, **W1**) |
| Apps/windows — `windows.notifications`, `windows.behavior`, `windows.switcher`, `windows.desktops` | WIP | DND/popups; focus/titlebar/snap; switcher sorting/filters; desktop names/order (**W1**) |
| Workspace — `workspace.behavior`, `workspace.search` | WIP | Shared click/scroll/paste/drag policies, OSD and runner providers; bounded authorized file indexing separate from working settings search (**S2**, **W1**, **S5**) |
| Security — `security.locking` | WIP | Actual lock/idle/grace with administrative auto-login policy (**W3**) |
| Locale/time — `locale.region`, `locale.time`, `locale.day-night` | partial | Typed system-default time-zone selector implemented. Remaining: Per-field language/number/currency/address/phone/measurement/SI-IEC formats; clock/timezone/sync policy and one day/night schedule (**S1**, **S3**) |
| About — `system.about` | partial | Runtime/boot/display/clock metadata works; real running/installed versions and hardware inventory (**U5**, **S1**, **H1**) |
| System — `system.power`, `system.users`, `system.autostart`, `system.session` | partial | Account information and registered default-session selector work; remaining: safe dim/suspend, guarded account lifecycle/password flows, autostart, confirmation/session restore (**W3**, **H3**) |

| ID | State | Shared settings work and exit |
|---|---|---|
| S2 | planned | Input/device registry and shared workspace policies; device-specific support, protected shortcuts, relogin/reboot and DE/local keyboard tests. Builds on S1/U1. |
| S3 | planned | One day/night/time/location service for theme/wallpaper/Night Light; manual fallback and opt-in location. Then locale, translation and font management. DST/time changes, malformed fonts and two-user tests; clock authority stays separate from personal formatting. |
| S4 | planned | Wallpaper/assets/style presets, custom accent picker, cursor/icons and reduced-motion effects. Validated resources + shared toolkit/host consumption + opaque fallback. D3 owns composition, not individual apps. |
| S5 | planned | Shared runner providers then bounded per-user file indexing: include/exclude rules, real status, capacity and permission-aware results; reuse in launcher and Files. No indexing merely to search settings pages. |

## Larger engineering tracks

| ID | State | Dependency-ordered work and exit |
|---|---|---|
| C1 | partial | Native integer AP workers exist opt-in. Add cancellable queues, safe wake/IPI, per-CPU interrupt stacks and shutdown quiescence instead of idle polling. |
| C2 | planned | Per-CPU scheduler/current context/GDT/TSS/FP/timer and lock audit after C1. No managed AP execution yet. |
| C3 | planned | Cross-CPU allocation, GC stop/root barriers, then pinned managed threads; require pressure/fault/cancel evidence before migration/load balancing. |
| C4 | planned | Migration, credential/service concurrency, real I/O contention and recovery at 1/2/4+ CPUs; only then consider default managed SMP. [Detailed contract](MULTICORE.md). |
| X1 | partial | Host cross-SDK exists in freestanding form; finish documented startup/headers/libc subset/compiler runtime and supported-API matrix; install/upgrade a real independent program. |
| X2 | planned, later | PTY/terminal capabilities + libc source audit → nano/reduced Vim proof; optional Bash requires subprocess/pipe/signal/POSIX compatibility beyond the existing custom shell. |
| X3 | planned, later | Deliberately scoped Python REPL/import/file I/O first, then modules backed by implemented APIs. No implied PyPI/subprocess/thread support. |
| X4 | planned, later | On-Zenith GCC/binutils/make/runtime after process/SDK and storage budgets; compile/run a small program before self-hosting claims. [Developer-port requirements](DEVELOPER-TOOLS.md). |

## Scope decisions and non-blocking ideas

Optional Doom remains outside the product's main delivery sequence. Orbit/package
browser, Observatory/resource monitor, Beacon/network panel, archive app, screenshots,
SSH and diagnostic export are proposed additional applications, not approved core
release blockers. Keep them in [application requirements](APPLICATIONS.md), and add
an explicit work row only when promoted. Btrfs is a possible recovery backend, not
an accepted prerequisite; WASM is an optional runtime investigation, not the desktop.

## Completion and update rules

A master row is accepted only after its stated exit and the relevant environment
checks. Attach exact evidence; separate source, local qualification, publication
and operator acceptance. Split a large row into component steps without inventing
another roadmap. Reopen a resolved defect only with new evidence. Every newly
accepted requirement must map to an ID here. No unchecked task may live solely in
a technical guide, chat, archive or source comment without a master owner.
