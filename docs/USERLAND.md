# Kernel and userland separation

Direction clarified by the user on 2026-10-11: **desktop and applications should
ultimately run outside the main kernel, following the Linux architectural model**.
The separation is an accepted requirement; executable format, ABI and runtime remain
open. Current code is still kernel-linked. Moving source into separate projects alone
would not satisfy this requirement.

User sequencing clarification: implement signed offline scripts/data installation first,
then bring the native text-program/process/ABI foundation forward before independent
compiled tool packages. Source-only splitting is insufficient. The current test DE
stays available throughout migration; proper desktop migration remains later.
The subsequently accepted delivery order completes self-contained installation,
recipe tooling/server delivery and the online package client before native tool
unbundling; see [ROADMAP](ROADMAP.md). GCC-preferred cross-development and later
on-system GCC/Python/nano/Vim research are scoped in [APPLICATIONS](APPLICATIONS.md).

## Target boundary

| Layer | Intended responsibility |
|---|---|
| Kernel/framework | Scheduling, memory/address-space protection, device drivers, filesystem/network primitives and controlled entry points |
| System services | Session/login, configuration, package operations, network policy and other privileged management outside the kernel where feasible |
| Display server/compositor | Display/input access through controlled APIs, app surfaces and input routing |
| DE or WM shell | Launcher/panel/workspaces and floating or automatic-tiling policy |
| Applications | Terminal, Files, Settings, Editor and other independent executables |

This is an architectural boundary, not a commitment to Linux binary compatibility,
Wayland/X11, systemd or a microkernel. Drivers/filesystems can remain in the kernel.
System-service and compositor boundaries must be chosen according to privilege,
restart behavior and performance, rather than copying every Linux subsystem.
DE and WM can share compositor/toolkit protocols and applications while having
separately selectable session components.

The goal is independent installation/update and failure isolation: replacing Files
should not require linking a new kernel, and a Files crash should not panic the OS.
Kernel installation leaves the loaded kernel running until reboot. A desktop/app
update may need that program/session restarted; hot-reloading active code is not
required. Independently version packages and declare API compatibility.

## Migration steps

1. **Prepare boundaries now:** keep core operation interfaces separate from UI/HAL,
   remove accidental dependencies and define ownership/lifecycle. Current in-kernel
   services remain temporary adapters, not the intended final architecture.
2. **Program foundation:** choose target ABI/format/runtime; prove args/env, file
   handles, memory/resource limits, exit status and cancellation with a standalone
   executable. Assess Cosmos changes needed for protected native processes.
3. **Protected execution and IPC:** per-process credentials, descriptors, cwd/env,
   address spaces and bounded messages/handles. Define process lifecycle/wait,
   byte pipes and terminal behavior; avoid app access to kernel objects/pointers.
4. **First independent app:** a text utility, then a small graphical client through
   a versioned surface/input protocol. Verify app faults do not corrupt the kernel.
5. **Move GUI/session:** port toolkit/apps, then compositor/session policy as the
   required display/input interfaces mature. Keep a diagnostic/recovery console.
6. **Split packages:** independent kernel, base services, compositor/DE/WM and apps;
   compatibility dependencies control updates and restart requirements.

A shared-address-space plugin loader can be a development intermediate, but is not
protected userland. C# NativeAOT application builds need explicit runtime/link/startup
integration; the existing kernel ELF is not a general application loader. Host .NET
DLL loading must not be assumed available on Cosmos.

## Runtime decision

Protected native executables with a Zenith syscall/libc ABI are the leading research
path for the requested Linux-like model and eventual Bash/POSIX goals. This may require
substantial upstream Cosmos memory, privilege and loader work. Native ELF format alone
does not supply Linux syscalls, glibc or distribution-package compatibility.

WASM remains an optional application runtime or possible intermediate, not the presumed
final desktop architecture. Its interpreter, host APIs, IPC and isolation guarantees
need validation. A basic WASI subset does not supply Bash fork/exec, signals or job
control; see [BASH-PORT](BASH-PORT.md). Do not begin a large interpreter implementation
before the architectural compatibility decision.

## Acceptance evidence

- Build/install/update one executable without changing kernel bytes; restart it and
  observe the new version while the running kernel version is unchanged.
- Crash/terminate a client without panicking the kernel or corrupting another client.
- Deny unauthorized file/device/service operations using per-process credentials.
- Bound memory/CPU/IPC and release handles/resources after abnormal termination.
- Restart a desktop/session independently, preserving on-disk settings and providing
  a usable headless fallback. State lost on session restart must be documented.
- Upgrade/downgrade incompatible service or graphics APIs only through a compatible
  package plan, with explicit rollback/restart requirements.

M5 is now a userland/runtime architectural milestone rather than an assumed WASM-only
implementation. The exact schedule and proof-of-concept scope remain to be discussed.
