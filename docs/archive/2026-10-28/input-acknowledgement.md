# Distributor input acknowledgement — 2026-10-28

**Superseded correction policy:** a later distributor retry failed at (670,644),
observed (668,644). Its journal showed alternating undershoot/overshoot while
relative corrections accumulated. Observed-motion-v2 is withdrawn; the historical
local passes below did not establish server reliability. Current v3 sends no
corrections and captures running-guest CPU/scheduler state on failure. Use
`tools/settings-replay.py --build BUILD_CHECKOUT` for a focused replay without a
kernel rebuild. Underlying device/frame-delivery cause remains unconfirmed (H1/Q2).

Owners: Q2/H1 in [master roadmap](../../ROADMAP.md). No kernel or client binary
changes, no new kernel tag and no rewrite of kernel/v0.0.27.

## Evidence and boundary

The distributor compiled 0.0.27 but its Settings gate stopped during synthetic
button-release acknowledgement: cursor target (670,644), observed (671,644).
The application result assertion had not failed. An earlier local Debug run missed
the opposite one-pixel witness movement; its unchanged prepared replay and full
Release acceptance passed. These observations establish an intermittent tiny-motion
acknowledgement failure, not the underlying driver/emulator cause or a proven fix
for the separately reported installed wheel/drag freeze.

A focused replay of the unchanged qualified Release binary completed 300 idle
button cycles without failure. Because that did not reproduce the server timing,
transport fault injection deliberately omits one return movement during the same
native test. The repaired policy must recover that exact missing movement, retain
an exact final cursor position and emit exactly one press/release per cycle.

## Controller policy

The rendered-mouse helper keeps the exact destination and original ten-second
position deadline. Only the one-pixel witness move/restore around a button edge may
receive at most two corrections, at least two seconds apart. Each correction is
the remaining delta from the actually rendered cursor. A delayed overshoot is
corrected back to the same target. Ordinary movement commands remain strict.

No button edge, click, Save, command, credential, filesystem operation or failed
application result is replayed. A frozen cursor still fails within the original
budget; missing UI effects/status/pixels still fail their unchanged assertions.
Corrections are recorded in input-motion.jsonl with target/observed/delta/count,
never raw keyboard input or credentials.

Some immutable tagged harnesses explicitly import their own validation-input.py.
The current runner now supplies its input-policy entry points to those imports,
just as it already supplies current authentication/network policy. The signed
binary and tagged application assertions remain unchanged. Controller-only updates
can therefore retry the existing 0.0.27 build rather than mint another kernel tag.

## Validation and handoff

Targeted controller tests cover omitted movement, delayed overshoot, bounded frozen
failure, strict ordinary motion, single button edges and tagged-helper policy
selection. Local results:

- All 245 shared controller regressions passed, including old tagged helper imports.
- Unchanged qualified ELF SHA256: `5a214cb6c65189dc4d817c91cef85eb8f4ca7709f542987c72e44bb19a778437`.
- 300 native button cycles passed with one deliberately omitted return movement:
  600 button commands total, exact final position (1180,140), one observed correction
  from (1181,140) by (-1,0). No button replay. The original policy would fail this
  missing-movement acknowledgement; frozen/overshoot negative cases remain host tests.
- Complete prepared Settings replay passed in 158.241 seconds: actual saves,
  captured UID/private access, inheritance, selectors, cancellation, reboot and
  independent ext2 audit on a copied disposable fixture.
- Full graphical desktop replay passed all 21 checks and independent ext2 audit;
  original disk preserved. The kernel/ISO and application assertions were unchanged.
- Documentation check: 96 files, 43 roadmap work IDs and 43 Meridian pages.

The no-build fixtures and public diagnostic journal are retained under ignored
`obj/mouse-ack-*`. The controller does not alter kernel compilation, signatures,
package results or release tags. Distributor confirmation remains pending until
the operator retries with the updated service checkout.

A reusable no-build reproducer is now available as `tools/mouse-ack-replay.py`.
Its CLI was separately exercised on the same ELF for ten cycles: 20 button edges,
one omitted return movement and one exact correction. It preserves its input disk
and emits `result.json` plus the public correction journal. For example:

```sh
ZENITH_VALIDATION_ACCELERATOR=tcg python3 -B tools/mouse-ack-replay.py \
  --disk /path/to/disposable/setup/disk.img \
  --elf /path/to/matching/Zenith.elf --keep /var/tmp/zn-ack-001 \
  --cycles 300 --omit-return-motion
```

Use a fresh evidence directory and a disposable test fixture with an available
public desktop, never the live installed VM disk. No compilation or installer runs.

### v3 read-only diagnostic validation

The omitted-return-motion negative control now fails at the exact expected pixel
with no corrective input, retains `input-failure.json`/framebuffer and successfully
captures three symbolized CPU samples plus scheduler observations for the matching
0.27 ELF. This is deliberately a failing transport control, not a kernel pass.
`settings-replay.py` selects the newest retained setup fixture and preserves its
input; it provides a focused server run without kernel compilation/OS installation.
