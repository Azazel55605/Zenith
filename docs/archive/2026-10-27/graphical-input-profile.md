# Distributor graphical-input failure

## Evidence

The 0.0.24 distributor run compiled and completed G_INTERACTIVE with status 0,
but failed the subsequent focus-filtered event assertion. The operator's retained
serial extract has GUI_POINTER=0, GUI_KEY=3, GUI_FOCUS_IN=1, GUI_FOCUS_OUT=0.
Thus the intended pointer/focus transitions were not observed: the extra key was
the unfocused-test F12 arriving at the still-focused client. This is not evidence
that keyboard focus filtering failed after a successful focus transition.

The tagged fixture relied on implicit PS/2 mouse input and emulator selection.
QEMU's info mice interface confirms that merely attaching a virtio mouse does not
necessarily make it active. The exact server-side PS/2 failure mechanism remains
unconfirmed. Avoid another full kernel build as the first diagnostic step.

## Narrow correction

The current validation controller attaches and explicitly selects QEMU Virtio Mouse
only for the protected graphical harness's monitored virtio-GPU VM. It discovers
the emulator-assigned mouse index and verifies the active selection; missing or
ambiguous devices fail. Direct current-harness runs use the same idempotent profile.
Other gates retain their device profiles and PS/2 coverage. No kernel, driver,
NuGet package, protected client binary or existing release tag is changed.

The current graphical helper now requires a real pointer acknowledgement and focus
loss before injecting F12, and restored focus before the two intended keys. Known
serial trace interruptions are parsed without treating command echoes as events.
It retains the exact two-key assertion and emits bounded phase/count/geometry
JSON and a failure screenshot. Input and guest mutations are never replayed to
manufacture success.

A no-build diagnostic command copies the retained disposable fixture, verifies
its boot kernel against the supplied ELF and replays just the graphical sequence.
The default input-only mode does not install packages. --full also runs the
retained signed probe upgrade and final ext2 audit, changing only the independent
fixture copy. Neither mode compiles/reinstalls the OS, modifies the original disk
or signs anything. --mouse ps2 is a comparison mode, not an automatic fallback.

## Local validation and boundary

- No-build replay passes with explicit virtio and with legacy PS/2 on local QEMU
  11.1.2, using the unchanged qualified 0.0.24 kernel/fixture. This does not reproduce
  or prove the server's legacy mouse behavior.
- The unchanged kernel/v0.0.24 graphical harness passes all 48 checks and independent
  ext2 audit through the corrected controller input profile (133.388 s).
- All 223 controller regressions pass, including 12 input-profile/parser tests for
  index discovery/verification, scope, cleanup, idempotence and event parsing.
- The current hardened full fixture also passes all 48 checks and ext2 audit
  (133.226 s). Kernel and protected-client sources are unchanged from the tag.
- Distributor no-build replay and subsequent delivery remain pending. Preserve the
  original failed fixture and exact kernel while comparing the input transport.

## Distributor replay

After pulling the corrected controller while idle:

```sh
PATH="$PATH:/usr/sbin:/sbin" ZENITH_VALIDATION_ACCELERATOR=tcg \
python3 -B tools/graphical-input-replay.py \
  --disk ~/.local/state/zenith/distributor/build-kernel-v0.0.24/obj/distributor/release-kfm3rxzl/native/g/desktop/disk.img \
  --elf ~/.local/state/zenith/distributor/build-kernel-v0.0.24/output-x64/Zenith.elf
```

Report its result and any Graphical input failure JSON. This isolates actual mouse
transport on the server without another build. If it passes, retry the existing
0.0.24 release with the updated controller; do not move the immutable tag.

## Follow-up: correctly positioned cursor, unobserved click

The distributor no-build replay on QEMU 10.0.13 verified the original server
kernel SHA256 `078261ccf5d6bf96329361a5b94ef7d9f87efc7a2096be5ef90a89ce509c1ade`.
Explicit virtio selection delivered the first pointer event, but focus loss still
failed. Read-only inspection of the retained failure frame located the cursor at
(313,180), exactly the intended terminal coordinate. Thus movement/coordinates
were correct; the next button edge was not observed.

The existing desktop samples current button state in Input.Poll. A fixed 150 ms
press followed by release can collapse to the released state before the next
render/input poll on the slower distributor. The controller now observes rendered
input frames: exactly one button transition is sent, followed by a one-pixel move
and return while holding that state. Both cursor positions must be rendered before
the call returns. Release is acknowledged the same way. The net pointer position
is unchanged, title drags return to their original position, and no button/key or
guest command is retried. A missing frame fails with actual/expected coordinates.

This pacing is installed only for the graphical validation monitor; it adapts the
unchanged old tagged harness as well as current/direct helpers. It does not change
kernel input semantics or claim to fix legacy mouse hardware. The original fixture
and tag remain preserved. Distributor confirmation of this sampled pacing remains
required. Local graphical replay and all 48 checks of the unchanged tagged gate
pass with an independent clean ext2 audit (137.135 s). All 230 controller tests
pass, including 19 input-profile/parser tests. Kernel/client source and the release
tag remain unchanged.

## Distributor replay confirmed

On 2026-10-27 the operator reports both native pixels/pointer acknowledgement/
focused-key isolation and the complete no-build virtio replay passed on QEMU
10.0.13/TCG. The verified server kernel remains
`078261ccf5d6bf96329361a5b94ef7d9f87efc7a2096be5ef90a89ce509c1ade`;
evidence is retained at `/var/tmp/zn-gui-_xrh__qi`. The original fixture disk is
preserved. This confirms the sampled input correction on the actual distributor.
It does not yet establish a complete release retry, signing/delivery or installed
acceptance. Retry the existing immutable 0.0.24 release with the updated controller.

## Follow-up: visible-frame readiness and full desktop replay

The distributor retry passed G_INTERACTIVE but failed `close fixture pixels`.
The operator's close.ppm had no blue, green, client-background or black pixels;
the serial log showed the second client presented, remained ready, and opened/
focused without exiting. A surface/open log is not proof that the composed frame
has reached the display. The fixture's fixed 0.5 s delay was not a sufficient
readiness contract on this host.

The controller now waits for complete PPM dimensions/bytes and at least 5000 aligned
probe-blue pixels for client.ppm, close.ppm and minimized.ppm. It captures fresh
frames within a bounded deadline without replaying guest input/commands. Partial
snapshot files are waited out rather than interpreted as missing application pixels.
Other screenshots remain observations and do not acquire this sentinel condition.

`graphical-input-replay.py --full` selects the retained harness's desktop-only
assertions after booting a copied prepared fixture. It validates the expected
script/desktop/audit boundaries before starting QEMU and excludes compiler/key
creation/live installer preparation. It checks input, close, minimized cancellation,
quota recovery, signed probe upgrade, UID denial and independent ext2 audit together.
Its full mode changes packages only inside that copied disposable disk. This
avoids discovering each later graphical assertion through another full build.

Local evidence (unchanged 0.0.24 kernel):

- Original tagged graphical gate: 48 checks and ext2 audit pass with visible-frame
  capture readiness (138.242 s).
- Complete no-build desktop replay: 21 desktop checks and ext2 audit pass, including
  final complete-file readiness; original fixture preserved.
- All 237 controller tests pass, including 26 input/parser/replay tests. Boundary
  extraction works without release tag refs being present in a shallow CI checkout.

Distributor full replay remains pending. After pulling the controller, add --full
to the replay command above, retaining the original failed fixture and matching ELF.
Do not start another expensive release retry until the whole desktop replay passes.

## Full distributor replay confirmed

On 2026-10-27 the operator reports the full no-build desktop replay passed on
QEMU 10.0.13/TCG against unchanged server kernel SHA256
`078261ccf5d6bf96329361a5b94ef7d9f87efc7a2096be5ef90a89ce509c1ade`.
All 21 desktop checks and the independent ext2 audit pass: pointer/focus isolation,
close, minimized cancellation/retirement, quota recovery, signed probe upgrade to
0.1.1, different-UID denial and matching-session access. Evidence is retained at
`/var/tmp/zn-gui-hbvbkxh8`; the original fixture is preserved. This supersedes the
pending replay note above. Complete release retry/signing/delivery and actual
operator-VM installation remain separate acceptance steps.
