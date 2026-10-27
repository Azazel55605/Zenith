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
It never installs packages, compiles a kernel, modifies the original disk or signs
anything. --mouse ps2 is a comparison mode, not an automatic fallback.

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
