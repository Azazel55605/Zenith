# Audio devices and applications (H2)

Owner and progress: [master roadmap H2](../ROADMAP.md). First scope: actual output and
input devices, default selection, volume/mute and application streams in Meridian.
System sounds and additional panels are explicitly subsequent work.

## Implemented contract

- Intel High Definition Audio PCI controller with an analog stereo 48 kHz / 16-bit
  PCM route. Enumerate codec widgets and bounded connection paths; never invent a
  device when no route exists. This matches the user's VMware HDA model; QEMU's
  intel-hda with hda-duplex supplies independent playback/capture qualification.
- One selected output and input, shared by all application streams. Choices/gains
  last for the current boot. Additional controllers, per-app physical routing,
  remembered preferences, jack hot-plug and other PCM formats remain subsequent
  H2 work; unsupported hardware displays its actual absence.
- A BSP-owned polling thread services fixed eight-period DMA rings. No audio IRQ,
  per-frame allocation, UI-loop processing, unbounded backlog or device I/O on APs.
  Each of sixteen invocation-owned streams has a 32 KiB ring. Playback is mixed
  with saturation; capture fans out only to authorized readers, discarding oldest
  complete frames when a reader falls behind. Nonblocking transfers return EAGAIN.
- Native calls 661..666 and [client header](../../userland/include/zenith_audio.h):
  versioned device/stream inventory, revisioned live controls, open/write/read/close.
  Full user ranges are checked before copies. Handles cannot cross process owners.
  Input requires root or the explicit `org.zenith.audio.record` Authorization action;
  its administrator dialog is the initial recording permission mechanism. Revocation,
  process exit/fault/cancel and a session change clear queues and stop capture.
  Other users' streams are hidden from non-root inventory and controls.
- Meridian Sound uses shared themed widgets and live meters. Output tests require
  an explicit click; there are no boot/event sounds. Input meters reflect actual
  authorized recording, never background microphone access just to show a meter.

## Qualification

Local qualification completed on 2026-11-18:

- Pinned Cosmos 3.0.89 x64 kernel publish succeeded. Tested ELF SHA256:
  `c90b706e0d7db0d5336a69a65e41f2585749736e05ff42d6677fb18ed8624ec9`.
- All 1089 host tests passed, including mixer saturation/gain/mute, bounded queues,
  capture fan-out/overrun, stream ownership/generation, explicit recording grants
  and the reviewed Meridian inventory. 68 affected controller/documentation tests
  and all 11 UI/bridge tests passed (actual widgets under sanitizers, both themes,
  package headroom and the audited seven AP bridges).
- Fresh `validation-runner.py tools/native-audio-smoke.py /var/tmp/zn-audio-final`
  passed in 96.674 s with TCG: signed installed native PCM on four CPUs, input and
  output selection, malformed pointers/frames, stale controls/handles, microphone
  denial and cleanup after a real native CPU fault. Capture used QEMU's silent
  backend; no host microphone was accessed. Separate WAV output reached peak 3200;
  the muted app's entire recording was zero. No-HDA installed boot also passed.
- The audio gate is part of the shared CI/distributor native plan (`f`), including
  older-tag compatibility through the existing file-presence check.
- Host-rendered Sound page evidence is in `/tmp/zenith-audio-ui`. This verifies
  layout/controls separately from the HDA/native QEMU evidence.

Publication qualification on 2026-11-18 at `7103973`:

- Fresh Release acceptance retained at `/var/tmp/zn-audio-release-068-publish`:
  all 588 controller regressions, 1089 host tests, the runtime adapters and 22 of
  23 native VM gates passed. The audio gate passed in 85.6 s. Release ELF SHA256
  `00fa6ae0d6a5104b256d0d6d39475d6293eaca5bca2a4232d2a4a30c01c1546b`.
- The remaining Horizon HZ5 gate failed its middle-click/new-tab check at
  `tools/horizon-smoke.py:306`; the retained image shows Documents beneath the
  pointer and one tab. Its input-delivery diagnosis belongs to A1/Q2. No assertion,
  input destination or timeout was relaxed, and no middle click was replayed.
- The release-controller expectation was corrected to include the new `f` audio
  gate. An initial unchanged Aurora host-test timeout passed both isolated and
  subsequent full controller runs with the original 120-second limit.

Proposed tags `kernel/v0.0.68`, `zenith-settings/v0.12.0` and
`zenith-man-pages/v0.1.8` remain withheld until release acceptance is resolved.
Installed VMware listening, input pickup and real codec/jack behavior remain
operator acceptance.
Later formats, persistent choices, independent routing, additional controllers
and system sounds are explicitly outside this first device/application stage.
