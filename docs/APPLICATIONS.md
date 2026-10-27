# Zenith applications, naming and developer tools

Work owners: **A1/A2/X1–X4** in the [master roadmap](ROADMAP.md). This page is a contract/procedure, not a separate work tracker.

> Product requirements and feasibility: future app/tool ideas are not shipped packages. Current work: [Sessions/desktop](components/SESSIONS-DESKTOP.md) and [Userland](components/USERLAND.md).

Requirements/names only. [ROADMAP](ROADMAP.md) owns A1/A2, X1–X4 and priorities.
Names other than Meridian and existing deployed commands remain proposals; package
identities must be finalized separately from UI requirements.

## Naming policy proposal

Use expressive names for Zenith-owned applications, with a descriptive subtitle in
menus and documentation. Suggested package IDs use zenith-NAME; third-party ports
retain upstream identities and commands (gcc, python3, vim, nano). Existing install,
pkg, shutdown and other commands remain unchanged. A name does not require a separate
package: small session controls may belong to the desktop/session package initially.

| Proposed name | Purpose | Example package identity |
|---|---|---|
| Sunrise (implemented installer) | Guided disk/account installation; native wizard pending U4 | zenith-sunrise (future separate package) |
| Sunset | Logout, shutdown and restart controls | Part of the session suite initially |
| Horizon | File manager | zenith-horizon |
| Aurora | Graphical text/code editor | zenith-aurora |
| Meridian (chosen Settings name) | System Settings; [ordered plan](MERIDIAN-PLAN.md) | Existing zenith-settings retained initially; rename/migration separate |
| Orbit | Graphical package/update browser | zenith-orbit |
| Observatory | System/process/resource monitor | zenith-observatory |
| Beacon | Network status/configuration and diagnostics | zenith-beacon |
| Prism | Theme/appearance tools and asset collections | Part of Settings or zenith-prism |

Package descriptions and searchable keywords must expose familiar terms such as
Files, Editor, Settings and Updates. Prefer functionally useful first-party tools;
do not fork upstream software merely to give it a celestial name. Check naming
collisions before publishing finalized identities.

## Files and graphical Editor requirements

Horizon builds on the existing [Files rework](ROADMAP.md#product-definition): locations,
places, real metadata, sorting, multi-selection, keyboard operation and permission-aware
copy/move/rename/delete. Add tabs and editor/application associations after the basic
operation model is reliable. Trash, search and archives need explicit persistence,
capacity, recursion and recovery policies. Keep current Files available for testing.

Aurora should first be a dependable graphical text editor, shared across DE and WM:

- Open/save/save-as through shared file dialogs and the guarded filesystem service.
- UTF-8 text, LF/CRLF handling, selection, clipboard, undo/redo and find/replace.
- Modified-document indication, unsaved-close prompt and external-change detection.
- Explicit permission, out-of-space and unsupported/binary-file diagnostics.
- Bounded file/buffer size; saving must not silently truncate or discard content.
- Keyboard-complete operation, line/column feedback and configurable indentation.

Follow with multiple documents/tabs, syntax highlighting, optional crash-recovery
copies and a project sidebar. Recovery copies must have a defined per-user privacy,
cleanup and reboot policy. Compiler execution, diagnostics and language-server features
come after process/IPC/toolchain support; initial Editor is not an IDE promise.
Both apps ultimately become independent userland programs; a GUI redesign alone
does not establish isolation or independent compiled-package updates.

## Doom engine package: requested feasibility slice

User request (2026-10-13): offer a separately installed Doom engine; users supply
their own WAD files. No commercial game data belongs in the engine package.
This is a proposed port, not an implemented or runtime-verified application.

[doomgeneric](https://github.com/ozkl/doomgeneric) is an initial candidate: its
platform hooks cover framebuffer presentation, keyboard events and timing. A
Zenith port also needs the C runtime/file I/O used by the engine, allocation,
application startup/exit and save/config persistence. Sound/music are separate
backend work; no Zenith PCM audio service is currently established.

The independent package depends on the native program foundation described in
[USERLAND](components/USERLAND.md). Current scripts/data packages cannot execute a native
engine. A kernel-linked C experiment could test rendering/input earlier, but
would require a kernel rebuild and would not meet the independent-package goal.
Do not silently substitute that experiment for the requested application.

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
Master work is X1–X4; optional Doom stays outside the main product sequence.

## Additional product ideas, proposed

Useful next applications include a keyboard-searchable launcher, consistent notifications,
shared clipboard/file dialogs, an archive manager, diagnostic-report export, a network
panel and graphical package plans showing dependency changes and restart requirements.
A process monitor needs real process/resource accounting; an SSH client/server needs
native execution, authentication and transport work. Theme/icon/font/wallpaper packs
need bounded runtime asset loading and per-user preferences before data updates affect
the desktop. Add screenshot capture when graphics APIs and image encoding are available.

Prioritize common workflows (install, edit/save, move files, configure networking,
update and recover) over a large application count. New ideas do not reorder the
accepted installed-system/package delivery sequence in [ROADMAP](ROADMAP.md).
