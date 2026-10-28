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

The tagged test waits only half a second after S_UI=0 before typing again. That
worker status precedes native-surface retirement, so it is not an input-readiness
acknowledgement. The controller now guards this specific public fixture handoff:
wait for the native retirement log, then observe a rendered focused Terminal caret
blink before injecting the command once. The blink must change exactly a solid
2-pixel-wide, 8–32-pixel-tall rectangle; static, incomplete and frozen frames fail.
No key or application command is repeated. Credential/password typing is untouched,
and the observer runs only before the known `sh /root/l` script. This closes an
unsafe test precondition; it does not prove the underlying mouse/device issue or
the separately reported installed wheel/drag freeze is fixed.

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

Controller regressions cover retirement-before-dispatch, rendered caret detection,
static/frozen failure, single command dispatch and untouched credential/other input.
`tools/settings-replay.py --build BUILD_CHECKOUT` selects the newest retained
setup fixture and runs the tagged Settings gate with current controller policy.
It skips kernel compilation and OS installation; the tagged harness may prepare
client packages. Original disks are preserved. Distributor and installed confirmation
remain pending; no local result substitutes for them.
