# Audio devices and applications (H2)

Owner and progress: [master roadmap H2](../ROADMAP.md). First scope: actual output and
input devices, default selection, volume/mute and application streams in Meridian.
System sound packs, event selection and previews now use this mixer. [Appearance integration](APPEARANCE.md)

## Implemented contract

- Intel High Definition Audio PCI controller with an analog stereo 48 kHz / 16-bit
  PCM route. Enumerate codec widgets and bounded connection paths; never invent a
  device when no route exists. This matches the user's VMware HDA model; QEMU's
  intel-hda with hda-duplex supplies independent playback/capture qualification.
- One selected output and input, shared by all application streams. Installed systems save each endpoint's level and mute plus the preferred input and output in `/etc/zenith/audio-devices.conf`. The key includes PCI location, controller and codec model, codec address, direction, pin and pin configuration. A missing or changed endpoint falls back to the driver’s default. Returning hardware can recover its saved settings. Recording still requires its usual permission.
- Live boots and per-app stream controls last for the session. Further controllers, independent routing, jack hot-plug and more PCM formats remain H2 work.
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

## Release qualification

Fresh clean Release acceptance completed on 2026-11-18 at
`5f3ba1fe6f7a509f342a40aeb0db356ffde25224`:

- All **1095 host tests**, **588 controller tests**, runtime-adapter tests and
  **23 fresh native VM gates** passed. The matrix used six supported workers with
  successful-result reuse disabled. Evidence: `/var/tmp/zn-audio-release-068-qualified`.
- The HDA gate passed signed installed native playback/capture on four CPUs,
  device selection, invalid ranges/frames, stale controls/handles, capture denial,
  fault cleanup and no-HDA boot. Capture used QEMU's silent input backend, without
  accessing the host microphone. Separate actual WAV output/mute checks passed.
- Full qualification exposed an existing Horizon HZ5 input bug: QEMU and the guest
  mouse manager received the middle press, but `Desktop.Tick` skipped stationary
  middle edges. The shared `InputActivity` predicate now dispatches them without
  pointer motion, preserving release/hover animation behavior. Six host regressions
  and Horizon's entire **169-check** native gate passed, including middle-open and
  middle-close. No test assertion, destination or timeout was relaxed. This is a
  kernel fix; it does not require a rebuilt Horizon package.
- Release ELF SHA256:
  `9e3d041e6f2ed242e3037e4772d4feda46e3144a96c6d3d80f8923cd2250a20a`.
  ISO SHA256:
  `56da26dbe818d3fc75cd03ce58bf3054c4a71be9f5ace8b55fd2e93e52f54847`.
- Audio acceptance is mandatory gate `f` in the shared CI/distributor plan. Its
  gate-list controller regression also covers retained audio evidence. Host UI
  checks exercise actual Sound widgets in both themes under sanitizers.

Release set at the qualified source commit: **kernel/v0.0.68**, **zenith-settings/v0.12.0**,
**zenith-man-pages/v0.1.8**. Qualification notes may follow in a documentation-only commit.
On 2026-11-18, the operator confirmed audible playback with kernel 0.0.68 in
VMware Workstation. Auto Detect alone initially returned `No supported audio
device`. Keeping Auto Detect and explicitly setting `sound.virtualDev = "hdaudio"`
in the powered-off VM's `.vmx` resolved detection and playback. VMware microphone
capture and other physical hardware remain unverified.

H2 still owns independent device routing, additional formats, extra controllers and installed capture checks. U14 owns sound packs and their desktop events.
