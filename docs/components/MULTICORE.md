# Multicore execution

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md)

The work IDs below expand the master tracker. [Runtime contract](../MULTICORE.md).

C1–C4 are jointly CI-qualified and tagged as `kernel/v0.0.44` at `8502029`.
[Release and CI evidence](../archive/2026-11-04/c4-release-and-ci.md) records the
complete fresh gate matrix. Distributor publication and installed-VM acceptance
remain separate under Q2/H1.

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
compositor and application bridge remained BSP-only at this stage; C4 extends them below.

[Implementation and local evidence](../archive/2026-11-03/native-worker-queues.md).
The mandatory release CPU gate and AP-enabled process gate preserve these contracts.
Local exit evidence: 94 CPU checks, 371 controller tests, 846 host tests and the full
AP-enabled process/64-pipeline gate passed. The final four-CPU native pixel pilot
was 2.14× faster than its one-CPU case in local TCG; ordinary apps were BSP-only at the C1 boundary.
Installed/VMware, KVM and physical observations belong to Q2/H1, not assumed proof
from additional QEMU vCPUs. C1 is included in the tagged C4 release above.

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
TLS boundary below; C4 owns protected-context scheduling, migration and concurrent service actors.

## C3 — Heap safety, then pinned managed work

**Local foundation complete; distributor/installed acceptance pending.** The
sealed pinned-job boundary includes shared publication/heap/metadata ownership,
native stopped-root epochs, permanent TLS/TLAB/exception owners, managed frame
jobs and a BSP-owned `fps cpu` preview. C4 extends protected application execution below.
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

**Local and CI qualification complete; distributor/installed acceptance remains Q2/H1.**
MaterialComposition now shares one exact radius-four filter across client materials,
trusted window chrome/backgrounds and the taskbar. Larger changed regions distribute
private clamped-halo tiles to healthy idle APs, with BSP participation and rotating
owner selection. Complete results are reaped by generation; no window, scene pointer,
credential or managed reference migrates. Small, disabled, busy and unsupported paths
retain scalar rendering. Failed work reserves its storage and disables further blur
admission; the BSP reconstructs the complete scalar region before painting.

The signature-checked copied central interrupt registry now rejects AP registration,
allocation and freeing before its lock/body. F1/F3/F4/F5/F6 are native CPU leases;
MSI allocation remains inside the verified 40–EE range. This reserves ownership;
it does not implement multi-destination device routing or a general IRQ scheduler.

[Implementation, measurement and local qualification](../archive/2026-11-03/parallel-material-composition.md).
`cpu blur-test`, `fps parallel-bench [fullscreen]`, `fps parallel on|off` and `fps`
report exact-filter work and actual scene timings/counters. Performance results
must include scene size, effects, CPU count and accelerator.

### Protected execution scheduler

The implementation uses owned **protected execution contexts** as its migratable
unit. It does not move managed kernel `Thread` stacks. Each application retains
its BSP service actor and captured identity; its user register/FP/address-space
state is queued to an allowed AP between syscall or reschedule returns. Permanent
C3 TLS/TLAB owners remain pinned. This gives packaged applications concurrent CPU
execution without admitting unreviewed driver, UI or filesystem code on APs.

Seven private bridges are derived from the hash-checked BSP ABI. Every AP owns its
raw GDT/TSS/IDT, stack, saved kernel state and FP image. A leased-context capability
is checked before privileged state changes. Local timer IRQs enforce execution
budgets; reschedule returns preserve the complete user frame. Device IRQs keep
BSP ownership. A raw context is never moved while Running.

Eight generation-tagged descriptors retain their leases until reaping. Ready
contexts may transfer to a healthy permitted sibling under the native pool/CPU
lock order. Runnable tickets are separate from terminal storage. Queued stops,
running cancellation, failed-owner quarantine and shutdown must acknowledge before
the BSP releases borrowed context/stack storage. Damaged queue metadata or missing
stop acknowledgement fail-stops rather than freeing live memory.

`ZENITH_CPU_MASK` is an invocation-local mask (decimal 1..255 or `0x01`..`0xff`).
Its default allows all owners, including serialized BSP fallback. An explicit
AP-only mask never silently expands to the BSP. `ps --cpu` exposes the last CPU,
mask and migration count within the existing UID-filtered process table.

Local C4 exit evidence:

1. The production native pool passes affinity, forced Ready transfer, FIFO
   requeueing, exact tickets, retained generations, running stop and faulted-target
   tests. Real guest frames preserve GPR/x87/MXCSR/XMM state across service returns.
2. Installed 1/2/4-CPU and additional eight-CPU cases exercise first entry,
   execution witnesses, simultaneous deterministic work, fault/NX/W^X, timeout,
   prompt AP cancellation, teardown and default-disabled fallback. TCG also passes.
3. The service audit keeps captured identities and private handles on their BSP
   actors. Real filesystem/network contention and personal UID isolation pass;
   the new installed streaming fixture overlaps acknowledged Meridian input,
   apply and material presentation and verifies committed bytes after ext2 audit.
4. All 14 mandatory native gates pass against one frozen ELF/ISO, including the
   four-CPU 64-pipeline/parked-managed-root sequence, graphical/terminal session
   teardown, signed upgrades, interruption/recovery and independent disk audits.
   Subsequent fresh Release acceptance passed the complete combined plan without
   reuse; final CI repeated every native gate on one shared source-bound build.
5. Repeated identical CPU work proves overlap on three APs. The compositor gate
   independently compares exact ordinary/fullscreen scene pixels and timings.
   These establish local concurrency, not universal application speedup. Default
   AP activation remains gated on Q2/H1 distributor/operator evidence.

[Frozen artifacts, test counts, measurements and limits](../archive/2026-11-03/protected-process-migration.md).

## Source and acceptance

[Managed bridge](../../src/Core/Multiprocessing/CpuWorkers.cs),
[native queues](../../src/Native/Cpu/workers.c),
[AP entry/interrupts](../../src/Native/x64/CpuWorkers.s),
[CPU gate](../../tools/native-cpu-smoke.py),
[host queue tests](../../tools/test_cpu_workers.py).
Preserve default-disabled boots and 1/2/4-CPU opt-in cases. Timing results from one
host or accelerator do not certify a different hypervisor or physical machine.
