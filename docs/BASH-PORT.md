# Bash port feasibility · 2026-10-09

GNU Bash can be a long-term Zenith target. It cannot currently be installed as
an ordinary executable: Zenith needs a program runtime and substantial POSIX
services first. This assessment inspects source and architecture; no Bash cross
build or guest execution has been demonstrated.

## Current evidence

- `Zenith.csproj` builds a Cosmos NativeAOT kernel. Its ELF output is the kernel,
  not evidence of an ELF application loader or Linux syscall compatibility.
- `src/Core/Shell/Shell.cs` executes registered C# commands with `command.Run`
  and falls back to shell scripts. `ExecPipeline` completes each stage into a
  text buffer before starting the next; there are no independent child programs.
- `src/Apps/TerminalWindow.cs` owns line editing and delivers lines to the managed
  shell. It is not a POSIX terminal descriptor with termios/process-group control.
- The sibling Cosmos source provides .NET PAL plugs over its VFS and minimal
  native memory stubs for LAI. These do not constitute a general C libc/process
  ABI for Bash. No implemented WASM/WASI application runtime was found in the
  inspected Zenith/Cosmos sources. M5 explicitly schedules this runtime.

## Dependencies and gaps

| Requirement | Existing foundation | Port work |
|---|---|---|
| C execution and runtime | NativeAOT kernel, native build tools | Cross target, C startup, allocator, libc APIs, program loading and runtime imports |
| Files and directories | Cosmos VFS, System.IO, FAT, experimental ext2 | C-facing descriptors, errno, stat/dir APIs, descriptor duplication and inheritance, per-process cwd/env |
| Child execution | Kernel threads and managed commands | Process state, executable dispatch, fork-compatible subshell behavior, exec, wait and exit status |
| Pipelines and redirection | Buffered text shell pipelines | Concurrent byte pipes, EOF/close semantics, dup/dup2, descriptor lifecycle and broken-pipe handling |
| Signals | Managed shell cancellation | Per-process signal delivery/masking and child notifications |
| Interactive use | GUI terminal and line editor | TTY byte streams, terminal modes, sizing and Readline integration; process groups for job control |
| External tools | C# commands compiled into kernel | Port utilities as programs or provide explicit executable adapters |

Bash's execution code invokes `pipe` and `execve`; its child-management code
invokes `fork`, `waitpid`, signal APIs and terminal/process-group operations.
These are execution semantics, not just missing build-system checks.
Sources: [execution](https://raw.githubusercontent.com/mirror/bash/master/execute_cmd.c),
[child management](https://raw.githubusercontent.com/mirror/bash/master/jobs.c).
The mirror's master branch is a research reference, not a pinned port source.

Bash supports builds with job control and Readline disabled. This can reduce a
first port's scope, but ordinary external commands, pipelines and subshells
still need execution services. A minimal configuration also removes language
features, so it must not be presented as full Bash compatibility.
Source: [GNU optional build features](https://www.gnu.org/s/bash/manual/html_node/Optional-Features.html).

## Runtime choices

1. **Native C/POSIX applications:** the most conventional route for a broadly
   compatible Bash. Establish a target ABI, application loader, libc and process
   services, then bring up noninteractive Bash before TTY/job control. Hardware
   isolation is an OS design choice rather than a Bash requirement, but a
   protected native userland needs upstream Cosmos memory/privilege work. This
   would expand the current WASM-oriented M5 architecture substantially.
2. **WASM with POSIX extensions:** preserves the roadmap's sandboxed program
   direction, but standard WASI preview 1 alone is insufficient. wasi-libc's
   headers explicitly omit fork/exec, signals and process groups. A port needs
   additional runtime services and/or maintained Bash patches; copying WASM
   linear memory alone does not reproduce a running child's stack and continuation.
   Source: [wasi-libc unistd.h](https://raw.githubusercontent.com/WebAssembly/wasi-libc/main/libc-top-half/musl/include/unistd.h).
3. **A restricted kernel-linked C experiment:** could explore parsing and builtin
   execution after a C bridge/libc subset exists. Shared kernel state, lack of
   child execution and unsafe failure boundaries make this a prototype rather
   than the intended general shell.

Implementing Bash-like syntax in the existing C# interpreter is a separate
compatibility project; it is not a port of GNU Bash.

## Proposed proof of concept

Before committing to a runtime choice, write down the required compatibility:
noninteractive scripts, external commands, subshells/pipelines, interactive
editing and job control. Then pin an upstream Bash release and audit its actual
imports under the chosen build configuration.

The first useful runtime target is a C program that reads/writes Zenith files,
receives args/env and exits with an observable status. Follow it with a shell
that launches a second program and waits, a concurrent pipeline, and a subshell
whose cwd/environment changes do not affect its parent. A Bash proof of concept
should pass at least:

- `bash -c 'printf "%s\n" hello'`
- redirection to a file on the Zenith VFS and subsequent readback
- an external test executable with argument/environment and exit-status checks
- `printf '%s\n' hello | PORTED_CAT`
- `x=parent; (x=child); test "$x" = parent`
- command substitution that launches a child and captures stdout

Keep Readline, loadable builtins, process substitution and job control outside
that first proof of concept. Add interrupt/child-exit tests, streaming-pipe
termination and interactive terminal tests before replacing the existing shell.
A successful host Bash build or builtin-only guest demo is insufficient evidence.

## Recommendation

Treat real Bash as an explicit M5 runtime objective if it is a priority. Keep
M2 filesystem work as useful groundwork, but recognize that ext2 alone does not
provide a C ABI, processes or executable commands. If broad unmodified Bash
compatibility is essential, prefer investigating native POSIX userland; if the
WASM design remains the priority, budget for a Bash-specific POSIX extension
layer. This is a feasibility assessment; the runtime choice requires a separate design decision.
