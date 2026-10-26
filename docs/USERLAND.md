# Kernel/userland migration plan

Reviewed **2026-10-26**. The accepted goal is independently installed, updated and
fault-isolated applications/desktop outside the kernel. A source split or shared
address-space plugin does not satisfy it. See [current implementation steps](components/USERLAND.md)
and the [native ABI contract](NATIVE-PROGRAMS.md).

## Current boundary

Zenith implements protected static x64 ELF foreground execution, signed package
verification and bounded filesystem services. The locally verified extension adds finite
stdin and regular-file writes. Sunbeam `cat`, `tee`, `ls`, `pwd` and `stat` are
independent executable code. The shell, remaining builtins, GUI,
session broker and most services still run as trusted kernel-linked code.

Full local acceptance at `cba1cbf` verifies live stdout/stderr, canonical host input and
bounded byte pipelines, with independent wc/head/tail. Native contexts may wait
on managed streams; raw user execution remains serialized on the BSP. Kernel 0.0.20
is operator-installed and running. The operator accepted the 0.0.21 process/base/
filesystem checklist and the 0.0.22 text/traversal exercises. Owned TCP/verified-TLS
and atomic download services plus independent net-tools now pass full clean-source
qualification at `faecc72`. Selected CLI extraction is implemented; distributor
and installed network acceptance remain pending. Managed multicore, protected
graphics and privileged-service migration remain separate.

The [network VM handoff](NETWORK-VM-TEST.md) gives kernel-first installation and
command checks. Earlier [CLI checks](CLI-VM-TEST.md) remain useful regressions.

## Migration stages

| Stage | State | Exit |
|---|---|---|
| Boundaries and signed scripts/data | Implemented | Shared Core/UI seams and installed ownership/transaction policy |
| Protected executable foundation | Implemented, locally verified | Private mappings, identity/output/exit, faults/timeouts, independent ELF update |
| Controlled read-only file tools | Locally verified; delivery operator reported | Caller permissions/cwd, safe buffers/handles, cancellation and installed tool use |
| Input/writable descriptors | Locally verified; delivery operator reported | Independently packaged utility safely reads input/writes persistent files |
| Canonical streams/pipes | Operator-installed in 0.0.20; individual exercises separately unreported | Live std descriptors, bounded byte channels, EOF/cancel and protected-context cleanup |
| Raw TTY/process lifecycle | Operator-installed and checklist accepted in 0.0.21 | Terminal modes, jobs/wait/termination and environment |
| Controlled networking and more tools | Fully qualified at faecc72; distributor/installed network checks pending | Socket ownership/limits and independently updated network/base/fs packages |
| First graphical client | Planned | Versioned surface/input/IPC, fault isolation and lifecycle cleanup |
| Desktop/service migration | Planned | Independently restarted session/compositor/apps with explicit privilege and compatibility |

## Target responsibility split

| Layer | Responsibility |
|---|---|
| Kernel/framework | Memory protection, scheduling, drivers, filesystem/network primitives and controlled entry points |
| Privileged services | Authentication/configuration/package/network policy with explicit authorization and restart boundaries |
| Display server/compositor | Display/input ownership, surfaces, routing and client cleanup |
| DE / automatic-tiling WM | Launcher/panel/workspaces and selectable window policy |
| Applications | Terminal, Files, Settings, Editor and tools as independent executables |

Keep recovery builtins and the simple DE while APIs mature. Native ELF is a Zenith
ABI, not Linux syscalls/glibc or arbitrary distribution binary compatibility.
C# NativeAOT apps require explicit runtime/startup/link integration; host DLL loading
is not assumed. WASM remains an optional runtime investigation, not the presumed
main desktop architecture. See [Bash assessment](BASH-PORT.md) and
[developer-tool goals](APPLICATIONS.md).

## Acceptance for each extraction

- Build/install/update the executable while kernel bytes remain unchanged.
- Restart into the new version under the same kernel with declared ABI dependencies.
- Preserve caller identity and reject unauthorized file/device/service operations.
- Contain faults/timeouts/cancellation and release handles, pages and client resources.
- Bound memory, I/O and CPU; test malformed inputs and hostile pointers.
- Preserve usable shell/login/recovery and independent filesystem integrity.
- Document restart state loss and incompatible update/rollback behavior.

These gates have been exercised for the bounded Sunbeam foundation; they do not
establish completion of the later process/GUI stages. Original runtime alternatives
and migration discussion remain in the [archived plan](archive/2026-10-15/USERLAND.md).

[Network-client implementation and remaining acceptance](archive/2026-10-26/native-network-clients.md).
