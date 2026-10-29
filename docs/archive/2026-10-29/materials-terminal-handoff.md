# Materials-to-Terminal handoff — 2026-10-29

Owner: Q2 in [master roadmap](../../ROADMAP.md). Controller-only protocol repair;
keep immutable `kernel/v0.0.28`, its binaries and all tagged materials assertions.

## Operator failure and boundary

The operator reported native-materials-smoke reaching the final shutdown wait,
which failed at 60.020 seconds (220.791 seconds total). The retained desktop log
at `release-v1dxzjnl/native/m/desktop/serial.log` contains `MATERIALS_EXIT`,
`UI_DONE=0` at 21.384 and native-surface retirement at 21.681, followed by another
scheduled worker. No filesystem-unmount message appeared. QEMU was terminated by
the Python harness after the assertion. The visual checks and client exit had
passed; this log alone does not establish what the final GUI command received or
whether a shutdown worker subsequently blocked.

The tagged gallery waits a fixed 0.5 seconds after `UI_DONE=0`, then types
`sh /root/ui010-p`; that downloaded fixture script contains `poweroff`. As the
previous Settings failures established, worker completion is earlier than all
GUI input cleanup and public shell readiness. The existing controller handoff
observer covered only Settings and its one-letter scripts, so the materials
wrapper and versioned gallery scripts bypassed it.

## Repair

- Add an explicit gallery protocol to the same public Terminal observer. The
  controller installs it for direct ui-gallery and the native-materials wrapper.
- Observe the first public prompt before launching the gallery and, after exit,
  native-surface retirement plus a focused blinking public prompt before typing
  its poweroff script. Restrict interception to the versioned fixture's `-u`/`-p`
  scripts. Other commands and credentials retain their original behavior.
- Type the requested script once. Missing retirement, a frozen frame or a hidden
  prompt never causes typing or command replay. Worker/exit markers do not prove
  shell readiness, successful unmount or poweroff.
- Keep the tagged render, material/opaque-control, movement, `UI_DONE=0`, 60-second
  unmount, process-exit and independent ext2 assertions unchanged.
- If a gallery wait still fails, capture a read-only screenshot plus bounded
  CPU/scheduler/mouse observations from the actual parent monitor before cleanup.
  Captures cannot change acceptance.

This closes a demonstrated protocol-coverage gap; it does not prove truncated
input was the exact cause of the operator's shutdown timeout. A QEMU 10.0.13
half-CPU local baseline without the guard passed in 53.080 seconds, so this
particular server timeout was not reproduced locally.

## Validation

Regressions cover delayed public prompt and retirement, frozen/unretired refusal,
exactly-once dispatch, first launch, unrelated commands/credentials, wrapper and
separate-checkout installation, and failure-only gallery diagnostics. Native runs
use the unchanged qualified 0.0.28 ELF
`5fc6d12aa26a2acf23b859a8fe2fe0c29171f287c7911611e1b1cdd36659477b`,
QEMU 10.0.13 with a 0.5 CPU quota and disposable copies. The original disks and
system QEMU are untouched. Container-wrapper process counters are not actual
QEMU CPU timings.

- All 336 common controller regressions passed in 33.087 seconds.
- Fresh dark-material installation/setup/desktop/clean shutdown passed in 96.390
  seconds, including unchanged kernel identity and independent ext2 audit.
- Light-material setup/desktop/clean shutdown passed in 48.628 seconds from a
  separate clean installed baseline copy, including the same identity/audit checks.
- Both runs logged a rendered public prompt before gallery launch and before
  poweroff. No mouse correction, repeated script, shutdown shortcut or deadline
  increase was used. Evidence: ignored `obj/028-materials-guard-fresh` and
  `obj/028-materials-guard-light`; documentation validation passes 100 files.

Distributor verification remains pending. Run the focused immutable materials
harness with the current controller and a copied retained clean live-install disk;
this redoes package setup and every visual/shutdown/audit assertion without a
kernel build or unrelated native gates. A pass must still be followed by the normal
release pipeline. The exact earlier server timeout cause remains unconfirmed.
