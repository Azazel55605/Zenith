# Appearance — styles, sets and startup (U14)

[Master roadmap](../ROADMAP.md) · [Meridian pages](../MERIDIAN-PAGES.md) · [Motion](MOTION.md)

Work IDs below are detailed steps of the master tracker (U14), not a separate schedule.

Requested 2026-11-19: more wallpapers, Application Style redone with the materials that sat in Colors, window
decorations, icon sets, cursor packs, the first system sound pack, session splash, login and boot styles, and more
animation, all for Meridian's Appearance settings. Settings that did not belong there were moved. New work starts
as a concept; implementation waits for approval of each concept.

## AP0 — Reorganized Appearance (kernel 0.0.72)

- **Application Style** now holds the materials: transparency, its strength, backdrop blur and whole-app backgrounds.
  **Colors** keeps the color mode and accent. Both pages edit the same appearance record and share one
  Apply/Reset/Defaults footer, so saved choices are unchanged.
- **System Sounds** moved to Input & Output › Sound (id `audio.sounds`, was `style.sounds`).
- **Login Screen** only chooses how the greeter looks; screen locking and automatic login are Security › Screen
  Locking. **Boot Splash** only chooses the animation; boot diagnostics and recovery access are System › Session.
- Groups: Colors & Themes (Colors, Application Style, Window Decorations, Icons, Cursors), **Startup & Login**
  (Session Splash, Login Screen, Boot Splash, a new group), Text & Fonts; Wallpaper and Animations stay direct pages.
- Checks: Meridian's host tests (materials only on Application Style, eleven pages under Input & Output, System
  Sounds under Sound), Prism's page list from the inventory.

## Concepts (approved 2026-11-19)

Approved with three additions: preview pickers instead of dropdowns for cursors, decorations, startup styles and
wallpapers, and the three widget styles built in full. The Ribbon concept's close tint now centers on its icon.

| ID | Concept | Proposal |
|---|---|---|
| AP1 | [Application Style and Icons](../design/appearance-concept.svg) | Widget styles Rounded (current), Soft and Sharp; Filled, Tonal and Outline buttons; the materials with a live preview. Icons: Zenith (Lucide, installed) with proposed Zenith Bold and Zenith Fine weights; Tabler, Phosphor, Remix and Material Symbols checked as other sets; role previews and a missing-icon fallback. Sets ship as their own packages. |
| AP2 | [Window decorations](../design/decorations-concept.svg) | Four styles from one decoration service: Zenith (current), Classic, Dots and Ribbon (new compact style), each with active, inactive and light states and a geometry table. |
| AP3 | [Wallpapers, second set](../design/wallpapers-concept-2.svg) | Orbits, Tides, Facets and Nebula, dynamic like the first four. The wallpaper choice holds 12 entries and all are used: the settings ABI needs a revision raising it to 24. |
| AP4 | [Cursor packs](../design/cursor-packs-concept.svg) | Contrast (1.5x, heavy outline, black and white) and Comet (accent-filled, white rim) beside Meridian; same roles and hotspots; a size choice; login and recovery keep Meridian Light. |
| AP5 | [System sounds](../design/sounds-concept.svg) | "Zenith Chimes": 12 events tuned in C major, quiet and short, synthesized from recipes by a repo tool into a `zenith-sounds` package; the System Sounds page under Sound with pack, level and per-event toggles. |
| AP6 | [Startup](../design/startup-concept.svg) | Session splash: Crossfade (current), Rise, Iris and Greeting. Login: Zenith card (current), Accounts (preselected last-used account), Clock and Split. Boot: Phases (current), Orbit, Pulse and Quiet. All keep failure display and recovery. |
| AP7 | [More motion](../design/motion-additions-concept.svg) | Twelve places that still change at once: taskbar centers, menus and popups, dialogs, Prism, notifications, toggles, tabs, changing lists, snapping, progress, counts, and page fades in every app. |

## AP3 — Wallpapers, second set, with a preview picker (kernel 0.0.72)

- Orbits, Tides, Facets and Nebula are formulas in `src/Gui/Graphics/Wallpaper.cs` beside the first four; they follow
  light/dark mode and the accent. Their names follow the first 12 (`orbits`, `tides`, `facets`, `nebula`), so the
  legacy snapshot (call 625) keeps its 12 slots; a choice from the second set reads as itself there.
- **Call 669, `zenith-native-style-catalog-abi` 1.0 (kernel 0.0.72, with the sound operations)** ([header](../../userland/include/zenith_style.h)): list every
  choice of a setting (label, kind, color) and draw a preview of a wallpaper with the desktop's own formulas. Saving
  stays call 626 with the same field numbers. `/etc/zenith/style.conf` and fields 9..16 are reserved for the styles
  still to come (cursor size, decorations, widgets, buttons, splash, login, boot, sounds).
- **Meridian › Wallpaper** is a picker: tiles in Dynamic and Solid colors groups, the chosen one outlined with a check,
  previews cached until the mode or accent changes, Apply as before. Older kernels keep the list. The toolkit gained
  `ZUI_TILE` (a picture the app paints, with hover, focus ring and selection drawn by the control).
- Checks: host tests (catalogue, a second-set choice saved and kept across reboot), Meridian's host test (previews,
  cache, choosing Tides, Apply); the settings gate (146 checks) on the new kernel.

## Fixes in the same round

- **Taskbar centers:** the network panel grew a row for the note that non-root sessions always see (it was drawn past
  the panel); the sound panel's error note has its own row instead of covering the volume buttons; the bar redraws
  when the network or sound state changes from elsewhere (Beacon, Meridian, the wheel, a cable), so the muted strike
  and the offline dot are never stale; the volume knob drags; the wheel turned down lowers the volume.
- **Prism's panel mode:** the scroll bar works: the wheel scrolls the list, the thumb drags, the track pages, and it
  widens under the pointer; the list follows the keyboard selection only while you navigate by keyboard.

## AP4 — Cursor packs (kernel 0.0.72)

- Meridian Light and Dark plus **Contrast White**, **Contrast Black** (1.5x, pure black and white, an outline grown
  outward, no shadow) and **Comet** (the accent with a white rim). Contrast and Comet are derived at run time from the
  generated Meridian images (`src/Gui/Graphics/CursorPacks.cs`), and every pack is scaled to the **size** choice (24,
  32 or 48 logical pixels, style field 9) at the display's scale, for frames and the device pointer alike: no extra
  images in the kernel. Older Meridian builds see the nearest Meridian variant.
- Meridian › Cursors: a pack picker (arrow, link, text and busy drawn by the desktop) and a size picker, one Apply.

## AP2 — Window decorations (kernel 0.0.72)

- Zenith, Classic, Dots and Ribbon from one service (`src/Gui/Graphics/Decorations.cs`): bar height, corner radius,
  button geometry and hit targets, title placement and focus treatment per style, as in the concept's table. The
  desktop draws every window's bar with it; a new style keeps every window's content size, so programs are not
  resized. Meridian › Window Decorations: previews of a focused and an inactive window per style.

## AP1 — Widget and button styles (kernel 0.0.72)

- **Rounded**, **Soft** and **Sharp**: every rounded rectangle the toolkit draws follows the style (Soft about 1.6
  times as round, Sharp at most 2 px); circles and pills stay round. **Filled**, **Tonal** and **Outline** primary
  buttons. Native apps read both through call 669 when their appearance changes (the desktop sends every program a
  reset on a change); the desktop's own controls follow `Theme.ControlRadius`. Meridian › Application Style shows both
  pickers under the materials.

## AP6 — Session splash, login and boot styles (kernel 0.0.72)

- **Splash:** Crossfade, **Rise** (the desktop grows from 94 %), **Iris** (a circle opens from the account picture) and
  **Greeting** ("Good evening, NAME" and the time, then the desktop). The log says `SessionFade ... style=NAME`.
- **Login:** Zenith card, **Accounts** (tiles; the last account used is preselected with the password field open,
  arrows choose another), **Clock** (the time; any key or click brings up the card) and **Split** (accounts in a side
  panel). Only accounts that may sign in show (root and uid 1000+ with a password); none falls back to the card. The
  last account is kept in `/var/lib/zenith/last-login`. Sign-in, throttling and power confirmation are unchanged.
- **Boot:** Phases, **Orbit** (dots light per phase), **Pulse** (the logo and the phase name) and **Quiet** (a hairline).
  The first phase always uses Phases (the setting is on the disk it mounts).
- Previews for all three pickers are drawn by the desktop.

## AP7 — More motion (kernel 0.0.72)

- Rising into place, scaled by the animation setting and landed by any input: the taskbar centers (8 px), Prism's
  menu (rises) and palette (drops, 12 px), and the +N window list.
- Toolkit switches slide (100 ms) in apps that run the frame clock (Meridian).
- Beacon and Orbit crossfade their pages (call 629), like Meridian.
- Second part: the portal and Sunset dialogs rise like the overlays; toolkit tabs glide their raised background to
  the new tab (140 ms); meters ease to a new value (160 ms); rows that join a list rise 12 px into place (160 ms, not
  on its first fill); Observatory crossfades its pages too.
- Third part (kernel 0.0.72): see AP9–AP11 below; Horizon's folder changes still redraw at once (a fade there needs its own
  design).

## AP5 — Zenith Chimes (kernel 0.0.72)

- `tools/build-sounds.py` renders the twelve events from recipes into mono 24 kHz WAV files (at most 66 KiB each,
  -12 dBFS, reproducible), packaged as **zenith-sounds** (recipe generator `sounds`). `SystemSounds` plays them at 48
  kHz stereo through the session's mixer, under the apps: sign in, sign out and lock, attention (a permission dialog),
  error (a failed sign-in), done (an Orbit change), volume (the taskbar) and shut down. Device and notification events
  wait for removable drives and notifications. Input & Output › System Sounds: Zenith Chimes or Silent.

- Second part: a level (0–100 %, default 70) and a switch per event in `/etc/zenith/sounds.conf` (trash and screenshot
  off by default). Meridian's System Sounds page has the level, a switch and a Play button per event; Play previews a
  sound even when its event is off. Call 669 ops 3 (read), 4 (save, system settings grant) and 5 (play). Shell:
  `settings set sound-level N`, `settings set sound-off EVENT,EVENT|none`.

## Fixes in the second round

- System sounds stream as the system (uid 0): at the login screen the session is nobody, and the mixer refused it, so
  the failed sign-in sound never played.
- A sign-in name that cannot exist now fails like a wrong password (same delay, message, log line and sound) instead
  of returning at once without either.
- Beacon's DNS step tries one.one.one.one, google.com and example.com in turn, so a network that filters one name
  no longer reports DNS as broken.

## AP8 — Icon sets (kernel 0.0.72)

- Zenith Bold (2.6 px strokes) and Zenith Fine (1.4 px) render the default drawings at another weight
  (`tools/build-ui-icons.py --package --set bold|fine`) into `/usr/share/zenith/icons/bold|fine/`, packaged as
  **zenith-icons-bold** and **zenith-icons-fine** (extra repository). Style field 17 (`ICONS`) picks the set; the
  desktop and the toolkit look in its folder first and fall back to the default, so a partial set still works.
  Meridian › Icons is a preview picker; a set that is not installed says so.

## AP9 — Notifications (kernel 0.0.72)

- `NotificationCenter` in the kernel; call 670, `zenith-native-notifications-abi` 1.0
  ([header](../../userland/include/zenith_notify.h)): op 1 posts a title (64 bytes), body (160) and icon name; the
  sender shown is the process name from the kernel, so a program cannot pose as another; five posts per sender per ten
  seconds. Ops 2 and 3 read and save Do Not Disturb and the popup time (`/etc/zenith/notifications.conf`; saving under
  the system settings grant).
- The desktop's `Toasts`: up to three cards above the taskbar at the right, sliding in from the edge (180 ms), held
  4/6/10 s (the pointer over one holds it), dismissed by a click (sliding out); the notification sound plays once per
  batch. Do Not Disturb keeps them in the history only.
- Posted by the system: Orbit results (applied or not), file jobs that fail or take two seconds or more. Shell:
  `notify [--icon NAME] TITLE [BODY]`, `notify history`, `notify dnd on|off`, `notify popup short|normal|long`.
- Meridian › Notifications: Do Not Disturb, popup time and a test button.

## AP10 — Window snapping (kernel 0.0.72)

- Dragging a window's title to the left or right edge snaps it to that half, the top edge maximizes it, near a corner
  (64 px) that quarter. A tinted preview grows from the pointer to where it will land (120 ms, scaled by the animation
  setting). Pulling a snapped window away gives it back its size under the pointer. Windows whose minimum or maximum
  size does not fit a zone do not snap there. Zones are `WindowSnap` (host-tested).

## AP11 — Counts (kernel 0.0.72)

- Desktop: the taskbar's +N and the sound level fade the old number up and the new one in (120 ms); +N's ring pulses
  once (`CountMotion`). Toolkit: a navigation row's detail that is a number (Orbit's update, installed and source
  counts) does the same in every app.

## AP12 — Notification history and motion in every app (local)

- A bell beside the sound button opens the history panel: newest first (five shown), each with its sender, age,
  title and body and a close mark; Clear all; a Do Not Disturb switch; the way into Meridian › Notifications. The
  bell shows the unread count (animated, cleared when the panel opens) and a strike under Do Not Disturb. Toasts stay
  away while the panel is open. The bell is drawn by the desktop: `zenith-icons` is at its 64-file limit.
- Toolkit `zui_motion_tick`: the frame clock for every app's loop (a frame at most every 16 ms while something
  animates). Horizon, Equinox, Orbit, Beacon, Observatory and desktop-tools ran without it, so tabs, lists, meters and
  counts stood still there; desktop-tools now also follows widget styles, icon sets and appearance changes live.

## Fixes after the first round of testing (kernel 0.0.75)

- Boot animations: the style travels on the kernel command line (`bootstyle=orbit|pulse|quiet`, written into both boot
  menus when the setting is saved, carried across kernel updates, and corrected at boot when the menu is older than
  the setting), so the first frame already uses it; a boot-screen thread redraws only the moving area: Phases' sheen,
  Orbit's comet, Pulse's breathing logo, Quiet's line growing between phases.
- Zenith Chimes 0.2.0: sign in, sign out, done, trash, screenshot and shut down are plucked chimes in the style of the
  device sounds.
- Meridian's picker previews fill their tiles up to the outline, rounded with the tile in Rounded and Soft
  (`zui_image_round`).

## AP13 — Notifications from the system, buttons and per-app choices (local, kernel 0.0.76)

- System sources, all posted by the kernel:
  - **Network:** a working connection lost (cable out or a failed reconnect) and "Back online"; disconnecting or
    turning the network off yourself stays quiet.
  - **Storage:** the system disk below 5 % or 256 MiB, once, until it recovers above 10 % and 512 MiB
    (`StorageWatch`, checked once a minute).
  - **Security:** wrong passwords for an account since the system started, reported at its next sign-in.
  - **Orbit:** a background update check two minutes after the desktop starts and then daily, when online and
    nothing else changes packages, telling when updates are ready (again only when their number changes); "Restart
    to finish the update" after a kernel update, with Restart now (through Sunset) and Later.
  - **Terminals:** a command that ran 10 s or more, from Polaris (under its own name, with Show) or the built-in
    Terminal when it is not in front.
- Buttons: up to two per notification. System buttons open an app (with arguments) or restart through Sunset; a
  program's buttons reach only itself (event 13, `Z_GUI_NOTIFICATION_ACTION`, and its window comes forward). Toasts
  show them right-aligned; the history panel keeps the first. Shell: `notify --action LABEL APP`.
- Per-app switches: Meridian › Notifications lists every sender seen since the system started and every one switched
  off (`OFF=` in `/etc/zenith/notifications.conf`); an app switched off posts nothing. Shell: `notify mute NAME on|off`.
  A program's own notifications skip the popup while its window is in front (they go to the history).
- `zenith-native-notifications-abi` 1.1: ops 4 (senders), 5 (switch one), 6 (post with buttons).

### Popup visibility and behavior

Meridian › Apps & Windows › Notifications chooses whether to show popups over fullscreen windows or from the
focused app. These conditions and Do Not Disturb keep suppressed notifications in the taskbar bell's history.
The popup stack starts in the chosen corner of the work area. Duration, holding on hover, and notification sound
are independent settings. The test notification uses the saved configuration. Per-app switch-off still disables
both the popup and the history entry.

The optional `POPUP_FLAGS` and `CORNER` keys in `/etc/zenith/notifications.conf` default to `13` and `bottom-right`.
Flags 1, 2, 4 and 8 enable fullscreen, focused-app popups, hover hold and sound, respectively. The kernel advertises
`zenith-native-notification-preferences-abi` 1.0. Request version 2 of call 670 op 2 fills the original 32-byte snapshot's
last eight bytes with `u32 flags` and `u32 corner`; op 3 accepts `u64 revision, u32 flags, u32 corner` while retaining
its original argument encoding. The settings grant or root still controls saves. Request version 1 keeps the
original wire format and updates existing fields independently of the new flags and corner.
- Gate `tools/appearance-smoke.py` (release check `l`): boot style in the menu, the failed sign-in report, snapping
  to the left half, top and a corner, a toast and its dismissal, a button, a sender switched off, a long command,
  the bell panel with Do Not Disturb and Clear all, and live style switching.

## Next

Keyboard snapping (Meta+arrows); notifications from removable drives (S6) and downloads (N3) when those exist;
picker and login-style checks in the gates.
