# Zenith applications, naming and developer tools

Direction accepted on 2026-10-11: Zenith-specific applications/packages should share
an astronomical/daylight identity; overhaul both Files and the graphical Editor.
Investigate a developer environment with GCC preferred, Python, Vim and nano.
These ports are future research goals. Sunrise is the working name of the implemented
bundled installer; other names and separate package identities remain proposals,
with no claim that any name is globally unique.

## Naming policy proposal

Use expressive names for Zenith-owned applications, with a descriptive subtitle in
menus and documentation. Suggested package IDs use zenith-NAME; third-party ports
retain upstream identities and commands (gcc, python3, vim, nano). Existing install,
pkg, shutdown and other commands remain unchanged. A name does not require a separate
package: small session controls may belong to the desktop/session package initially.

| Proposed name | Purpose | Example package identity |
|---|---|---|
| Sunrise (implemented installer) | Guided disk/account installation; first-run welcome remains proposed | zenith-sunrise (future separate package) |
| Sunset | Logout, shutdown and restart controls | Part of the session suite initially |
| Horizon | File manager | zenith-horizon |
| Aurora | Graphical text/code editor | zenith-aurora |
| Meridian | Settings | zenith-meridian |
| Orbit | Graphical package/update browser | zenith-orbit |
| Observatory | System/process/resource monitor | zenith-observatory |
| Beacon | Network status/configuration and diagnostics | zenith-beacon |
| Prism | Theme/appearance tools and asset collections | Part of Settings or zenith-prism |

Package descriptions and searchable keywords must expose familiar terms such as
Files, Editor, Settings and Updates. Prefer functionally useful first-party tools;
do not fork upstream software merely to give it a celestial name. Check naming
collisions before publishing finalized identities.

## Files and graphical Editor requirements

Horizon builds on the existing [Files rework](SYSTEM-PLAN.md#7-files-rework): locations,
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

## Developer environment: staged feasibility

A native loader, versioned syscall ABI and C library are prerequisites for conventional
native ports. Existing kernel-linked C# shell commands are not a POSIX C runtime.
ELF loading does not make Linux executables or distro packages compatible.

1. **Host cross-development SDK:** establish Zenith application startup/link layout,
   headers, libc/syscall wrappers and compiler runtime. Prefer GCC plus an assembler/
   linker producing Zenith-targeted executables from Linux. Validate a small C program
   and a separately installed update before promising general source compatibility.
2. **Terminal and libc foundations:** raw input, ANSI cursor/control behavior, terminal
   dimensions, termios-like operations, signals/cancellation and required file/process
   APIs. Port a terminal capability/curses layer as appropriate; test resizing,
   editing, save errors, exit and terminal-state restoration.
3. **Terminal editor ports:** assess nano and a reduced terminal-only Vim separately.
   Nano's ncurses dependency is a concrete library prerequisite. A reduced Vim avoids
   GUI/X11 dependencies, but remaining OS/terminal calls still need a source audit.
   Porting difficulty must be measured rather than inferred from an editor's UI size.
4. **Python:** investigate CPython cross-building with a deliberately stated module
   subset. First acceptance is a REPL plus imports and file I/O; add sockets, TLS,
   threads, subprocesses and extension modules only as their dependencies work. Native
   package installation, virtual environments and pip need separate validation. A
   CPython cross-build needs a matching build-platform Python. MicroPython may be an
   optional earlier experiment, but is not evidence that full CPython or PyPI works.
5. **GCC running on Zenith:** port the compiler's host runtime and dependencies plus
   assembler/linker, make, shell/build utilities and required C/C++ runtime support.
   GCC needs GMP, MPFR and MPC, among other build prerequisites. A compiler built on
   Linux to run on Zenith is distinct from a compiler that itself builds on Zenith.
   First goal is compiling and executing a small C program locally; full self-hosting
   and C++/other language frontends are later acceptance gates.

Suggested future developer collection: zenith-sdk (host-side initially), libc headers/
startup files, binutils, gcc, make, python3, nano, vim and eventually git and SSH tools.
Cross-build packages belong to the Linux development environment; Zenith-native
packages must be labeled separately with their actual architecture and ABI.
Do not change the kernel's Cosmos/.NET toolchain merely to offer GCC for user programs.
The current ext2 profile has a 512 MiB root limit: measure storage/memory needs and
extend filesystem support before promising a complete on-system SDK.

Primary references checked on 2026-10-11:
[GCC build/host/target terminology](https://gcc.gnu.org/onlinedocs/gccint/Configure-Terms.html),
[GCC prerequisites](https://gcc.gnu.org/install/prerequisites.html),
[CPython cross-compilation](https://docs.python.org/3/using/configure.html#cross-compiling-options),
[nano terminal/library documentation](https://www.nano-editor.org/dist/latest/nano.html),
[MicroPython/CPython differences](https://docs.micropython.org/en/latest/genrst/index.html),
[Vim build instructions](https://github.com/vim/vim/blob/master/src/INSTALL).
These document upstream capabilities/dependencies; none establishes a working Zenith port.

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
