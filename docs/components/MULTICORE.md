# Multicore execution

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md)

Work IDs below are detailed steps of the master tracker, not a separate schedule.

## Implemented boundary

Opt-in native integer AP workers have private static stacks/context and no managed
objects. Default boots keep them disabled. Managed scheduling, GC, drivers and
foreground native service execution remain on the BSP. Extra vCPUs do not distribute
ordinary Zenith application work. Polling workers consume idle CPU.

## C1–C4 — Ordered implementation steps

- [ ] C1: safe wake/IPI, per-CPU interrupt stacks, cancellable bounded queues and
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
