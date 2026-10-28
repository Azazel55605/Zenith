# Distributor graphical input investigation — 2026-10-28

Owners: Q2/H1 in [master roadmap](../../ROADMAP.md). Controller-only work;
no kernel/client binary changes, new kernel tag or rewrite of kernel/v0.0.27.

## Mouse acknowledgement

The distributor compiled 0.0.27 but Settings initially failed its synthetic
button-edge witness: target (670,644), observed (671,644). The application result
assertion had not failed. A proposed v2 screenshot-feedback correction passed
local deliberate missing-movement controls, but failed again on the server.
Its correction journal oscillated between undershoot and overshoot. Those local
passes did not establish server reliability; relative feedback is withdrawn.

Current observed-motion-v3 preserves the exact destination and ten-second deadline,
sends no corrections and captures read-only running-guest CPU/scheduler state,
input-failure.json and framebuffer on failure. No click, Save or other mutation is
replayed. Tagged helper imports use the current controller policy without changing
the signed binary or tagged assertions. A deliberately omitted return movement now
fails with its expected exact pixel difference and retains diagnostics.

## Settings-to-Terminal handoff

The next server replay passed the original mouse checks, saves, material assertions
and close-during-drag check, then timed out waiting for S_LEVEL. Its CPU samples
caught active compositor work and no runnable command worker. The copied final
screen supplied the missing evidence: Terminal had received `t/l`, reported
`sh: t/l: command not found`, and returned to its prompt. The intended public
fixture command was `sh /root/l`. This was lost injected input, not a stuck settings
mutation or failed translucency service.

The tagged test waits only half a second after native-worker completion before
typing again. A worker status precedes native-surface retirement, so it is not an
input-readiness acknowledgement. The first controller change guarded only S_LEVEL;
the distributor confirmed that handoff passed, then failed at S_SHARED after
closing the keyboard page. The supplied tail shows retirement and a new thread,
but no S_SHARED result; command truncation at this second failure is not yet
confirmed by a framebuffer. The remaining exits had the same unsafe precondition.

The controller now observes a rendered public shell prompt before every known
Terminal fixture command: the one-letter `/root` scripts, the `passwd root`
command that opens the test's hidden prompt, and its public cancellation probe.
Each new native-window lifecycle must also retire before typing. The observation
requires a genuine 2-pixel-wide, 8–32-pixel-tall caret blink and the public prompt's
ANSI green user/host followed by blue cwd on that same row immediately to its left.
Color dominance tolerates antialiased text on solid or material backgrounds in
both appearance modes. The neutral hidden-password prompt, previous scrollback
rows, static cursors and frozen screens cannot acknowledge public readiness.

The first launch and consecutive shell commands now check public readiness too;
only the already completed native-retirement check is reused. A newly opened
native window during observation fails before typing. Every command is dispatched
once; no click, mutation, credential or key sequence is repeated. Actual credential
entry and in-app text editing are outside this hook. Tagged application result,
material, security and filesystem assertions remain unchanged. This establishes
an explicit test precondition; it does not establish a fix for the separate
installed rapid-wheel/drag freeze or underlying mouse/device issue.

## Validation and replay

The operator supplied exact server ELF SHA256
`32a0f8f77117910e2036b06892626acf0a48c343040a8da2f517f71f7cabba4b`,
the failed disposable disk and final framebuffer. Originals are preserved under
ignored `obj/server-027`. A fresh-boot public typing probe passed twelve commands
locally, so that result alone did not reproduce the closing-window transition.
The focused prepared replay uses a separate disk copy and the exact server ELF.
The complete replay passed in 146.522 seconds: S_LEVEL in 0.151 seconds, actual
saves, hidden-input cancellation, keyboard/time-zone/session selectors, captured-UID
profiles, independent upgrade, reboot persistence and an independent ext2 audit.
This is local exact-binary evidence; distributor confirmation remains separate.

After covering all native exits, the full exact-server-binary prepared replay
passed again in 148.411 seconds. It acknowledged seven native-to-Terminal handoffs,
including all three S_SHARED checks and the later Meridian/launcher transitions.
All 300 shared controller regressions passed. Tagged assertions and the kernel
binary remained unchanged; distributor confirmation of this follow-up is pending.

Controller regressions cover retirement-before-dispatch, rendered caret detection,
static/frozen failure, repeated page exits and repeated same-script launches,
first-launch/shell-only bypass, concurrent lifecycle changes, single command
dispatch and untouched credential/other input.
`tools/settings-replay.py --build BUILD_CHECKOUT` selects the newest retained
setup fixture and runs the tagged Settings gate with current controller policy.
It skips kernel compilation and OS installation; the tagged harness may prepare
client packages. Original disks are preserved. Distributor and installed confirmation
remain pending; no local result substitutes for them.

### Hidden-read cancellation evidence

The next operator replay (`/var/tmp/zn-settings-glspqrmo/replay`) passed native
retirement and S_LEVEL but stopped at S_SECRET_CANCELLED before shared selectors.
Only the main thread remained; samples caught material painting/shadows. The
copied final screen confirms the intended `echo S_SECRET_CANCELLED | logger`
was truncated to `ho S_SECRET_CANCELLED | logger`; Terminal reported
`sh: ho: command not found` and returned to its prompt. Ctrl+C had cancelled the
hidden read, but the public command began before input cleanup had completed.

Terminal deliberately clears input/type-ahead on both sides of hidden reads.
Its worker clears the secret flag asynchronously; the next GUI update clears
prepared input and completes the visible prompt transition. A fixed half-second
sleep is not acknowledgement of that boundary. Caret blinking alone is also
insufficient because a waiting password prompt has a caret. This evidence led to
the public-prompt check above, covering ordinary command completion and secret
cancellation as well as native-window focus restoration.

The follow-up controller suite passed all 304 regressions, including a blinking
hidden-prompt rejection, delayed public-prompt recovery, same-row/color ordering,
untouched credential entry, frozen failure and one-time command dispatch. Server
confirmation of the strengthened readiness protocol remains pending.

Local verification of the public-prompt protocol on the exact server ELF:

- Complete prepared Settings replay passed in 155.451 seconds, including hidden
  cancellation, shared selectors, package upgrade, rapid wheel, launcher lifetimes,
  reboot persistence and independent ext2 audit. Original disks were preserved.
- A separate live negative control held `passwd root` at its hidden prompt. A real
  blinking caret did not admit the public command: the observer timed out without
  sending credentials or that command. After one explicit Ctrl+C, it observed
  the public prompt, dispatched the command once and received S_SECRET_CANCELLED.
- The inspected failure image independently classifies its returned shell prompt
  as public and its earlier neutral password prompt as non-public.

Evidence is retained under ignored `obj/server-027-public-prompt` and
`obj/server-027-secret-negative`; source control contains the controller policy
and regression cases. This is local exact-binary evidence, not server acceptance.
