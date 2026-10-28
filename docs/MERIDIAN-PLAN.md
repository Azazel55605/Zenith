# Meridian — System Settings implementation plan

**Planned 2026-10-27.** User scope: every menu in the supplied outline must be
implemented; incomplete features must have a WIP message at the top of their page.
This document proposes implementation order, dependencies and acceptance. It does
not claim all underlying services exist. The [active implementation](components/SESSIONS-DESKTOP.md#native-app-migration) now supplies the first shell; this plan remains the owner for subsequent phases.

[Complete page/control inventory](MERIDIAN-PAGES.md) ·
[Original user outline](archive/2026-10-27/meridian-user-requirements.md) ·
[Current Appearance service](NATIVE-SETTINGS.md) · [Design language](DESIGN-LANGUAGE.md)

## Delivery approach

Build the **complete navigation and page structure first**, then activate real
features in small, tested slices. Do not wait for audio, Bluetooth or printer
support to make their menus visible. The outline defines **10 top-level categories
and 43 destination pages**; group headings are navigation, not duplicate pages.
The page inventory preserves all requested controls and identifies their service
owner and delivery phase.

Use **Meridian — System Settings** as the application name. Initially retain the
published `zenith-settings` package and `native run settings` entry point. Renaming
an installed package/executable is a separate compatibility migration, not a
prerequisite for displaying Meridian in the launcher/window. This plan changes no
runtime names, release tags or installed state.

The accepted neutral design language remains authoritative. KDE supplies behavior
and information-architecture references; this is a native Zenith application, with
no assumed Qt/KCM, KWin, PipeWire, NetworkManager, SDDM or systemd dependency.

## Baseline and gaps

The 0.0.25 source/Settings 0.1.0 release has a locally qualified Appearance page:
manual light/dark mode, six accents, signed semantic palettes, system-wide root-only
save/apply/reboot behavior, revision conflicts and unsaved-close protection.
Release tags are pushed; distributor delivery and installed acceptance remain
unconfirmed. [Evidence](archive/2026-10-27/appearance-settings.md).

Existing trusted services also cover selected keyboard layouts, timezone, default
session, accounts, primary-NIC IPv4 configuration, one-shot NTP and reboot/poweroff.
Those are useful foundations, **not completed Meridian pages or general native
management APIs**. Legacy native graphics has fixed surfaces up to 640x480; explicitly negotiated
viewport clients now resize up to 1920x1000 with generation-checked acknowledgement,
legacy forced-opaque pixels and an explicit foreground/material extension with
shared host policy. Meridian now has atomic transparency/blur preferences;
[desktop integration](archive/2026-10-28/desktop-material-integration.md) records
local qualification. It still has
bounded lifetime/request budgets, no display scaling/text-composition protocol and no
shared dynamic native font loading. UI text atlases currently cover ASCII.

Resolution and scaling are the first substantial new system-service priority.
Current display bootstrap obtains a canvas; it does not supply a Zenith-owned
mode enumeration/change/rollback contract. Driver support must be measured before
showing a mode as selectable. Audio, Bluetooth, printers, indexing, true screen
locking and suspend must remain WIP until their service/driver foundations exist.

## WIP and capability behavior

Every destination opens a real page with a title, description and its requested
sections/controls. Search and recent-page links can open WIP pages normally.

| Page condition | Top-of-page message and behavior |
|---|---|
| No feature backend yet | **WIP — This page is not functional yet.** Explain the missing service; show planned sections with disabled controls. No Apply success, dummy devices or invented readings. |
| Some features work | **WIP — Some settings on this page are still being implemented.** Identify working sections and missing ones; only working controls may save. |
| Implemented, no compatible device | **No supported device detected** with refresh/device information. Hardware absence is not itself WIP. |
| Implemented, this driver/VM lacks a capability | Explain the unsupported capability beside the control; retain the page. Do not report 60 Hz, brightness or a device name unless actually known. |
| Insufficient permission | **Administrator permission required** or read-only scope information. Do not label a completed feature WIP merely because the user cannot change it. |
| Service unavailable/error | Preserve drafts, show the failure and recovery action. This is an error state, not a successful save or generic WIP. |

These states may coexist: a missing device or permission does not remove a WIP
notice for genuinely unimplemented features.

Place the WIP banner below the page heading and above settings content, keep it
visible while scrolling, and include it in keyboard reading order. Each control
has capability and scope metadata. A fully WIP page cannot create a dirty draft or
write preference keys. Do not save unsupported values as if they will become active
later. Explicit visual previews are allowed only when clearly labelled and isolated
from actual system state. A page loses its WIP banner when its requested controls
work through real services, with legitimate hardware-dependent exceptions explained.

## Feature dependencies and master ownership

The [master roadmap](ROADMAP.md) is the only schedule/state tracker. Phase names here identify feature dependencies and preserve the page inventory references; they are not another project roadmap or promised dates. Implement each as a
reviewable package/service increment, with its own evidence. M1 gives every menu a
home immediately; later phases progressively remove WIP banners.

| Phase / master owner | Work and deliverable | Feature acceptance |
|---|---|---|
| **M0 — Current release acceptance / Q1** | Confirm distributor delivery and the kernel-first installed Appearance checklist. Keep planning and shell design moving in parallel. | Existing save/apply/reboot and CLI/GUI behavior confirmed on the installed VM before publishing a successor. |
| **M1 — Complete Meridian shell / U1, D1** | Register all 10 categories/43 pages, Quick Settings startup page, hierarchical navigation, breadcrumbs/back, settings search, direct page links and bounded recent-page history. Move the working Appearance controls into Colors. Build shared scrolling, text/number fields, lists, selectors, sliders and dialogs; virtualize long navigation so control limits are respected. | Every inventory page is reachable by keyboard/pointer/search, every unavailable feature has the required WIP state, no false save actions, page navigation protects drafts. |
| **M1 foundation gate / U1** | Add a general long-lived interactive-client profile, responsive/clipped layouts and bounded Unicode-preserving text input (initial glyph coverage first). Do not grant Meridian an app-name-based privilege/budget bypass. | Leave settings open idle and active well beyond existing short invocation limits; no unexplained disappearance, unbounded memory/request growth, or lost drafts on ordinary navigation. Keep watchdog/cancel isolation. |
| **M2 — Display first / D2** | Audit pinned drivers and expose primary-display identity/current mode/supported modes. Implement guarded resolution changes and integer UI scaling, coordinated font/layout/surface updates and legacy-client compatibility. Then orientation/refresh where actually supported. | Actual supported VM profile changes mode and scale, including a smaller viewport; Keep/Revert works, and client failure or timeout restores the previous known-good display. Unimplemented hardware controls remain WIP/unsupported. |
| **M3 — Shared preferences and existing services / S1** | Add user-versus-system scope, system-default inheritance and migration of existing root-only appearance defaults. Connect Meridian to typed keyboard-layout, timezone, About, primary-NIC and account-list services. Show actual device/runtime state and pending/restart effects. | Two accounts retain independent personal preferences; ordinary users cannot alter system defaults/accounts/network/clock authority; CLI and GUI agree. No page writes privileged files directly. |
| **M4 — Input and basic interaction / S2** | Device registry, mouse/touchpad policies, keyboard model/repeat/Num Lock and layout groups/switching, typing test, shortcut registry/conflict handling, initial Workspace behavior. | Multiple-device settings bind to stable identities; DE/Headless keymaps remain correct; shortcut capture cannot steal protected system input; changes survive relogin/reboot as appropriate. |
| **M5 — Appearance assets and compositor styling / S4, D3** | Wallpaper color/image, picker/catalogs, slideshow, dynamic pairs; custom accent picker; 2–3 widget styles, button variants, decoration styles, icons/cursors, animation controls and optional transparency/translucency/blur. | Changes reach the shared toolkit/compositor, preserve contrast/input, recover from missing assets, and update independently of kernel bytes where no new service is needed. Effects have an opaque/reduced-motion fallback. |
| **M6 — Time, day/night and locale/text / S3** | One shared day/night scheduler; manual times first, solar schedule/manual location next, opt-in automatic location later. Connect scheduled themes/wallpapers and Night Light; extend clock synchronization. Add locale formatting, translations, native font selection/rendering and font management. | Theme and Night Light follow one schedule through DST/time changes; manual fallback works without location/network; locale/font changes preserve text and do not render unusable UI. |
| **M7 — Desktop/workspace service contracts / W1, S5** | Notification/OSD policy, DE focus/titlebar/snap behavior, task switcher, virtual desktops and shared runner providers. Make file-click/drag/drop/clipboard behavior effective in newly ported apps. | Owner/session isolation holds; DE and future WM expose truthful capabilities; Files/Editor use shared policies rather than hardcoded copies. Basic settings search already works from M1. |
| **M8 — Device, media and discovery services / H2, N2, S5** | Safe removable-device automount, bounded per-user file indexing, PCM playback/recording, device format tests and level meters, live stream inventory and system sound packs. Add advanced connection profiles/priority/negotiation as network support grows. | Real supported devices and service-backed values; bounded disk/CPU/buffer use, permission-aware search/capture, mount failure recovery, no automatic execution from removable media. |
| **M9 — Secure session and lifecycle / W3** | True screen lock, idle/grace policy, administrative auto-login, users add/delete/password flows, autostart, shutdown/reboot/logout confirmations and session restore. Add software idle dimming; implement suspend only after real resume support. | Lock preserves apps while blocking input/content leakage; authentication stays trusted; unsafe account deletion is refused; restart/autostart loops and unsaved work are handled explicitly. |
| **M10 — Explicit later capabilities / H3** | Specialized Accessibility features, Bluetooth, printers and physical-hardware capabilities not already proven. Advanced per-app audio routing follows the basic mixer/stream service. | Separate small backend/driver specs and real device acceptance. Pages remain visible/WIP until then; a placeholder or VM-only result does not establish hardware support. |

### Current foundation and next contracts

The local shell already registers all 43 pages, search, group navigation and recent
links; Colors retains working persistence and About has selected real metadata.
This does not finish M1: shared form/text/control richness, responsive scale/layout
and long-duration lifetime acceptance remain U1/D1/D2. Every backend beyond those
must retain WIP until its own service and native acceptance exists.

The master current-focus section orders design parity, shared/display foundations
and the remaining Editor/Terminal/Sunrise ports. Files is explicitly excluded from
that migration batch; its later makeover is A1. App ports consume these shared
contracts rather than waiting for all 43 Settings backends or copying their own style.

## Service and state architecture

Each page declares stable ID, category, keywords, scope, required capabilities,
current values, draft validation, preview policy and apply/restart semantics. Use a
compiled/declarative page registry first; do not introduce executable third-party
settings plugins as a shortcut. Keep rendering/forms in userland and privileged
operations in narrowly typed, versioned services. Never route a native page through
an arbitrary root shell command or give it raw framebuffer/input/device addresses.

Personalization defaults to **user scope** once that foundation exists. Network,
accounts, global clock, boot/login policy and system device policy require explicit
system authority. The present root-only Appearance service remains clearly labelled
until migration; do not silently convert its global file into a per-user setting.
User identity/home paths come from the trusted account/session service, not client
supplied `USER`, `HOME` or arbitrary configuration paths. Future elevation requires
a trusted host prompt; do not collect administrator credentials in an ordinary page.

Use per-domain revision checks and explicit Apply/Save/Discard behavior. A save
failure or stale revision retains edits. Navigating away, closing, changing search
result or switching scope asks before discarding. Show effective values separately
from saved intent and classify changes as live, next login, app restart, service
restart or reboot. Do not imply that a successful file write means hardware applied.
Cross-service changes need an explicit transaction/recovery policy, not a pretend
whole-system atomic Save. Loading/device enumeration runs asynchronously and remains
cancellable; preserve a responsive UI when a driver or service stalls.

The page registry, search and WIP scaffolding can ship in the Meridian package
without a new kernel when existing contracts suffice. New services require a
compatible kernel/service version first. Theme/icon/cursor/wallpaper/font/sound packs
are data packages with manifests, bounded decoding, ownership checks and recovery
assets; importing an image/font must not create an unsigned privileged code plugin.
A package rename, ABI schema, on-disk preference layout or extension format is a
separate design decision, not frozen by this plan.

## High-priority feature contracts

**Display:** discover actual modes and capabilities. Use a trusted, independent
confirmation timeout (proposed 15 seconds), restore on timeout/client crash, and
persist only an explicitly kept configuration. A crashed settings client cannot
own the only revert timer. Keep headless/recovery access and a known-good boot
configuration. Resolution is not UI scaling: font metrics, hit testing, cursor,
layout and native surfaces must scale coherently. Define resize/generation events
and old-client behavior before growing the fixed-surface ABI. Fractional scaling
and multiple-monitor layouts follow proven primary-display/integer scaling.
Brightness/backlight, refresh and tearing controls require actual driver support;
software dimming must be named separately from hardware brightness.

**Day/night:** one owner supplies theme, dynamic wallpaper and Night Light timing.
Manual schedules work first. Solar calculations use timezone/date/location with
polar-day/night and clock-change fallbacks. Automatic location is opt-in, with
manual coordinates and no-network fallback. Night Light is a global compositor/
display color transform, not merely tinting Meridian's own pixels. Strength is the
blend amount; day/night temperatures are targets. Scope and display support are
explicit. No second independently persisted schedule on the Quick Settings page.

**Locking and power:** the current return-to-login flow ends a session; it is not a
screen lock. A real lock retains apps, prevents client input/screen leakage and
uses trusted authentication. Auto-login is a separate administrator policy, not
password-grace time. Suspend requires actual device/clock/process resume support;
reboot, poweroff, display blanking and CPU halt are not substitutes.

**Recovery and identity:** network edits need a tested recovery route to the last
working configuration; account deletion cannot remove the current active identity
or silently erase a home. Autostart/session restore launch authorized app identities
with bounded arguments, not arbitrary privileged shell strings. Confirmation
policy applies to interactive session actions; headless/noninteractive CLI operations
need explicit semantics and must not unexpectedly wait for a graphical dialog. Failed startup
entries can be disabled without making login unusable; do not restore credentials.

**Basic accessibility is immediate:** readable contrast, visible focus, keyboard
navigation, labelled controls, scroll access and reduced-effects behavior are M1
requirements. The later Accessibility page does not defer these fundamentals.

## Acceptance and tracking

M1 acceptance must enumerate the registry and compare it with all 43 inventory IDs;
open every page, test search/back/recent links, top-of-page WIP semantics and draft
navigation with keyboard and pointer. Check dark/light, all accents, small supported
viewports and long labels. Verify WIP controls cannot write or claim success.

Each activated feature needs service/host regressions, actual native UI interaction,
permission/stale/error tests and installed/reboot evidence when persistence or
hardware is involved. Display modes require failure/revert tests; locks require
negative input/content tests; audio/removable devices require real supported devices.
Keep local, distributor and operator evidence separate. Never count a menu stub as
a working backend. Mandatory kernel acceptance gates remain intact for API releases.

[The master roadmap](ROADMAP.md) owns all work state, priorities and acceptance. This guide owns feature contracts/dependencies; the inventory owns detailed control coverage. Component pages expand master IDs. Archive detailed execution evidence once as increments complete.

## KDE references and interpretation decisions

Checked on 2026-10-27. The user outline controls Meridian's category order; KDE
handbooks can lag the current UI, so they clarify concepts rather than dictate an
exact Plasma version/layout.

- [System Settings modules](https://docs.kde.org/trunk_kf6/en/systemsettings/systemsettings/general.html): reference for grouped modules, navigation/search and distinct appearance/session components.
- [Region & Language](https://docs.kde.org/stable_kf6/en/plasma-workspace/kcontrol/region_language/): reference for language preferences, independent format overrides and previews.
- [KDE region-language model](https://github.com/KDE/plasma-workspace/blob/master/kcms/region_language/optionsmodel.cpp) and [its page source](https://github.com/KDE/plasma-workspace/blob/master/kcms/region_language/ui/main.qml): confirm **Data & Storage Sizes** means size-unit formatting, such as decimal MB versus binary MiB. It is not storage partitioning or distributor cleanup.
- [Window Behavior](https://userbase.kde.org/System_Settings/Window_Behavior): reference for focus, raising, titlebar actions and movement as separate policies. Floating-DE controls must not pretend to configure a not-yet-implemented tiling WM.

Additional interpretation: “same as mouse” means the requested mouse options also
appear on Touchpad, gated by that device's real capabilities. Focus delay applies to a supported pointer-focus policy, not to delaying an
explicit click; raising is a separate policy. Application/widget
style, window decorations, login styling, post-login splash and boot splash remain
separate resources/lifecycle phases. Screen tearing means presentation policy,
subject to driver support. Day/night scheduling is shared even though its controls
appear through multiple pages. Optional per-app audio routing remains after basic
audio, and Accessibility/Bluetooth/Printers retain their explicitly later priority.
