# Native launcher frame readiness — 2026-10-29

Owner: Q2 in [master roadmap](../../ROADMAP.md). Controller-only repair; preserve
immutable `kernel/v0.0.28` and all tagged launch, retirement and filesystem checks.

## Failure and concrete counterexample

The distributor Settings gate failed at the start-menu client's retirement
assertion (30.032-second wait, 677.977-second total). The observed mouse used ring
was empty in both samples (`last_used=device_used=1712`); a native launcher worker
was still polling/sleeping. This was not evidence of the earlier stranded virtio
receive queue or a globally frozen compositor.

The same assertion had already failed locally with the exact qualified 0.0.28 ELF
under QEMU 10.0.13 at half a CPU, in `obj/028-registry-fresh`. The retained
`launcher-native-0.ppm` and `launcher-native-5.ppm` screenshots show the Terminal,
not the newly launched native app. The later `wait-failure.ppm` shows the native
System Information window alive. Both Terminal screenshots nevertheless satisfy
the tagged predicate: a 94-pixel strip of `2a2c31` anywhere on screen. This shared
colour is present in the Terminal and launcher menu, so the predicate does not
establish a presented native frame. The test can send its single Escape before
the native window is visible, then wait for retirement of a still-running client.

Direct evaluation of the retained images confirms:

| Screenshot | Old shared-fill predicate | Actual native control/chrome witness |
|---|---|---|
| launcher-native-0 | true | absent |
| launcher-native-5 | true | absent |
| wait-failure | true | System frame at (340,150), Close at (816,570) |

The server now fails the same assertion, but its screenshot has not been inspected
locally. The fix is grounded in this concrete local failure; distributor acceptance
remains a separate boundary. Its reported ELF hash is `8fb262...`; the tool verifies
the distributor's exact ELF against its disk before replaying it.

## Repair and regression contract

The current rendered-input controller recognizes the four named desktop-client
screenshots in the immutable Settings harness: desktop-w/z and launcher-native-0/5.
It takes read-only snapshots until the controlled dark/teal 600x480 fixture has:

- Its opaque, enabled 96x34 Close control at the workspace-relative position,
  including border, inset fill and foreground text.
- Matching focused host borders on the unoccluded top/left/right edges and the
  host Close X, at coordinates derived from that native control.

The bottom border is not a focus witness: the real panel's shadow can tint it
when a cascaded window reaches the panel. A local candidate check caught this
legitimate occlusion before publication; the final regression includes it. No
pointer destination tolerance or input feedback correction was introduced.

Other capture names retain their original behavior. A stale/frozen Terminal or
menu cannot permit the next Escape. Observation is bounded by the existing
20-second frame-readiness budget; input is never replayed. Missing presentation
retains the failing screenshot and public readiness diagnostic. The underlying
tagged assertions still require one Escape to cause retirement, followed by clean
poweroff and independent ext2 checks; none is skipped or manually marked passed.

Regressions render the actual shared C UI code and generated font atlas, rather
than manufacturing a button shape matching the observer. They exercise moved
frames, stale Terminal/menu fills, incomplete/disabled controls, missing text,
unfocused chrome, panel-shadow occlusion, one Escape after readiness and no input
on a frozen frame. New no-build fixture selection tests preserve the source disk.

## Focused no-build replay

```sh
PATH="$PATH:/usr/sbin:/sbin" ZENITH_VALIDATION_ACCELERATOR=tcg \
python3 -B tools/desktop-launch-replay.py \
  --build ~/.local/state/zenith/distributor/build-kernel-v0.0.28 --cycles 5
```

This selects the latest retained Settings desktop disk and matching build ELF,
checks kernel identity, and boots a separate copy. It tests Welcome and System
from the real start menu once each per planned cycle, observes the full native
frame, sends one Escape and requires retirement. It stops on first failure; cycles
are explicit independent test cases, not event retries. It then observes the public
Terminal prompt, invokes the fixture's existing poweroff script, requires unmount
and clean exit, and audits the copied ext2 root. No kernel compile, OS install,
package replacement or repeated full Settings preparation is required. Explicit
`--disk` and `--elf` support still-active short workspaces.

## Local qualification

The final observer passed 20 launcher invocations (10 Welcome/System cycles) on
the retained failed fixture, using QEMU 10.0.13 at a 0.5 CPU quota: 80 button
commands, 20 Escape commands, clean poweroff and independent ext2 audit in 89.900
seconds. The complete unchanged Settings desktop sequence also passed from a
fresh copy of its prepared baseline in 180.263 seconds, including launchers,
upgrade, reboot persistence and ext2 audit. Neither run compiles a kernel.

A superseded intermediate local Settings run was explicitly stopped after the
observer changed; it is excluded from acceptance. A first focused run rejected
the real panel-shadowed bottom border, leading to the refined unoccluded-chrome
check above; it is also not a passing run. Evidence directories remain under
ignored `obj/028-launch-ready-*` and `obj/028-settings-ready-final`.

These local runs use ELF
`5fc6d12aa26a2acf23b859a8fe2fe0c29171f287c7911611e1b1cdd36659477b`.
The private cached Docker wrapper uses the distributor's exact QEMU 10.0.13 and
matching OVMF, not a replacement of system QEMU. Launcher-process counters are
not actual QEMU CPU measurements.

The stricter quarter-CPU replay also passed 20 launches (10 cycles), 80 button
commands and 20 Escape commands, clean poweroff and independent ext2 audit in
203.566 seconds (`obj/028-launch-ready-quarter/result.json`). Together, the final
half/quarter-CPU probes cover 40 launches with no retries, input correction or
retirement-deadline increase. All 347 common controller regressions passed in
30.704 seconds; documentation validation passes 101 files. The real C renderer
also supplies a disabled Close control as a negative test.

## Operator distributor confirmation

On 2026-10-29 the operator confirmed that the requested `--build ... --cycles 5`
probe passed after pulling `51a1138`. That command verifies the distributor ELF
against its retained Settings disk, copies it, tests ten Welcome/System launches
with single Escape/retirement assertions, and requires clean poweroff plus the
independent ext2 audit. This is operator-reported success; the detailed result log
and timing were not supplied.

This focused result cannot substitute for the normal parallel release pipeline or
installed-VM acceptance. Retry unchanged kernel 0.0.28 with the current controller;
no new kernel tag is needed for the frame-readiness repair.
