# Zenith applications, naming and developer tools

The application names below include both installed programs and proposed extensions. The [master roadmap](ROADMAP.md#core-delivery-work) owns delivery state for A1/A2, U2–U14 and X1–X5. Component pages define each program’s behavior and limits.

## Naming policy proposal

Use expressive names for Zenith-owned applications, with a descriptive subtitle in
menus and documentation. Suggested package IDs use zenith-NAME; third-party ports
retain upstream identities and commands (gcc, python3, vim, nano). Existing install,
pkg, shutdown and other commands remain unchanged. A name does not require a separate
package: small session controls may belong to the desktop/session package initially.

| Proposed name | Purpose | Example package identity |
|---|---|---|
| Sunrise (implemented installer) | Guided disk/account installation; native wizard pending U4 | zenith-sunrise (future separate package) |
| Sunset (chosen, U10) | Lock, log out, restart and shut down popup and the lock screen; [concept](design/sunset-concept.svg), [design and stages](components/SUNSET.md) | Part of the session suite initially |
| Horizon (chosen) | File manager; [concept](design/files-concept.svg), [design and stages](components/HORIZON.md) | zenith-horizon |
| Aurora (chosen, U2) | Graphical text/code editor; [concept](design/editor-concept.svg), [design and stages](components/AURORA.md) | zenith-aurora |
| Polaris (chosen Terminal name) | Terminal; [design](components/SHELL.md#polaris-design) and [concept](design/terminal-concept.svg) | zenith-polaris |
| Meridian (chosen Settings name) | System Settings; [ordered plan](MERIDIAN-PLAN.md) | Existing zenith-settings retained initially; rename/migration separate |
| Orbit (chosen, U12) | Graphical package and update manager: a plan applied as one transaction, reasons, history and sources; [concept](design/orbit-concept.svg), [design and stages](components/ORBIT.md) | zenith-orbit |
| Observatory (chosen, U7) | System, process and resource monitor; [concept](design/observatory-concept.svg), [design and stages](components/OBSERVATORY.md) | zenith-observatory |
| Beacon (chosen, U11) | Network status, configuration, history and diagnostics; [concept](design/beacon-concept.svg), [design and stages](components/BEACON.md) | zenith-beacon |
| Prism (chosen) | Keyboard launcher with apps, calculator and search, beside the start menu; [concept](design/prism-concept.svg). Theming and appearance belong to Meridian | zenith-prism |
| Equinox (chosen) | Partition manager: disks, partitions, format, resize, as a plan applied at once, in the spirit of GParted and KDE Partition Manager; [concept](design/equinox-concept.svg), [component](components/EQUINOX.md) | zenith-equinox |
| Nebula (chosen) | Web browser: Zenith's own frontend around the NetSurf engine, credited as "powered by NetSurf"; [concept](design/browser-concept.svg), [design and stages](components/BROWSER.md), [current port](#netsurf-browser) | zenith-netsurf today; zenith-nebula when the frontend is renamed |

Package descriptions and searchable keywords must expose familiar terms such as
Files, Editor, Settings and Updates. Prefer functionally useful first-party tools;
do not fork upstream software merely to give it a celestial name. Check naming
collisions before publishing finalized identities.

## Files and graphical Editor requirements

Horizon is the native file manager. It provides locations and tabs, selection and file operations, associations, and bounded archive creation/extraction. Removable devices depend on S6 and remote shares on N4. [Horizon](components/HORIZON.md)

Aurora is the native graphical editor. It supports multiple documents, UTF-8 with LF/CRLF, clipboard, undo/redo, find/replace, project navigation, recovery copies and external-change handling. Keep permission, free-space and unsaved-close behavior intact when extending it. Compiler diagnostics and language-server work depend on process and toolchain support. [Aurora](components/AURORA.md)

## Doom engine package: requested feasibility slice

The optional [Doom engine work](ROADMAP.md#scope-decisions-and-non-blocking-ideas)
uses a separately installed engine and user-supplied WADs. The package contains
the executable, its adapter, the corresponding source, and the license. It
ships no WADs, including the Freedoom fixture used for local validation.

[PureDOOM](https://github.com/Daivuk/PureDOOM) is pinned in the
[port sources](../userland/doom/UPSTREAM.md). Since version 0.3.0, **Doom** is available in the desktop launcher. Version 0.4.1 uses the newer app layout and window materials. Its native WAD browser uses the Zenith UI toolkit and
the current theme. It opens in `~/doom` when available and remembers the last
folder used to start a game. Browse folders, select a `.wad`, and choose **Play**.
The engine opens the file read-only and identifies the game from its map directory.

The app requests a 640×480 window, follows the desktop's opening-size preference,
and supports maximize and fullscreen (F11).
The software renderer still draws at 320×200. The window scales that image
to the original 4:3 display aspect ratio and fills unused space with black.

```sh
native run doom --launcher
native run doom --launcher /path/to/wad-folder
native run doom --check-wad /path/to/doom2.wad
native run doom --preview /path/to/doom2.wad
native run doom --play /path/to/doom2.wad
```

`--check-wad` checks the IWAD header and directory bounds up to 30 MiB. Use
`--preview` to see the title/demo and press Escape to exit. In `--play`, Escape
opens the game menu, Enter selects an item, and F10 offers to quit. Close the
window to exit directly. The classic controls are arrows to move/turn, Shift
to run, Alt with Left/Right or comma/period to strafe, Ctrl to fire, Space to use, Tab for the
automap, and number keys to switch weapons. Controls follow physical key positions.
The engine package requires the large-native and physical-key ABIs, both first
included in the kernel 0.0.77 source tag.
The engine recognizes classic `IWAD` files and BFG-style base games that
start with `PWAD`, including their alternate title graphic. Standalone loading
rejects ordinary add-on PWADs. A Doom WAD with the fourth episode selects Ultimate Doom.
A source build uses `python3 -B tools/build-userland.py DESTINATION --program doom --version 0.4.1`.

- [X5](ROADMAP.md) owns the executable/package size increase and the larger
  native read budget needed for WAD startup and level transitions.
- The [focused physical-key ABI](GRAPHICAL-CLIENTS.md#focused-physical-keys)
  supplies press/release and input reset. The adapter clears queued edges,
  held gameplay keys, and held map controls on focus loss or reset.
- Gameplay needs per-user save/config paths. Sound effects can be resampled to
  48 kHz stereo PCM. Music needs a MIDI synthesizer and can follow the first
  playable build.

Proposed first scope: classic Doom/Doom II IWADs, optional compatible vanilla
PWADs, scaled software rendering and keyboard controls, per-user saves/config,
clear missing/unsupported-WAD errors, and reliable exit back to the desktop.
Modern GZDoom-specific mods are outside this initial compatibility target.
Engine updates/uninstall must preserve user-owned WADs and saves. Validate real
gameplay and save/load in the guest; a menu screenshot is insufficient. Test
package updates without changing the running kernel before calling it independent.
Retain upstream GPL source/license obligations with the distributed engine.

## Developer ports

The [developer tools contract](DEVELOPER-TOOLS.md) owns prerequisites and acceptance.
Master work is X1–X5; optional Doom stays outside the main product sequence.

## Additional product ideas, proposed

Prism handles launcher search, Observatory monitors processes and resources, Beacon manages the wired connection, and Orbit shows package plans and changes. Notifications, clipboard and the theme and asset services are in use. Potential additional tools include a standalone archive manager, screenshot utility, SSH client and diagnostic exporter. [Desktop and remaining app work](ROADMAP.md#core-delivery-work)

Prioritize common workflows (install, edit/save, move files, configure networking,
update and recover) over a large application count. New ideas do not reorder the
accepted installed-system/package delivery sequence in [ROADMAP](ROADMAP.md).

## NetSurf browser

The optional `zenith-netsurf` package supplies a native Zenith frontend for NetSurf 3.11. The app menu lists it as **Nebula**; the window around the engine follows the [Nebula concept](design/browser-concept.svg) ([stages](components/BROWSER.md)). Run `native run netsurf` in a desktop terminal and pass an HTTP or HTTPS URL, or an absolute HTML file path, to open it directly. Ctrl+L focuses the address field, Ctrl+F finds on the page, Ctrl+Plus/Minus/0 zoom and Ctrl+Q closes the window. The chrome uses Zenith's theme and the web viewport uses the site's CSS.

Build the package payload with `python3 tools/build-userland.py obj/packages/zenith-netsurf --program netsurf`. The build uses GCC for x86-64, GNU binutils, make, Perl, Python with Pillow, and host zlib headers. The pinned engine, dependencies and C runtime build from the checkout. The installed package includes their notices and corresponding source under `/usr/share/doc/zenith-netsurf`.

The engine supports HTML, CSS, plain text, PNG, BMP, ICO and GIF. JavaScript is disabled. HTTP and HTTPS use GET, verify certificates for HTTPS and limit each response to 8 MiB. The updated app requires the browser network ABI, which supports successive requests for pages, images and stylesheets throughout the session. Requests run one at a time. Ctrl+D toggles a per-user bookmark. Alt+Home opens bookmarks and recent pages, which are also matched locally in the address field. The page cache is in memory. [Work tracker](ROADMAP.md#scope-decisions-and-non-blocking-ideas)

The [browser smoke test](../tools/netsurf-smoke.py) installs a test-signed package on a copy of a disposable disk, follows links from a local document to HTTP and HTTPS, and checks the resulting pixels. For an interactive check on a desktop with the package installed, run `native run netsurf https://example.org/`.
