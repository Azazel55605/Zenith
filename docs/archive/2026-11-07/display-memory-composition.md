# 0.0.47 display, memory and parallel composition follow-up (Q3/M2/D2/S3/C4)

## Installed evidence and diagnosis

The operator reports fresh 0.0.46 idle use growing from 201264 KiB to 596440 KiB
in approximately five minutes, without Observatory. Growth remains after 0.0.47's
rendering improvements. A disposable pressure replay on the current prebuilt
kernel reclaimed 4,945,667 objects: used physical pages fell from approximately
420 MiB to 117 MiB. Physical pages in use include uncollected garbage; this proves
reclamation works for the controlled load, not the absence of every possible leak.
The old collection policy waited for only one eighth of physical memory to remain.

The installed screenshot shows VMware SVGA II at 1920x1080, 100%, UTC selected,
no successful NTP synchronization, and native applications failing on maximize.
The kernel admitted whole-background material area only up to 1,920,000 pixels,
while the toolkit admitted 1920x1200. The actual validator rejects 1920x1040.
The VMware-local-RTC guess also used the guest's selected display time zone,
which cannot establish the host RTC's basis. Boot menu resolution was never set.

## Implementation

- Allocation debt of 32 MiB, as well as the physical reserve, now requests collection.
  Actual collection counters reset debt, including externally requested collections;
  refused attempts retain debt and retry at most once per second. The taskbar
  avoids rebuilding unchanged minute strings. `free --gc` exposes collector
  counters; root-only `free --collect` reports the actual attempt and resulting
  counters rather than assuming the request succeeded.
- Framebuffer ABI 1 adds calls 645 (grow one private invocation-owned NX buffer)
  and 646 (decline resize without ending the client). New clients negotiate this
  capability before large viewports are offered; older clients retain the former
  viewport limits. Pages are captured by the normal process lifetime and freed
  on exit/fault/cancel. Sizes, page mappings, caller ranges and resource reserve
  are checked; refusal preserves the previous viewport. Client binaries no longer
  reserve a maximum-sized static frame. A 4K backing-buffer probe exercises the
  real page mappings and presents pixels from the high end of that allocation.
- Material area validation follows the negotiated frame rather than the stale
  constant. Physical mode admission uses actual SVGA bounds and both VRAM planes.
  Standard larger modes extend the pinned driver's per-canvas catalogue only
  after these checks. Arithmetic limits (16384 per axis, 32 Mi pixels) and byte
  budgets remain security/resource bounds, not a 1080p UI design limit. Material
  cache budgets scale with usable memory and retain bounded solid fallbacks.
- VMware GETTIMEFULL supplies a guarded boot-time UTC sample without coupling it
  to the guest display zone. Explicit `rtc=utc`/`rtc=local` overrides remain, and
  are carried through menu rewrites. The clock source is visible in timedatectl.
  Host time is unauthenticated; configured NTP remains an independent source.
- Both Limine's `interface_resolution` and each entry's framebuffer `resolution`
  are published. Selected physical dimensions are saved in both installed menus
  using the existing checked boot store and preserved during kernel publication.
  Initial SVGA autofit precedes the boot splash; firmware dimensions, rather than
  the driver's default dimensions, define the safe shutdown hand-back.

## Phase 3 boundary

The foreground compositor now shares the existing private AP tile ownership
channel with blur. Submission copies immutable, bounded foreground/background
snapshots. APs run fixed integer premultiplied composition, never receive managed
scene pointers and never mutate UI/device state. The BSP also computes tiles.
A retained private result is published only when the whole clipped operation
succeeds; failure cannot blend an already-published tile twice. Busy/small/single-
CPU paths fall back immediately; incomplete native payloads remain reserved.

`fps parallel on|off` controls blur and foreground composition together, and `fps`
reports their separate AP tile counters. The real full-scene parallel benchmark
continues to compare exact pixels. Packet handling/download throughput is separate.

## Validation boundary

Source `d03b4b6` is locally qualified: 887 host tests, 536 controller tests,
eight runtime-adapter tests and all 15 individual mandatory VM gates pass.
The controlled candidate workload triggers three debt collections within 18 s of load;
the final forced collection returns use to 122220 KiB against a 123044 KiB
starting baseline on the final repaired ELF. Three pre-runtime-repair guest
full-scene comparisons are pixel-identical
(scalar 25.5–26.7 ms, parallel 21.2–25.2 ms). The final ELF also passes three exact
comparisons with positive AP blur/composition counters; timings under concurrent
qualification vary (scalar 22.1–31.5 ms, parallel 21.5–25.2 ms). This does not
establish a consistent performance gain on the installed VMware profile. The
first matrix passed 81 login/session checks, then exposed
an omitted assembly admission range for calls 645/646 before Settings launched.
The BSP whitelist and all seven derived private bridges now admit those calls;
the source hash was updated only after auditing this boundary-only change.
Early Settings bounds checks and 45 bridge/queue regressions cover the repair.
Host tests cover 1080p/4K material acceptance,
resize refusal, boot-resolution preservation and time-zone-independent correction.
Production native queue and C# bridge tests compare exact alpha pixels, clipped
strides, source immutability, guard bytes and bounded fallback. The graphics gate
requires real production AP composition and the 4K private-buffer probe.
The fresh matrix then exposed a title-gesture bug: a completed drag remained
eligible as the first title double-click. A quick second grab maximized Meridian
while the fixture was testing close-during-drag; separating the gestures made that
check pass. Actual pointer motion beyond four pixels now clears title-click
history. The original unmodified Settings regression passes against this repair.

The graphics gate then caught a real post-collection fault in runtime enum/reflection
metadata. The pinned `FreeWeakHandles` recycled weak/dependent slots while their
owners still retained the handle address, and treated immutable frozen keys as
unmarked. The copied-runtime adapter now clears dead targets without recycling
owned slots, recognizes frozen keys as live and handles null targets. Explicit
owner release still frees a slot. The adapter preserves the existing cleanup
scope; upstream finalization and `WeakTrackResurrection` remain separate runtime
limitations. A regression executes the real adapted methods for null, marked,
unmarked and frozen targets and repeated cleanup of weak/dependent slots; 33
focused runtime/GC/replay checks pass. The graphics gate now forces a collection
before Polaris launch. Subsequent qualification evidence uses disk-backed
`/var/tmp` storage after completed disposable fixtures filled the host's `/tmp`.
This storage interruption is distinct from the captured guest page fault.
The final native qualification used one frozen ELF:
`e3cd34317ce413c4bb8b5caee9f462ed163519e22f927469979ffe598255d8ba`.
The all-in-one run stopped on a setup HTTP fetch returning status 1 before the SMP
workload. Its already-running gates finished; a fresh full SMP fixture passed,
and the seven not-yet-started gates passed separately. This is evidence for all
15 individual gates, not a claim that the original all-in-one run succeeded.
The original failure is retained. The separate graphics regression passes all 87
checks after forced collection, and an additional 1080p VMware-SVGA test maximizes
and closes Settings, desktop-tools, Observatory and Polaris with whole-app blur
both disabled and enabled. The real 4K backing-buffer probe passes.

Evidence is in `/var/tmp/zenith-047-qualified`, `/var/tmp/zenith-047-pending`,
`/tmp/zenith-047-smp-recheck.log` and the hash manifest
`/var/tmp/zenith-047-results.json`. No distributor, installed VMware or successor-
release acceptance is claimed. Actual 4K physical SVGA, VMware host clock, Limine
menu/splash and long-duration installed idle-memory observations remain Q2/H1
operator acceptance checks.

Protocol references: [VMware GETTIMEFULL](https://github.com/vmware/open-vm-tools/blob/master/open-vm-tools/services/plugins/timeSync/timeSync.c),
[Limine v10 configuration](https://github.com/Limine-Bootloader/Limine/blob/v10.x/CONFIG.md).
QEMU's VMware-SVGA device caps its advertised mode at 2368x1770; 4K physical SVGA
validation therefore remains a VMware/device acceptance check, distinct from 4K
buffer/validation tests.

## 0.0.48 release qualification

The subsequent fresh full pre-tag acceptance passes on source `2e93304`, using
the same ELF/ISO hashes as the repaired candidate. All 15 mandatory VM gates pass
in one run with successful-result reuse disabled and seven workers admitted by
the host CPU/RAM budget. The run also passes 887 host tests, 536 controller tests
and eight copied-runtime adapter checks. Evidence: `/var/tmp/zenith-048-pretag`;
controller output: `/tmp/zenith-048-pretag.log`. This resolves the previous
all-in-one batch acceptance gap without changing or hiding its failed fixture.

The release set is `kernel/v0.0.48`, `zenith-settings/v0.10.2`,
`zenith-observatory/v0.3.1`, `zenith-polaris/v0.3.3` and
`zenith-desktop-tools/v0.4.3`, all at qualified source `2e93304`. The four native
payloads also build at these exact release versions and their manifests require
`zenith-native-framebuffer-abi` 1.0. Tags request the distributor build/sign/publish
pipeline; they do not claim delivery or installed VMware acceptance.
