# Shell, terminal and text workflows

[Documentation](../README.md) · [Current status](../STATUS.md)

**State:** M1's interactive shell is implemented. Terminal and local console are
usable with the configured keyboard layout. Live native streams and bounded byte
pipelines are operator-installed in 0.0.20. Bounded raw input, jobs and process
ownership pass full local acceptance at `5852867`; distributor/operator checks
for that release remain pending. A full POSIX TTY remains planned.

## Implemented steps

- [x] Parse/execute quoting, variables, globs, conditionals, loops, functions,
  substitutions, scripts, redirections and bounded streaming pipelines.
- [x] Run submitted commands on workers with explicit credentials and Ctrl+C cancellation.
- [x] Support keyboard layouts, including German Shift+7 and AltGr corrections.
- [x] Recall command history in the graphical terminal and local text console.
- [x] Provide built-in text/filesystem/system diagnostics and manual pages.
- [x] Display bounded download/package progress instead of printing every update.
- [x] Launch signed independent Sunbeam while retaining recovery builtins.

## Current boundary

Byte pipes have 8 KiB buffers and backpressure; at most eight stages/workers share
a shell tree. Producers run in child shells, and the final stage retains parent
state. Early reader exit closes the input; cancellation stops all stages. Native
stdin uses canonical host lines, stdout streams live and stderr bypasses pipes.
Buffered builtins/redirections remain bounded at 1 MiB. The qualified CLI release
adds four background jobs, owned process snapshots/cancellation and foreground
raw input leases; full PTY/VT screen rendering remains unimplemented. See [native streams](../NATIVE-PROGRAMS.md#live-streams-and-pipelines).
The no-GPU serial transport preserves raw bytes but has bounded input behavior and
is not equivalent to local keyboard history/navigation. History is session state,
not a persistent cross-reboot database. Bash and nano/Vim are not available ports.

## Remaining steps

- [ ] Investigate sustained PS/2 input loss in the QEMU virtio-GPU local console.
  The long GUI release gate passes with virtio keyboard; standard-VGA recovery
  still exercises PS/2. [Observed failures](../archive/2026-10-16/native-input-and-writes.md#failures-found-during-acceptance).

- [x] Implement bounded byte streams, EOF/backpressure and stdin/stdout/stderr.
- [x] Complete guest/common stream and cancellation acceptance.
- [ ] Complete distributor/installed stream acceptance.
- [x] Add bounded foreground terminal modes; full cursor/PTY contracts remain future work.
- [x] Integrate bounded process lifecycle/jobs/wait/cancellation with caller credentials.
- [x] Extract selected base/filesystem commands; [coverage](../archive/2026-10-26/cli-process-foundation.md#native-command-coverage-in-this-batch).
- [ ] Finish remaining text/traversal and network command extraction.
- [ ] Complete distributor/VM acceptance of the new CLI release: [test guide](../CLI-VM-TEST.md).
- [ ] Assess terminal editor/Bash compatibility against measured ABI gaps.

## Source and acceptance

Owners: [shell](../../src/Core/Shell), [console](../../src/Core/Console),
[GUI terminal](../../src/Gui), [input](../../src/Core/Input).
Use [development](../DEVELOPMENT.md) for commands and
[settings/session reference](../SETTINGS-SESSIONS.md) for input/transport limits.
Future ports: [Bash](../BASH-PORT.md), [applications](../APPLICATIONS.md).

Relevant native harnesses include [foundation](../../tools/foundation-smoke.py),
[keyboard](../../tools/keyboard-smoke.py),
[progress](../../tools/terminal-progress-smoke.py),
[login/local console](../../tools/login-session-smoke.py) and
[serial](../../tools/headless-smoke.py). Verify parsing/status/redirection,
German keys/history, cancellation and secret handling through the actual transport.
[Stream acceptance](../archive/2026-10-17/native-streams.md) records the current
local result. Historical evidence remains in the [archive](../archive/2026-10-15/PROGRESS.md).
