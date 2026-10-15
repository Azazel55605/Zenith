# Shell, terminal and text workflows

[Documentation](../README.md) · [Current status](../STATUS.md)

**State:** M1's interactive shell is implemented. Terminal and local console are
usable with the configured keyboard layout; a full raw TTY and streaming process
pipelines remain planned.

## Implemented steps

- [x] Parse/execute quoting, variables, globs, conditionals, loops, functions,
  substitutions, scripts, redirections and buffered pipelines.
- [x] Run submitted commands on workers with explicit credentials and Ctrl+C cancellation.
- [x] Support keyboard layouts, including German Shift+7 and AltGr corrections.
- [x] Recall command history in the graphical terminal and local text console.
- [x] Provide built-in text/filesystem/system diagnostics and manual pages.
- [x] Display bounded download/package progress instead of printing every update.
- [x] Launch signed independent Sunbeam while retaining recovery builtins.

## Current boundary

Pipeline stages buffer text before running the next stage. An infinite producer
is not stopped just because a later `head` needs only a few lines. There is no
native stdin/byte stream bridge, general jobs/process table or full-screen raw
TTY. The no-GPU serial transport has its own bounded ASCII input behavior and
is not equivalent to local keyboard history/navigation. History is session state,
not a persistent cross-reboot database. Bash and nano/Vim are not available ports.

## Remaining steps

- [ ] Define byte streams, EOF/backpressure, stdin/stdout/stderr and cancellation.
- [ ] Add terminal modes/cursor/control contracts before full-screen CLI programs.
- [ ] Integrate process lifecycle/wait/termination without losing caller credentials.
- [ ] Unbundle selected commands after their controlled runtime APIs are available.
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
Historical evidence remains in the [archive](../archive/2026-10-15/PROGRESS.md).
