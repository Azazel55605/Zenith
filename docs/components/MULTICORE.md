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

### Installed 0.0.44 follow-up

The distributor build succeeded, but VMware CPU diagnostics exposed stale user
selectors across GDT growth and missing suspended BSP boot/UI roots during worker
collection. Both are repaired locally, with refused diagnostic admissions retried
without replaying accepted work. [Evidence and final candidate checks](../archive/2026-11-04/installed-cpu-return-and-gc.md).
Q2/H1 owns new-release and installed VMware confirmation; the previous CI result
does not establish that acceptance.

## C5 — General SMP

**Started 2026-11-15.** Requested by the operator: real multicore for processes in general, not
only the compositor.

### Where work runs before C5

| Work | CPU |
|---|---|
| Native program code between system calls (C4, `smp=native`) | any CPU |
| Fixed compute jobs (material blur, managed frame pilot) | APs |
| Every native system call (files, present, events, network, terminal) | BSP: the slice returns, the BSP actor services it, then resubmits |
| Kernel threads: file jobs, network, package manager, shell, built-in apps | BSP |
| GC collection, scheduler, drivers, interrupts | BSP |

Measured effect: four CPUs improve whole-scene composition by about 8 % (KVM) and 2–3 % (TCG)
([evidence](../archive/2026-11-03/parallel-material-composition.md)); a program that makes many
system calls gains nothing, because each call queues on the BSP.

### What C5 changes

The pinned Cosmos scheduler already has per-CPU states, spinlocks and a CPU id on every thread;
it is started with exactly one dispatch CPU (`BootCpuFixup`, `PinnedCpuFixup`). C3 made allocation,
the heap and collection safe with several active CPUs; C4 gives every AP private bridges, timers and
wake vectors. C5 finishes the step from pinned jobs to general scheduling:

1. **C5.0 Measure.** Per-CPU busy time and, per program, user time versus BSP service time and
   queueing, at 1/2/4 CPUs, for a file copy, a compute loop, Observatory and Horizon. Kept as the
   before/after baseline.
2. **C5.1 Locks.** Every critical section that relies on masking interrupts (14 pinned Cosmos files:
   scheduler manager, condition variable, mutex, interrupt event, alarm system, timer device, keyboard
   queue, kernel console, log, heap/GC/TLAB already under the C3 gate) becomes an IRQ-safe spinlock
   section; the single context-switch slot becomes per CPU. Zenith's own kernel code is audited for
   the same assumption.
3. **C5.2 Scheduling on every CPU.** Dispatch count equals the online CPUs. Each AP runs the general
   scheduler with its own timer tick; a wake on another CPU sends the reschedule vector; an idle CPU
   takes ready work from a busy one; collection stops every CPU running managed code. Behind
   `smp=managed` until qualified.
4. **C5.3 Services follow their program.** A native program's service thread runs on the CPU its
   code runs on, so system calls no longer cross to the BSP. Kernel threads run anywhere. Drivers and
   device interrupts stay on the BSP: a call from another CPU queues work and waits for completion.
5. **C5.4 On by default** with two or more CPUs, `smp=off` to opt out, after a 1/2/4/8-CPU stress
   gate (all native gates at each count, lock-order and wake races, GC under load) and a VMware run.
6. **C5.5 Threads in native programs**: several user contexts in one address space, scheduled like
   any other thread.

### C5.1 status (local)

`src/Native/Cpu/sched.c` is a recursive per-CPU scheduler lock that cooperates with a pending GC
stop while it spins. `tools/PatcherFixup/SchedulerLockFixup.cs` makes the 9 masked sections of the
pinned scheduler (`GetBusyCpuTimeNs`, `RegisterThread`, `CreateThread`, `ReadyThread`, `BlockThread`,
`ExitThread`, `YieldThread`, `MarkSleeping`, thread creation in `libSystemNative`) take it after masking
and release it before restoring, and wraps the three interrupt-time paths (`OnTimerInterrupt`,
`ReschedulePendingFromIrq`, `ScheduleFromInterrupt`) whole. `cpu sched` reports acquisitions,
contention, spins, refusals, nesting depth and the owner. Checked by `tools/test_sched_lock.py` (four
host threads acting as CPUs) and `tools/test_sched_lock_fixup.py` (IL of an adapted copy), and in the
guest by `native-smp-smoke.py`. [Audit](../archive/2026-11-15/c5-lock-audit.md).

### C5.2 design

- **One rule for running threads:** a thread runs only on `SchedulerThread.CpuId`. Its CPU changes
  only while it cannot run: at creation, or after its CPU has switched stacks away from it. A CPU
  never picks a thread whose context another CPU has not finished saving.
- **Placement at creation first.** Threads created through Zenith's placement API get the least loaded
  online CPU; everything else stays on the BSP (desktop/UI owner, drivers, the kernel loop, the raw
  bridge). Load balancing and migration of running threads come after.
- **APs schedule through the same path as the BSP.** The LAPIC timer handler (vector 0xEF) only calls
  `OnTimerInterrupt` for its own logical CPU, and the IRQ stub already keeps per-CPU switch slots (C2).
  Each AP's permanent pinned owner becomes its idle thread: its native worker loop (native jobs,
  protected contexts) runs when no managed thread is ready. The stride policy gets per-CPU state for
  every AP and the dispatch count becomes the online count.
- **Two interrupt tables per AP.** The native table (today's) maps the new scheduler vector to a
  handler that only acknowledges it and records a pending tick. The managed table routes it, and
  0xEF, through the Cosmos IRQ stub. An AP loads the managed table only while it runs managed threads
  and switches back, with interrupts masked, before it runs a native job or a protected user slice;
  the C4 one-shot quantum timer is unchanged. A managed interrupt can therefore never land in user
  mode or in native work.
- **Ticks and wakes.** The BSP's timer tick sends the scheduler vector to every AP that is in managed
  mode; its handler runs the AP's `OnTimerInterrupt`. `ReadyThread` for a thread of another CPU marks
  that CPU and sends the same vector, whose exit reschedules.
- **GC:** every AP running general threads is a permanent participant; its root bounds follow the
  thread it is running (updated at each switch); suspended threads are scanned from the registry as
  on the BSP.
- **Serial and log:** early UART/GOP writes from APs are dropped (C3 rule); Zenith's log queues AP
  lines for the BSP so gates and `dmesg` keep them.
- Behind `smp=managed` until C5.4.

### C5.2 status (local, in progress)

- `smp=managed` (implies the C1 workers) raises the dispatch count to the online CPUs, gives every AP's
  pinned owner the role of idle thread and builds the managed interrupt tables
  (`ManagedDispatchFixup`, `zenith_cpu_managed_enable`). Boot log: `cpu: native workers=4 ready=True;
  managed dispatch=4 enabled=4`.
- `CpuWorkers.StartAnywhere` places a kernel thread on an AP (round robin) by asking for exactly
  0x41000 stack bytes; every other thread stays on the BSP. Threads placed on an AP start with the AP's
  code selector (8); the BSP runs on Limine's 0x28.
- Root causes found on the way:
  - **CPU identity cost.** `zenith_cpu_logical_id` (behind `GetCurrentCpuId`, called on every allocation)
    did an RDMSR, and on APs also a CPUID and a table scan; both exit the VM. It now reads `IA32_TSC_AUX`
    (id | 0x100) with RDTSCP, and returns 0 without any instruction before the workers start. One managed
    thread doing allocation-heavy work went from 2.38 s to 0.11 s; default boots benefit too.
  - **Shared GC state outside allocation entries.** `ReturnAllocContext` (thread exit returns its TLAB to
    the free list) and the GC handle manager (no lock at all) relied on masking; they now take the C3 heap
    gate. A thread exiting on an AP had corrupted the free list (page fault in the sweep).
  - **Idle hand-back.** The policy picks the next thread before it requeues the preempted one, so a lone
    thread on an AP fell to the idle owner every tick; the AP idle loop now offers the CPU again before
    halting.
  - **Lock hold time.** Periodic tick traces wrote to COM1 under the scheduler lock, and every AP walked
    the thread registry for sleepers each tick. Traces are quiet; only the BSP checks sleepers.
  - **Global allocation gate.** Every allocation took the C3 heap gate, so CPUs took turns allocating. The
    TLAB fast path now runs without it (interrupts are masked from reservation to header publication, and
    a CPU acknowledges a stop only through its stop interrupt or while waiting on the gate); the slow path
    and pinned allocations keep it.
  - **Roots of waiting AP threads.** The C3 collector fixup skipped the stack of every thread whose CPU
    was not 0: on C3 those were only the pinned owners, covered by the stopped AP snapshot. Under
    `smp=managed` a thread preempted, blocked or sleeping on an AP therefore had no roots at all, and a
    collection freed its live objects (a closure read back as zero after a long allocation loop; also
    threads that never finished). An AP thread is now skipped only while it is Running, which the
    snapshot covers; every other one is scanned from its saved context like a BSP thread.
  - **Duplicate queue entries.** A thread readied while still its CPU's current thread was queued by
    the wake and again by its own switch-out. The stride policy's insertion now removes an existing
    entry for the thread first.
- Acceptance probe: `cpu threads N [rounds]` runs N allocation-heavy managed threads through
  `StartAnywhere`, checks every result against a BSP reference and reports the CPUs used.

### C5.3 design (started 2026-11-16)

Survey of what a system-call service touches when it runs off the BSP:

- **Already safe across CPUs:** `lock`/`Monitor`, `Mutex`, `ConditionVariable` and `InterruptEvent` block
  and wake through `SpinLock.AcquireIrqSafe`; wakes go to the thread's own CPU and kick it (C5.2). The
  SATA driver serializes each port with a spinlock and polls for completion; virtio GPU/net, NVMe and
  xHCI hold IRQ-safe locks. Zenith's log takes a lock around direct UART writes. Zenith kernel code
  guards shared state with `lock` already (threads are preempted on the BSP today).
- **Still BSP-only:** the raw BSP bridge (`s_rawGate`), the desktop/UI owner, framebuffer and device
  registers, the PS/2 keyboard queue and the boot console. Service calls reach them only through
  queues and locks owned by BSP threads.

Plan:

1. **Run the program where its service runs.** Each AP's process bridge is a generated copy of the
   BSP's `zenith_process_run`, which the BSP already calls synchronously from a managed thread
   (`RunNative`). Under `smp=managed`, a native program's run loop moves to a placed thread, and on an
   AP it enters its own CPU's bridge directly instead of queueing a slice and waiting for the BSP's
   doorbell. User code and system calls then alternate on one AP with no cross-CPU hand-off.
2. **Interrupts during user code on an AP.** The BSP bridge already yields a slice for a foreign vector and
   re-delivers it on the caller's stack, so a GC stop parks with valid kernel bounds. The generated AP
   bridges did not: they acknowledged and dropped a GC stop (harmless while APs were no GC participants)
   and faulted the AP on any unexpected vector, including the scheduler tick or kick (0xEF) that
   `smp=managed` can deliver while a slice runs. A stop now ends the slice and is re-delivered; 0xEF is
   acknowledged and ignored (a kick also left `managed_pending` set).
3. **BSP-only work stays queued:** anything listed above as BSP-only keeps its owner thread; a call
   from an AP waits on the existing lock or queue.
4. **Measure** with the C5.0 workloads (file copy, compute loop, Observatory, Horizon) at 1/2/4 CPUs.

### C5.3 status (local, done 2026-11-16)

- `NativeProcess.Run` under `smp=managed` runs a program started without `ZENITH_CPU_MASK` on a placed thread
  (`CpuWorkers.StartAnywhere`, credentials carried over); the caller joins it. An explicit mask keeps the
  BSP-owned slice protocol and its placement guarantees.
- On an AP, `RunProcessSlice` submits for that AP alone and runs the slice itself
  (`zenith_cpu_process_run_here`): it claims the descriptor, suspends BSP-forwarded ticks for the slice
  (inside the bridge they would count as the program's quanta), enters the AP bridge and publishes the
  result. An AP owns only invocations whose affinity is exactly its own bit (submit, run, cancel, reap);
  everything else stays BSP-owned. If the AP's worker took the slice first, the old wait applies.
- The slice quantum is the BSP timer's period; LAPIC registers are per CPU, so the BSP records it when
  managed dispatch is enabled (an AP reading `cpus[0]` read its own idle timer and every submission was
  refused).
- `cpu status` reports `local-process-slices`. `native-smp-smoke` adds a four-CPU `smp=managed` pass:
  placed hello and probe (one AP per program), three unmasked programs timed, timeout, fault, local
  slices counted, no AP fault.
- A slice that ends only for its quantum is re-entered on the AP, at most 8 quanta in a row, while no kick
  and no cancel is pending: each round trip through the managed loop cost a resubmission under the shared
  queue lock (compute work: 2261 slices through the loop before, 571 after).
- Measured under TCG, four CPUs, same installed disk:

  | Workload | `smp=native` (BSP services calls) | `smp=managed` (service on the AP) |
  |---|---|---|
  | one `sunbeam --io-budget` (1 MB in 4 KB writes) | 1.59–1.64 s | 1.29–1.44 s |
  | three in parallel | 2.71–3.11 s | 1.76–3.01 s |
  | three `--smp-work` (compute) | 3.42–3.68 s | 3.15–3.65 s |

  Call-heavy programs gain about 20 %; compute work is level (the BSP no longer handles their calls, which
  is where the BSP-bound programs were losing).
- `native-settings-smoke` passes in full under `smp=managed` (window app, materials, input, concurrent
  network I/O, reboot persistence): window programs are placed like any other.
- Left for C5.4's stress gate: programs with an explicit `ZENITH_CPU_MASK` under `smp=managed` keep the
  BSP-owned protocol; one early run measured them slower (about 4.5 s against 2.9 s for the compute work),
  not reproduced since.

Rules carried over: Cosmos skips `finally` while unwinding, so every lock taken in kernel code is
released explicitly on the exception path; a lock left held on one CPU now stalls the others.

## Source and acceptance

[Managed bridge](../../src/Core/Multiprocessing/CpuWorkers.cs),
[native queues](../../src/Native/Cpu/workers.c),
[AP entry/interrupts](../../src/Native/x64/CpuWorkers.s),
[CPU gate](../../tools/native-cpu-smoke.py),
[host queue tests](../../tools/test_cpu_workers.py).
Preserve default-disabled boots and 1/2/4-CPU opt-in cases. Timing results from one
host or accelerator do not certify a different hypervisor or physical machine.
