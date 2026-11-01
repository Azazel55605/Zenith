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

### Polaris design

Polaris is the native Terminal, a mix of Konsole and kitty. [Concept](../design/terminal-concept.svg).

**The boundary.** The shell stays in the kernel, and there is no fork/exec, so a client cannot start one.
A kernel-owned terminal session holds the shell, its job thread, type-ahead, hidden input and output. The
client sends key events and receives output. This is not a command service: a session runs with the
caller's credentials and takes the same keys a person would type, so it grants nothing the caller lacks.

**Line editing stays in the session.** Prompt, history, completion, multi-line input, type-ahead and
hidden prompts already work there and are the risky part. The client gets the prompt, edit text and
cursor as state, never the text of a hidden prompt, and owns only the grid, scrollback, selection and chrome.

**Recovery.** The kernel Terminal window stays, as a host of the same session code. It remains what a
missing or failing Polaris falls back to, and local console and serial are unchanged.

**Problems to solve, found in the current kernel:**

| Limit | Effect on Polaris | Plan |
|---|---|---|
| Native programs get 8 MiB of address space, 7.7 MiB of it a full-size frame | No room for scrollback | Raise the bound behind a new package capability |
| Each process page allocates 8 KiB for alignment | Twice the memory for every native program | Allocate aligned pages |
| A running native program blocks `reboot`, `poweroff` and package work | With Polaris open, you could not run them from it | A program holding a terminal session stops counting; other apps, such as Meridian, still block |
| The UI library has no monospace font, and a program is at most 256 KiB | Glyphs must fit the image | Hack atlas of ASCII, Latin-1 and box drawing |
| One native program runs at a time on the raw entry gate | A busy child slows Polaris drawing | Measure in P3; output is batched per frame |

**Stages**, each qualified separately:

1. **P1 session extraction:** move the session out of `TerminalWindow`, with no visible change. Every gate
   that types into Terminal covers it.
2. **P2 session service:** calls 630..635, 32 MiB images behind `zenith-native-memory-abi`, aligned page runs and the blocking rule. `gui-probe --terminal` drives a session in the graphics gate, including a hidden prompt.
3. **P3 single pane:** the zenith-polaris package with scrollback that reflows, selection, clipboard and
   smooth scrolling. Installed, it is what the launcher, Ctrl+Alt+T and `open Terminal` give you. Ctrl+Alt+Shift+T
   and `open recovery-terminal` always give the built-in one. The kernel keeps the clipboard: calls 636 and 637 set
   it and paste it into the client's own session, so a client cannot read it, and neither works at a hidden prompt.
   Limits: ASCII glyphs only, others show `?`; solid background, no materials; a fixed "Polaris" title; 1 MiB cells
   of scrollback; plain PgUp and PgDn scroll, as in the built-in one. Selection and clipboard have host tests only
   for the model, not a VM check. Terminal windows the desktop opens itself at first boot stay built-in.
4. **P4 tabs and layouts:** several sessions, up to eight, with Tall, Columns and Rows.
   Also from operator testing of 0.1.0: the launcher should say Polaris once it is installed, not Terminal. Pasting
   text with line breaks must not run it: it stays editable until Enter, as multi-line paste. This also applies to the
   built-in Terminal, which shares the session code. Move the helpers listed under the toolkit gaps into `userland/ui`.
5. **P5 navigation:** find bar, profiles, prompt marks, hints and the scrollback rail. Inline images are not planned yet.

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
