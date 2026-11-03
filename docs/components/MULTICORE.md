# Multicore execution

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md)

The work IDs below expand the master tracker. [Runtime contract](../MULTICORE.md).

## C1 — Native worker foundation

**Local implementation complete; distributor/installed acceptance pending.**
Opt-in native queues use permanent private work/interrupt/fault stacks, GDT/TSS,
IRQ-scoped BSP admission, targeted wake IPIs and interrupt-shadow-safe HLT idle.
Eight descriptors per AP retain terminal results until explicit reaping. Generation
tokens prevent stale-handle reuse. Queued/held/running cancellation and shutdown
quiescence are bounded; failed queues are quarantined while healthy APs continue.
No AP enters managed code, keeps GC references, owns the UI or performs device I/O.
Default boots remain BSP-only.

The native pixel-buffer pilot establishes useful bounded parallel work before C3.
Its results match an independent scalar implementation, and local equal-work
1/2/4-CPU timings are recorded with the environment and limits. The ordinary managed
compositor and application bridge remain BSP-only.

[Implementation and local evidence](../archive/2026-11-03/native-worker-queues.md).
The mandatory release CPU gate and AP-enabled process gate preserve these contracts.
Local exit evidence: 94 CPU checks, 371 controller tests, 846 host tests and the full
AP-enabled process/64-pipeline gate passed. The final four-CPU native pixel pilot
was 2.14× faster than its one-CPU case in local TCG; ordinary apps remain BSP-only.
Installed/VMware, KVM and physical observations belong to Q2/H1, not assumed proof
from additional QEMU vCPUs. No release has been published for C1 yet.

## C2 — Per-CPU execution state

**Local foundation complete; distributor/installed acceptance pending.** The pinned Cosmos build copies
now use hardware logical CPU identity, CPU-local IRQ switch/error slots and
stack-local return temporaries. Both IRQ scheduler callbacks translate logical
identity. Full baseline FP state is preserved with a matching 512-byte native save
area and managed thread layout/initialization. APs own their initial FP environment,
private tables/stacks, diagnostic one-shot timer and native reschedule interrupts.
The process bridge rejects non-BSP ownership before touching shared state.
Local exit evidence: 106 CPU checks, 382 controller tests, 846 host tests, 46 runtime
adapter regressions and the full AP-enabled process/64-pipeline gate passed on the
frozen C2 kernel. This establishes the execution-state foundation, not managed SMP.

[Implementation, lock/credential audit and evidence](../archive/2026-11-03/per-cpu-execution-state.md).
General scheduler dispatch, raw foreground processes, services and UI mutation
still admit only the BSP. A native reschedule IPI does not dispatch a managed
thread. C3 qualifies permanent pinned owners and their roots/GC, exception and
TLS boundary below; C4 owns general scheduling, migration and concurrent services.

## C3 — Heap safety, then pinned managed work

**Local foundation complete; distributor/installed acceptance pending.** The
sealed pinned-job boundary includes shared publication/heap/metadata ownership,
native stopped-root epochs, permanent TLS/TLAB/exception owners, managed frame
jobs and a BSP-owned `fps cpu` preview. General scheduling/migration remain C4.
413 controller tests, 846 host tests, 46 existing runtime regressions, 106 CPU
checks, full C3 graphical/safety acceptance and the complete AP-enabled process
sequence passed. [Frozen evidence and limits](../archive/2026-11-03/pinned-managed-workers.md).
[Runtime contract](../MULTICORE.md#c3-pinned-managed-compute).

1. Six object-header/length publication entries and seven unmanaged heap/page
   entries share recursive native ownership with IRQ preservation.
2. Native stop epochs require current register/work-stack acknowledgements before
   mark/sweep. Missing roots refuse collection; admitted collector failures keep
   the world stopped. Completed collections alone resume mutators.
3. Pinned/interior references, allocation pressure, deferred requests, exceptions,
   cancellation, failed APs and shutdown are exercised at 1/2/4 CPUs. Cold metadata
   construction publishes keys last; UART/early-GOP output stays BSP-owned.
4. Permanent pinned owners run fixed managed jobs without policy dispatch or
   migration. Complete frame copies return to the UI owner. `fps cpu` presents an
   independently checked frame; equal-work timings include dispatch and copying.
   The full process gate keeps managed AP roots live through its fault/pipeline
   sequence before checking reuse. A separate identical-payload 1/2/4-CPU
   pipeline comparison records CLI latency with parked managed roots.

Use `fps reset`, `fps bench`/`fps motion` and `fps` compose/present counters to
separate composition from presentation. Compare identical resolution, effects,
app workload, accelerator and CPU count. Checksum overlap is not an application
performance result; the native pixel pilot is not the full GUI workload either.

## C4 — Migration and broad concurrency

Migration/load balancing follows C2/C3. Apply the pinned-job boundary to measured
compositor/app workloads before broad migration. Audit credentials, service concurrency, centralized interrupt ownership,
real I/O contention and recovery before considering default managed SMP.

## Source and acceptance

[Managed bridge](../../src/Core/Multiprocessing/CpuWorkers.cs),
[native queues](../../src/Native/Cpu/workers.c),
[AP entry/interrupts](../../src/Native/x64/CpuWorkers.s),
[CPU gate](../../tools/native-cpu-smoke.py),
[host queue tests](../../tools/test_cpu_workers.py).
Preserve default-disabled boots and 1/2/4-CPU opt-in cases. Timing results from one
host or accelerator do not certify a different hypervisor or physical machine.
