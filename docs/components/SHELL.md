# Shell and terminal

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md)

Work IDs below are detailed steps of the master tracker, not a separate schedule.

## Implemented boundary

The custom shell supports scripting, redirections, bounded streaming pipelines,
exported environment, four background jobs/wait, owner-filtered processes and
foreground raw leases. Base/filesystem/text/network commands dispatch to signed
native packages, retaining explicit recovery builtins. CLI extraction was operator
exercised; shell evaluation and privileged operations remain trusted.

## U3 — Native Terminal and PTY

- [ ] Define invocation/session-owned shell and PTY byte/mode/resize channels.
- [ ] Preserve canonical/raw input, hidden prompts, type-ahead, EOF and cancellation.
- [ ] Port rendering/selection/scrollback to shared userland controls; retain recovery.
- [ ] Test busy/jobs, account changes, session stop, crash/close and independent update
  across graphical, local text and serial paths. No arbitrary privileged command RPC.

## H1 / N1 / X2 — Related work

Sustained PS/2/relative-input and home-network behavior need measured regression
profiles, not automatic reopening of old releases. Full POSIX PTY/signals, Bash and
nano/Vim compatibility require the [developer-port contract](../DEVELOPER-TOOLS.md).

## Source and evidence

[Shell](../../src/Core/Shell), [IO](../../src/Core/IO),
[console](../../src/Core/Console), [existing terminal](../../src/Apps/TerminalWindow.cs).
[Native ABI](../NATIVE-PROGRAMS.md), [input/session contract](../SETTINGS-SESSIONS.md).
[CLI acceptance](../archive/2026-10-26/cli-process-foundation.md),
[text acceptance](../archive/2026-10-26/native-text-traversal.md).
