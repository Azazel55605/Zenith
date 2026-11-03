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
Managed scheduler state, raw foreground processes, services and the UI still admit
only the BSP. A native reschedule IPI does not dispatch a managed thread. Per-CPU
scheduler timer calibration, roots/GC barriers, exception-chain/TLS ownership and
pinned managed dispatch must be qualified together in C3 before AP admission;
C4 owns migration and general concurrent services.

## C3 — Heap safety, then pinned managed work

1. Cross-CPU allocation publication. `AllocationPublicationFixup.cs` currently
   excludes BSP preemption using local IRQ masking, which does not exclude another
   CPU. Preserve its object-header and array-length publication contracts.
2. Stop-the-world epoch, register/stack root acknowledgements, failure policy and
   safe resume. Existing conservative-root normalization is not a GC barrier.
3. Pressure/fault/cancel tests with all participating CPU stacks, pinned/interior
   references and allocation during collection requests.
4. Pinned managed threads and explicitly owned jobs first. Publish complete frames
   on the UI owner; compare equal-work output, process latency and responsive input
   at 1/2/4 CPUs. Keep migration disabled.

Use `fps reset`, `fps bench`/`fps motion` and `fps` compose/present counters to
separate composition from presentation. Compare identical resolution, effects,
app workload, accelerator and CPU count. Checksum overlap is not an application
performance result; the native pixel pilot is not the full GUI workload either.

## C4 — Migration and broad concurrency

Migration/load balancing follows C2/C3. Audit credentials, service concurrency,
real I/O contention and recovery before considering default managed SMP.

## Source and acceptance

[Managed bridge](../../src/Core/Multiprocessing/CpuWorkers.cs),
[native queues](../../src/Native/Cpu/workers.c),
[AP entry/interrupts](../../src/Native/x64/CpuWorkers.s),
[CPU gate](../../tools/native-cpu-smoke.py),
[host queue tests](../../tools/test_cpu_workers.py).
Preserve default-disabled boots and 1/2/4-CPU opt-in cases. Timing results from one
host or accelerator do not certify a different hypervisor or physical machine.
