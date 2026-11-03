# Multicore execution

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md)

Work IDs below are detailed steps of the master tracker, not a separate schedule.

## Implemented boundary

Opt-in native integer AP workers have private static stacks/context and no managed
objects. Default boots keep them disabled. Managed scheduling, GC, drivers and
foreground native service execution remain on the BSP. Extra vCPUs do not distribute
ordinary Zenith application work. Polling workers consume idle CPU.

## Selected implementation sequence (2026-11-03)

C1–C3 are now the selected engineering work. Polaris is implemented and confirmed;
ordinary processes and the compositor still execute on the BSP. Four configured
vCPUs do not currently accelerate them.

| Step | Deliverable | Exit evidence |
|---|---|---|
| C1 / lifecycle | Cooperative cancellation at entry/every 256 integer iterations, distinguish cancelled completion, reusable slots; permanent stop admission and AP halt acknowledgement before disk shutdown | Real production-loop host tests; 1/2/4-CPU native checks, cancel then successful jobs, acknowledged stop before unmount; default-disabled boot preserved |
| C1 / efficient workers | Private interrupt/fault stacks, bounded job ownership, wake/IPI with race-free sleep/recheck, cancellation and stop across queued/running/parked jobs | Idle host CPU measurement, repeated wake/stop races and fault containment, no lost jobs or polling fallback disguised as idle sleep |
| C1 / useful native work | Pilot a bounded image/math job on APs using explicitly owned native buffers; managed UI owner submits and publishes results | Pixel-equivalent output, cancellation/cleanup and equal-work 1/2/4-CPU timings; APs retain no managed roots |
| C2 / per-CPU execution | CPU identity/current thread, interrupt-return switch slots, GDT/TSS/kernel stacks, FP ownership and local timer/reschedule IPIs | Identity/register/FP tests on each CPU; IRQ/lock audit; no managed AP work until C3 |
| C3 / heap safety | Cross-CPU allocation publication and stop-the-world epoch/root acknowledgement; bounded failure handling before AP resume | Heap-pressure tests with all CPU stacks/register roots, pinned/interior roots, cancellation and faults during barriers |
| C3 / useful work | Pinned managed threads and explicitly owned rendering jobs; publish completed frames only on UI owner | Equal-work 1/2/4-CPU timings, pixel-equivalent output, process latency and responsive input under pressure; no migration yet |

The first lifecycle slice deliberately keeps the existing single in-flight
checksum batch and static slots. It is not an application task queue. The test-only
`cpu cancel-test` cancels a batch at its readiness barrier and verifies each CPU's
cancelled completion, followed by normal reusable jobs. Native loop checkpoints also
support cancellation after execution starts. A stop latches admission closed for
this boot, including if a later filesystem flush fails. A missing/faulted AP stop
acknowledgement refuses power change; its private memory is never reclaimed.
Idle polling is still a C1 gap. The native-buffer pilot can establish useful
parallel work before C3; general managed processes still require C2 and C3.

## C2/C3 audit boundaries

The existing adaptation layer is tied to Cosmos **3.0.89**. Keep changes in Zenith's
signature-checked build copies; the sibling Cosmos checkout is reference material.
Before enabling any managed AP entry, verify these runtime contracts:

- `SchedulerManager.GetCurrentCpuId` is CPU-zero-only; per-CPU containers alone do
  not establish CPU-local scheduler dispatch. Context-switch native imports and
  interrupt-return scratch state must change together. The built ELF still has
  single global `_context_switch_target_rsp` and `_context_switch_is_new_thread`
  symbols, so the native switch handoff is not CPU-local either.
- Allocation publication currently uses local interrupt masking in
  `tools/PatcherFixup/AllocationPublicationFixup.cs`. That excludes BSP preemption,
  not another CPU. Existing root normalization in `ConservativeRootFixup.cs` is
  not a stop-the-world barrier.
- `src/Native/x64/Process.s` and `NativeProcess` retain serialized raw application
  entry. Kernel native workers must not execute arbitrary application syscalls,
  share that context, or gain managed references as a shortcut.
- UI/window ownership stays on its host thread. Only bounded jobs with explicitly
  owned native or GC-safe buffers may run elsewhere; framebuffer mutation from
  multiple managed APs is not the first rendering step.

Use `fps reset`, the equal-work `fps bench`/`fps motion` commands and `fps`
compose/present counters to separate composition from presentation. Capture
identical resolution, effects, app workload, accelerator and CPU count before
comparing. No speedup claim follows from checksum overlap or a TCG CPU-count test.

## C1–C4 — Ordered implementation steps

- [ ] C1 (active): safe wake/IPI, per-CPU interrupt stacks, cancellable bounded queues and
  shutdown quiescence instead of polling.
- [ ] C2: per-CPU scheduler/current thread, context, GDT/TSS, FP and timer state;
  audit lock/IRQ ownership before running managed work on APs.
- [ ] C3: allocator/GC stop/root barriers, then pinned managed threads with memory
  pressure/fault/cancel tests.
- [ ] C4: migration/load balancing and credential/service concurrency; stress real
  I/O and recovery at multiple CPU counts before considering default managed SMP.

## Source and acceptance

[Multiprocessing](../../src/Core/Multiprocessing), [x64](../../src/Native/x64),
[exact contract](../MULTICORE.md), [CPU smoke](../../tools/native-cpu-smoke.py).
Preserve default-disabled and 1/2/4-CPU opt-in scenarios. Nested-KVM measurements
are deployment observations, not evidence that TCG is universally faster.

## C1 lifecycle validation (local, 2026-11-03)

Release ISO built with Cosmos 3.0.89 and SDK 10.0.114. ELF SHA256:
`8e8778f7303fa6602680ceedc8ab72c6583f2ba5a27ea9a06fca88c047ee35ce`.
The focused native CPU gate passed **61 checks** on QEMU 11.1.3 with forced TCG:
1/2/4-vCPU opt-in and four-vCPU default-disabled, cancellation followed by two
successful concurrent jobs, actual CPU identity, IRQ-state preservation, parameter
refusals, responsive shell and acknowledged AP stop before clean poweroff.
Evidence: `obj/c1-lifecycle-native-complete`; wall time 54.177 seconds.

Four host tests execute the unchanged production assembly integer loop and cancel/
stop publishers (including cancellation while running). Four PowerControl tests
cover busy refusal, worker-stop failure before disk/hardware shutdown, and flush
failure/gate release. These do not replace AP interrupt/halt validation.

The initial focused guest attempt, `obj/c1-lifecycle-native`, stopped after receiving
only the first `cpu status` line; it was a harness ordering defect. The gate now
waits for its status-zero completion before inspecting later fields, with unchanged
IRQ-state assertions. The subsequent complete run above is the passing evidence.
The common controller run passed 359 tests and hit one sandbox-only `/var/tmp`
write refusal in the release-failure regression. That exact remaining test passed
outside the sandbox (360 tests covered across the run and focused recheck).
The complete native-process gate also passed with `--cpu-workers` under forced
TCG, using two vCPUs: **849.823 seconds**, evidence `obj/c1-process-workers`.
It covers 64 exact-output byte pipelines, fault/timeout recovery, worker checks
after application faults, signed native upgrade, account/file permissions, hidden
authentication, GUI/local canonical/raw input and cancellation, independent ext2
audit and clean shutdown. This gate is correctness evidence, not a process-throughput
benchmark. Both guest runs use the Release ELF hash above.
No distributor/installed acceptance or application/rendering speedup is claimed.
