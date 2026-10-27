# Meridian page inventory

[Ordered plan and WIP rules](MERIDIAN-PLAN.md) ·
[Original outline](archive/2026-10-27/meridian-user-requirements.md)

**Reviewed scope, 2026-10-27.** All destinations are now registered in the unshipped Meridian shell; most feature backends remain WIP. [Implementation boundaries](NATIVE-APP-MIGRATION.md). Every destination
below must be present from M1, including later-phase features. Group names become
expandable navigation headings; nested option groups/tabs remain available inside
their page. Page IDs are proposed stable navigation/search identities, not an ABI.

Baseline describes verified source foundations, not completed page implementations
or installed acceptance: **A** = the current Appearance client has working controls;
**C** = a related trusted CLI/core service exists; **H** = selected host behavior
exists but lacks a general native settings contract; **—** = no completed foundation
claimed. Any incomplete requested section keeps its page's top WIP banner.

Scope goals: **U** personal/session preferences; **S** administrator/system policy;
**U/S** separate personal choice and administrator default/device authority. These
are targets: current Appearance writes remain system-wide/root-only until M3.
All phase references refer to [M0–M10](MERIDIAN-PLAN.md#order-of-work).

## 1. Quick Settings

| ID / page | Requested sections and controls | Baseline; activation / owner |
|---|---|---|
| `quick.overview` — Quick Settings | Startup overview; manual quick theme toggle and automatic time-based mode; links to Wallpaper, Colors/Themes and other settings; recently visited pages/options. Show real summaries, not placeholder device readings. | A partial; manual links/control M1, user history M1/M3, automatic mode M6. U; settings navigation + shared day/night service. |

## 2. Input & Output

Mouse & Touchpad, Keyboard, Sound, Display & Monitor are groups beneath this category.

| ID / page | Requested sections and controls | Baseline; activation / owner |
|---|---|---|
| `input.mouse` — Mouse | Device selector for multiple mice; left-handed buttons; pointer speed; acceleration; scroll speed; invert scroll. Independent per-device capability/state. | H; M4. U/S; input/device service. |
| `input.touchpad` — Touchpad | Touchpad device selector and the same requested handedness, pointer speed/acceleration, scroll speed/inversion controls, with explanations for controls that device cannot support. Do not invent a touchpad from mouse presence. | —; M4 as drivers allow. U/S; input/device service. |
| `input.keyboard` — Keyboard | Model selection; startup Num Lock; repeat enable/delay/rate; typing test; layout selector, multiple-layout list/order and layout-switch mechanism. Keep layout separate from language/locale. | C for single saved layout, H for input; M3 single layout, M4 remainder. U/S; keyboard/seat service. |
| `input.shortcuts` — Shortcuts | System actions and system-app actions; inspect/change/reset bindings, capture chords and resolve conflicts. Preserve trusted recovery/authentication shortcuts. | H for fixed shortcuts; M4. U/S; compositor shortcut registry + app action IDs. |
| `audio.playback` — Playback Devices | Enumerate/select playback devices; per-device test; channel configuration; sample rate and bit depth when negotiable. Report supported/negotiated formats rather than accepting arbitrary values. | —; M8. U/S; audio service + driver. |
| `audio.recording` — Recording Devices | Enumerate/select recording devices; show actual per-channel pickup/level meters with capture permission/activity state. Never generate fake meter activity. | —; M8. U/S; authorized audio capture/meter service. |
| `audio.streams` — Playback Streams | List apps actually outputting audio; identify each stream/application. Per-app output routing is retained as the optional advanced step. | —; stream inventory M8, routing M10 after mixer/routing support. U; audio service. |
| `display.configuration` — Display Configuration | Actual display/current/supported modes; resolution and scaling first; orientation, refresh rate, brightness and tearing policy. Device-dependent controls remain visible with a reason when unavailable. | H for initial display; M2 priority, unsupported hardware/presentation features M10. U/S; display/compositor service. |
| `display.night-light` — Night Light | Off/On/Auto; strength; separate day/night temperature targets; schedule source/status linked to Day–Night Cycle. Apply across the display, not just the app. | —; M6 after global color-transform support. U; compositor color + scheduler. |
| `input.accessibility` — Accessibility | Visible later-feature destination with a WIP summary. Specialized features need a follow-up scope; basic keyboard access, contrast, focus and readable layout apply from M1 everywhere. | —; specialized tools M10. U/S; relevant input/text/accessibility services. |

## 3. Connected Devices

| ID / page | Requested sections and controls | Baseline; activation / owner |
|---|---|---|
| `devices.bluetooth` — Bluetooth | Later Bluetooth management destination. Keep top WIP explanation until a Bluetooth service/driver and a separately defined feature scope exist. | —; M10. U/S; Bluetooth service. |
| `devices.external` — External Device Handling | Device list, per-device automount on login and on plug-in, saved policies and actual mount/error state. Use stable device/volume identity, supported filesystems and safe permission checks. | C for explicit bounded mounts; M8. U/S; storage/device broker. |
| `devices.printers` — Printers | Later printer management destination. Visible WIP page until a print service/driver and an agreed feature scope exist. | —; M10. U/S; print service. |

## 4. Networking

| ID / page | Requested sections and controls | Baseline; activation / owner |
|---|---|---|
| `network.connections` — Connections | Connections list; name and active state; IP settings, resolver settings, automatic connection, priorities and link negotiation. Initial primary wired IPv4 profile first; profile priority/multiple links only when a real connection manager exists. No implied Wi-Fi/IPv6 support. | C for primary IPv4/DNS/hostname; M3 base, M8 profiles and negotiated-link policies. S; network service. |

## 5. Appearance & Style

Colors & Themes, Text & Fonts are groups beneath this category. Keep Wallpaper and
Animations as directly discoverable pages. Add transparency/translucency and blur
to Application Style as carried forward from the accepted design direction.

| ID / page | Requested sections and controls | Baseline; activation / owner |
|---|---|---|
| `style.wallpaper` — Wallpaper | Plain color, image and slideshow; wallpaper picker with categories; paired/dynamic light/dark wallpapers. Shared picker/provider, not one copy per theme. | H for existing generated background; color/image/picker/slideshow M5, shared scheduled pairs M6. U; wallpaper service + bounded image decoder. |
| `style.colors` — Colors | Accent selection (six initial presets, later custom picker), dark/light modes and switching options. Automatic switching references the common scheduler used by Quick Settings and Night Light. | A for manual modes/six accents; reuse M1, user scope M3, picker M5, scheduled switching M6. U/S; appearance service. |
| `style.application` — Application Style | Two or three shared widget styles, including rounded and sharper-corner alternatives; button variants; optional transparency/translucency and backdrop blur. Effects are separate from geometry and remain toggleable. | A for one fixed shared style only; M5. U; shared toolkit/resources + compositor for materials. |
| `style.decorations` — Window Decorations | Several decoration presets: conventional Windows-like controls, macOS-like dots and additional Zenith options. Define geometry, button affordances and focus states through the host; clients cannot impersonate trusted chrome. | H for one trusted decoration; M5. U; compositor decoration service/resources. |
| `style.icons` — Icons | Select among system icon sets, preview common roles, show missing-icon fallbacks. Shared application/shell resource lookup. | —; M5. U; bounded icon resources. |
| `style.cursors` — Cursors | Select among cursor styles, preview roles and retain a readable recovery cursor. Apply consistently in the host, not just Meridian. | H for fixed host cursor; M5. U; compositor/input cursor resources. |
| `style.sounds` — System Sounds | Select interaction sound packs; map/test actual system sound events once playback exists. | —; M8. U; sound-event policy/resources + audio service. |
| `style.splash` — Splash Screen | Enable/select an animation during the authenticated transition from login manager to desktop. It must not delay or hide a failed session startup. | —; M5 style/resource shell, functional activation with M9 session lifecycle. U/S; session host. |
| `style.login` — Login Manager | Several greeter styles, including Windows-like, macOS-like and Zenith alternatives. Style/layout only here; auto-login policy links to Screen Locking and stays administrative. | C/H for authenticated greeter and packaged branding; M5 resources, M9 trusted application/lifecycle. S; trusted greeter. |
| `style.boot-splash` — Boot Splash | Optional animation before the login manager, if supported. Retain diagnostics/recovery and distinguish boot splash from post-login animation. | —; M9/M10 if the boot/display path can support it. S; boot/display service, never an ordinary client drawing over authentication. |
| `style.fonts` — Fonts | System UI font selection, antialiasing and hinting options; honest previews of supported rendering modes. Changes must affect consumers, not only the picker. | C/H for verified packaged fonts in trusted hosts; native font/render resource work M6. U/S; shared text renderer/resources. |
| `style.font-management` — Font Management | All Fonts, Personal Fonts and System Fonts views; install/manage fonts. User installs stay user-scoped, system installs require authority; malformed fonts get bounded validation. | C for font package delivery only; M6. U/S; font catalogue/import service. |
| `style.animations` — Animations | Global speed; per-effect enable/disable for open/close, maximize, minimize, fullscreen, peek desktop and virtual-desktop switching. Mark effects WIP until that actual operation/animation exists. | —; M5 basic effects, M7 desktop-dependent effects. U; compositor effect policy; reduced-motion fallback. |

## 6. Apps & Windows

Window Management groups Window Behavior, Task Switcher and Virtual Desktops.

| ID / page | Requested sections and controls | Baseline; activation / owner |
|---|---|---|
| `windows.notifications` — Notifications | Do Not Disturb, visibility conditions and popup behavior. Settings summaries remain truthful before the notification service exists. | —; M7. U; notification service/compositor presentation. |
| `windows.behavior` — Window Behavior | Focus: click-to-focus in DE, focus delay, raising; titlebar double-click/maximize-button actions; movement screen-edge and window snap zones. Expose per-session capabilities for floating DE versus later tiling WM. | H for fixed DE behavior; M7. U; window manager/compositor. |
| `windows.switcher` — Task Switcher | Several visualizations; sort order; one-window-per-application option; filters for virtual desktops and monitors when those concepts exist. Use owned app/window identity. | —; M7, monitor-specific filters after display support. U; session window/task service. |
| `windows.desktops` — Virtual Desktops | Create/manage virtual desktops and their order/names; coordinate switching with animations/task filtering. | —; M7. U; compositor/workspace service. |

## 7. Workspace

| ID / page | Requested sections and controls | Baseline; activation / owner |
|---|---|---|
| `workspace.behavior` — General Behavior | OSD status/notification popups; scrollbar click jumps versus page steps; file/folder click selects versus opens; middle-click paste; double-click interval; drag/drop asks versus same-drive move policy. Share these rules with ported apps. | H for selected hardcoded behaviors only; simple input policies M4, OSD/clipboard/drag-drop integration M7. U; toolkit, input, compositor and filesystem-operation services. |
| `workspace.search` — Search | File indexing enable/status; data to index; include/exclude rules; runner/search providers for math, power actions, apps and future providers. Reuse providers in the start menu and WM runner. Settings-page search is separate and already planned for M1. | —; runner providers M7, bounded permission-aware file index M8. U; search/index services. |

## 8. Security & Privacy

| ID / page | Requested sections and controls | Baseline; activation / owner |
|---|---|---|
| `security.locking` — Screen Locking | Automatic lock and idle timeout; delay before password is required; administrative auto-login configuration/link. Never implement lock as destructive logout or treat auto-login as password grace. | C for authentication/logout only, no true lock; M9. U/S; trusted lock/authentication and login policy services. |

## 9. Language & Time

| ID / page | Requested sections and controls | Baseline; activation / owner |
|---|---|---|
| `locale.region` — Region & Language | Language preferences and per-field number, time, currency, measurement, address and phone formats; **Data & Storage Sizes** (SI/IEC size-unit formatting). Include real previews and translation/font availability, not disk-management controls. | C for fixed English text/time formatting, no general locale preferences; M6. U; locale/formatting/text services. |
| `locale.time` — Date & Time | Automatic/manual time mode, manual adjustment and timezone. Existing one-shot NTP is not a continuous automatic sync policy; display the actual source/state and preserve monotonic timers. | C for timezone and software UTC/NTP; timezone GUI M3, manual/sync-policy service M6. U for display formatting; S for clock/default timezone authority. |
| `locale.day-night` — Day–Night Cycle | Automatic solar timing using location (opt-in detected or manual), or manually chosen day/night times. Report location/source/fallback; supply one schedule to theme, wallpaper and Night Light pages. | —; manual schedule then manual-location solar M6, automatic detection when an actual provider exists. U; shared schedule/location service. |

## 10. System

| ID / page | Requested sections and controls | Baseline; activation / owner |
|---|---|---|
| `system.about` — About | System information; software and actual installed/running versions; detected hardware. Distinguish running kernel from installed package versions; unknown values stay unknown. | C/H for proc/system info; read-only page M1/M3. Readable metadata; system info service. |
| `system.power` — Power Management | Suspend policy/action and automatic idle dimming. Separate display dimming from backlight hardware and prove real suspend/resume before enabling it. | C for reboot/poweroff only; dim M9, suspend M9/M10 after driver/firmware work. U/S; idle/display/power services. |
| `system.users` — Users | Manage accounts: actual user list, add, delete and account/password actions. Existing account creation does not imply safe deletion exists. Protect active/system identities and make home-data handling explicit. | C for identity, add and password services; list M3, guarded mutations M9. S for account administration; trusted credential prompts. |
| `system.autostart` — Autostart | Inspect/add/remove/enable/disable application startup entries and their actual failures. Use authorized app identities and recovery from crash loops. | —; M9. U/S; app/session launch service. |
| `system.session` — Session | Confirmation policy for shutdown/reboot/logout; session restore behavior. Show actual DE/Headless availability and preserve busy/unsaved-work handling. Restore only supported app state, never credentials. | C for session selection and safe teardown; basic summary/default M3, confirmation/restore M9. U/S; session/app lifecycle services. |

## Coverage check

Counts by category: **1 + 10 + 3 + 1 + 13 + 4 + 2 + 1 + 3 + 5 = 43**.
All 10 categories and 43 destinations are required in M1. Later-phase pages retain
their WIP banner rather than being removed from navigation. Option groups such as
keyboard Focus/Titlebar/Movement, font All/Personal/System, and region formats are
part of page coverage, even though they are not counted as additional destinations.
The original outline remains the traceable scope record; additional future features
require an explicit inventory change rather than an open-ended interpretation of “…”.
