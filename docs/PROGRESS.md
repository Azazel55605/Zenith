# Active work

Updated **2026-10-18**. [Status](STATUS.md) owns delivery boundaries;
[Roadmap](ROADMAP.md) orders future work.

## Native terminal streams: release handoff

- [x] Stream/runtime fixes passed full local acceptance at `6544ffd`; user-authorized
  `kernel/v0.0.19` points there. It remains immutable and fails the server's first
  repeated byte pipeline. Prior implementation/evidence is archived below.
- [x] Exact server binaries pass the full native gate locally under the same Debian
  QEMU version, including a CPU-throttled portion. Server reliability is unresolved.
- [x] Deploy bounded diagnostics (`384d9fc`). Operator snapshot shows both sampled
  worker stacks halted, threads 0/78/79/81 Ready and 80 Sleeping; no Running entry.
- [x] Reproduce a same-thread dispatch defect in the actual pinned dependency:
  wake-before-switch then self-selection removes its queue entry but leaves Ready.
- [x] Adapt both voluntary and IRQ self-dispatch to restore only Ready -> Running;
  preserve current stack, ticket totals and first-entry/native-switch flags. All
  13 real-dependency scheduler regressions pass, including 100 repeated cycles;
  all 40 pinned dependency adapter tests pass.
- [x] Build the untagged candidate at `a63ce7c`; pass 658 host tests and 134
  controllers. Full native-process gate passes under Debian QEMU 10.0.13 in
  648.864s, including 64 pipelines across GC and installed/GUI/local audits.
- [x] Operator tested the rebuilt candidate: the first pipeline still times out
  (120.727s). Verified halt call sites are in `ConditionVariable.WaitTimeout`;
  the self-dispatch correction alone does not resolve the actual server failure.
- [x] Reproduce two further pinned-runtime defects: a condition wait returns after
  an unrelated interrupt while still parked, and timed monitor interop discards
  the timeout result. Adapt build copies to await a real wake and return the result.
- [x] Real-dependency tests cover timed/signaled waits, an intervening unrelated
  interrupt, 100 repeated wake cycles, IRQ/ticket preservation, and both source
  plug and transplanted CoreLib timeout returns.
- [x] Release build, 658 host, 134 controller and all 48 adapter checks pass.
  Full native gate passes under Debian QEMU 10.0.13 / TCG in 686.101s: 64 exact
  byte pipelines, FP, cancellation/fault cleanup, installed audit and GUI/local input.
- [ ] Verify these wait corrections on the actual distributor. The reproduced
  defects are not yet a proven cause of its stall; pipeline expectations/timeouts
  remain unchanged. Rebuild current main, not the immutable failed tag.
- [ ] Once the server candidate passes, run clean-source full release acceptance
  before a new kernel tag. No replacement release is qualified yet.
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
