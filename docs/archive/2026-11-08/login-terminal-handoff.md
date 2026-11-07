# Distributor 0.0.48 login Terminal handoff (Q3/W1)

## Evidence and cause

The distributor times out waiting for `LOGIN_META_REMOVE=0`. The final serial
extract contains authenticated Desktop startup, collection #3, then the completed
welcome fade; there is no package-removal output. The operator confirms that
`native/s/failure.ppm` shows Desktop without a Terminal.

The immutable fixture's final `login(root, desktop)` waits only for the Desktop
startup marker, immediately sends Ctrl+Alt+T and sleeps one second before typing.
In the host, the initial `Desktop.Tick` polls input before composing/fading the
first frame, then polls again for normal dispatch. The first poll intentionally
drains authentication input. An early shortcut queued at this boundary can be
consumed without opening Terminal. The server's intervening collection exposes
this window. The fixture's earlier ordinary-user login already waits for the
session-fade record; the final root login did not.

This is a validation-input handoff race, not evidence that package removal ran
and hung. The authentication input drain must remain intact.

## Controller repair

`desktop-fade-public-terminal-v1` applies only to the direct login fixture with
session-fade coverage. Before its existing Terminal shortcut, it requires the
latest authenticated Desktop's completed fade; it sends that shortcut exactly
once. Before returning to the fixture, it observes a focused two-pixel caret
blink on the public ANSI user/host/cwd prompt. It rejects a greeter, a changed
session, ambiguous/dead VM registry or an unready/hidden prompt. Other keys,
credentials and legacy fixtures without this fade contract use their original
transport. No command, package mutation, credential or shortcut is repeated.

The current runner installs this policy for both newly imported and preloaded
monitor helpers while preserving immutable tagged files. Failures retain a
phase-only JSON report, screenshot and read-only CPU diagnostics. Kernel and
release tags remain unchanged.

## Validation boundary

The unchanged `kernel/v0.0.48` login harness passes all 81 checks under TCG with
the repaired controller, including the final root Terminal, metadata removal,
retained dependencies, package verification, clean shutdown, secret-free
transcripts and independent ext2 audit. ELF SHA256 remains
`e3cd34317ce413c4bb8b5caee9f462ed163519e22f927469979ffe598255d8ba`.
Evidence: `/var/tmp/zenith-048-login-handoff` and
`/tmp/zenith-048-login-handoff.log`.

All 547 controller tests pass, including the documentation checks.
Controller regressions cover shortcut-before-fade ordering, one dispatch,
focused/public prompt evidence, session changes, dead/ambiguous registries,
non-interception of credential keys, direct immutable-harness loading and
preloaded/legacy helper compatibility. The distributor's exact build and timing
still need a focused server retry. A saved fixture's random password is not
available for direct reauthentication; the safe verifier runs the single login
gate against the existing kernel with a fresh disposable fixture and credentials.
