# Developer tools and compatibility contracts

Work is tracked only under **X1–X4** in the [master roadmap](ROADMAP.md).
Current native execution is a Zenith ABI, not Linux/glibc or full POSIX. GCC/Clang
already build independent freestanding programs on the host; runtime ports below
are not delivered Zenith applications.

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


## Bash

The custom shell is functional but is not GNU Bash. A conventional Bash port needs
an audited C/libc contract, real subprocess creation/exec/wait, pipe and signal
semantics, terminal modes and process groups. Existing bounded shell jobs/raw leases
are useful foundations, not fork/exec/POSIX job control. Do not repeat the outdated
claim that Zenith has no executable or streaming interfaces at all.

First proof: noninteractive builtins, file redirection, a separate child executable
with argv/env/status, a byte pipeline, subshell isolation and child command
substitution. Then fault/interrupt/child-exit/terminal restoration. Readline,
loadable builtins and broad interactive job control require separate acceptance.
A host build or builtin-only demonstration cannot qualify a port. WASM is optional;
it is not the chosen desktop/runtime path or an automatic source compatibility layer.

Storage/resource budgets (R1), PTY (U3) and source-by-source API audits precede
shipping nano/Vim, Python or on-system GCC. Do not change the kernel's Cosmos/.NET
toolchain just to provide a user-program compiler.
