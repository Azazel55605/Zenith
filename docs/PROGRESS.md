# Active work

Updated **2026-10-18**. [Status](STATUS.md) owns delivery boundaries;
[Roadmap](ROADMAP.md) orders future work.

## Native terminal streams: release handoff

- [x] `kernel/v0.0.19` remains immutable at locally accepted `6544ffd`; its
  distributor native pipeline fails. Later untagged candidates also fail there.
- [x] Real-dependency regressions reproduce and correct queue/wake accounting,
  self-dispatch state and timed-condition contracts. Latest runtime candidate
  `693cac9` passes 658 host, 48 adapter tests and the full local native gate
  (686.101s), but the operator's rebuild still fails after 120.746s.
- [x] Copy exact failed server ELF/ISO and disposable installed disk. Direct and
  original pre-pipeline warmup replay pass locally under Debian QEMU 10.0.13 / TCG.
  Server TSC/LAPIC calibration is normal; later queue observations identify the
  ticket-underflow/global-pass starvation mechanism below.
- [x] Add no-build replay using a separate disk copy and failure-only typed queue,
  ticket/pass/runtime and bounded saved-frame observations from the exact ELF.
  State-only snapshots also occur during healthy pipelines and do not establish
  queue membership. That diagnostic slice changes controller tools only.
- [x] Pass 149 common controller tests, actual fixture replay/warmup and live
  read-only observer checks. A synthetic failed wait remains false after capture.
- [x] Distributor warmup replay reproduces the stall and reports total tickets
  `2^64 - 1400`: global stride becomes zero; polling readers repeat while queued
  producers retain unchanged pass/runtime. Exact pinned policy regression
  reproduces that total and frozen global pass after blocked-thread exit debits.
- [x] Track runnable admission in each copied stride record; debit once across
  block/exit, transfer on migration and change totals for runnable priorities only.
  Sleeping/Created exits and policy handover no longer subtract absent tickets.
- [x] Release ISO builds; 658 host, 149 controller and 56 actual dependency-adapter
  tests pass. The full Debian QEMU/OVMF native gate passes in 656.737s,
  including 64 pipelines, GC, fault/cancel, credentials and desktop/local input.
- [x] Operator reports the rebuilt `b11f8f8` candidate passed the focused native
  gate on the actual distributor. This confirms the fix on the failing server
  path; no server duration or complete four-gate release result was supplied.
- [ ] Once the server path passes, run clean-source full release acceptance before
  a new kernel tag. No replacement release is qualified yet.
- [ ] Distributor publication and installed VMware update/reboot/confirm, followed
  by representative pipe/input checks, remain separate acceptance steps.

[Server-only stall and same-thread dispatch investigation](archive/2026-10-18/native-pipeline-server-stall.md).
[Prior runtime fixes and local acceptance](archive/2026-10-17/scheduler-late-wake.md).
[Stream implementation](archive/2026-10-17/native-streams.md).
Raw TTY/jobs, seek and general process scheduling remain future work.

## Intermittent installed TCP connections

- [x] Trace refresh/update/fetch/nslookup: downloads resolve automatically through
  the same resolver; no separate manual DNS priming requirement or client DNS cache.
- [x] Operator reproduced on 2026-10-17 after successful full refresh: subsequent
  update refreshed core generation 24, then extra exhausted both fetch/connect
  attempts to zenith-stable.net.local:443 (10.0.10.3).
- [x] Operator capture reproduces SYN -> stale plain ACK -> appropriate RST;
  later successful traffic in that capture is another VM. Waiting subsequently
  allowed the operator's command to work. Actual pinned DLL inspection confirms
  source ports restart at 49152 each boot; external-state collision is a hypothesis.
- [x] Operator says failures also occur without reboot/resume. Stale external
  state remains a working hypothesis; its exact location is not established.
- [x] Implement entropy-seeded dynamic-port offset/stride with active-port checks;
  pass 639 host tests, 12 real-dependency adapters and a Release ISO/ELF build.
- [x] Targeted TCG TCP/HTTPS regression passed (100.486 s): bounded exhaustion,
  recovery, 12 verified HTTPS fetches, new source ports on wire and clean poweroff.
- [x] Clean-source acceptance passed at `db90ebc`: 112 controllers, 639 host,
  71 login/session, 92 repository, native programs and 169 kernel recovery checks.
  Repeat exact-artifact TCP/HTTPS and all 12 adapters passed.
- [x] Operator authorized source/release publication; `kernel/v0.0.17` pushed at
  exact tested `db90ebc` with source/validation notes on main.
- [x] Operator reports 0.0.17 installed and initially working after release handoff.
- [ ] Explicit boot confirmation and sustained repeated VMware refresh/update are
  not separately reported. Stale-flow location remains unconfirmed.

Earlier timeout-retirement/fresh-socket retry is implemented and locally tested;
actual-path reliability is still open. See [Networking](components/NETWORKING.md)
and [packet/dependency evidence](archive/2026-10-17/tcp-stale-flow.md).

## Dashboard retry controls

- [x] Add Retry build for failed/interrupted rows, background queue/service API,
  duplicate/busy guards and restricted installed publisher helper.
- [x] Add helper/API regression tests; validate rendered desktop/mobile controls.
- [x] Operator reports restored dashboard running and all three failed-delivery
  records green after retries on the replacement distributor.
- [ ] Retry persistence across browser/SSH closure is not separately reported.

[Validation and deployment boundaries](archive/2026-10-17/dashboard-retry.md).

## Native input/write handoff

- [x] Repair and verify normal Cosmos VS Code Debug/Release ISO builds; operator
  confirms they work. Cosmos stays pinned at 3.0.89.
- [x] Implement finite shell stdin, guarded regular-file write/create/truncate/
  append/flush, resource limits and cleanup; add independent Sunbeam `tee`/stdin `cat`.
- [x] Declare the native I/O capability and update references/manuals.
- [x] Pass clean-source common acceptance at `2a515e8`: 109 controllers, 632 host,
  71 login/session, 92 repository, expanded native programs and 169 kernel checks.
- [x] Push `kernel/v0.0.16`, `sunbeam-tools/v0.3.0` and
  `zenith-man-pages/v0.1.1` at that exact tested source.
- [x] Operator reports replacement distributor restored, all three release records
  green after verification retries, and a full installed-system update successful.
- [ ] Explicit boot confirmation and piped-input/persistent-write acceptance remain
  separately unreported; repeated repository access has the TCP blocker above.

[Detailed evidence and failed attempts](archive/2026-10-16/native-input-and-writes.md).
The long GUI gate uses virtio keyboard; separate standard-VGA recovery retains
PS/2 coverage. Sustained legacy PS/2 input loss in the affected QEMU graphical
profile remains open in [Shell](components/SHELL.md).

Canonical streams/byte pipes are the active slice above; raw TTY, seek and general
process scheduling remain future slices. Existing recovery builtins and read-only ABI-1 binaries stay supported.
