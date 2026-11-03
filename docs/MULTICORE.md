# Multicore foundation

Work owners: **C1–C4** in the [master roadmap](ROADMAP.md). This is the execution
contract; [component steps](components/MULTICORE.md) own implementation detail.

## Activation and boundary

The x64 boot argument `smp=native` starts **isolated native workers**. Ordinary
boots leave them disabled. Up to eight CPUs including the BSP participate; Limine
MP revision 0 supplies topology and AP entry. Legacy APIC IDs above 255 are excluded.
C3 adds explicitly admitted pinned managed compute jobs. General managed scheduling, GC collection, device/filesystem services, the
trusted UI owner and the protected foreground application bridge stay on the BSP.
Additional vCPUs alone do not distribute ordinary application work.

The AP runtime is `src/Native/Cpu/workers.c`, entered through
`src/Native/x64/CpuWorkers.s`. The native runtime never allocates or retains GC references, invokes application
syscalls, writes UART or accesses disks/network. C3 may register one trusted
managed callback after its permanent owners and GC protocol are ready; ordinary
native jobs do not enter that callback.
`tools/build-cpu.py` builds freestanding general-registers-only code, with no red
zone or host-library dependency. Import and instruction regressions are host-tested.
Nothing edits the installed Cosmos packages or sibling upstream checkout.

## CPU ownership and interrupt-driven idle

Each AP has a permanent 256 KiB work stack, private GDT/TSS, 8 KiB interrupt stack
(IST1), 8 KiB fault stack (IST2) and 8 KiB GC stop stack (IST3). Its IDT uses a shared immutable native table.
APs share the BSP kernel CR3 but never the managed scheduler/interrupt dispatch.
Their local device/timer LVT entries are masked. Wake vector `0xf1` and spurious
vector `0xff` have native-only handlers; the spurious path issues no EOI.

Native AP queue code runs with IF clear. The admitted managed callback runs with
IF enabled so a native stop IPI can capture its roots. Empty queues and held jobs recheck their predicates
with IF clear, then use **`sti; hlt; cli`**. The STI interrupt shadow includes HLT:
a publication/IPI in the check-to-sleep window stays pending and wakes the halted
worker. Pending notifications may coalesce; queue/release/cancel/stop state is
authoritative, so workers drain work without requiring one interrupt per job.
There is no periodic AP timer or idle polling fallback. Register-preserving wake
handlers use IST1, issue EOI and return to the worker. Stack-bound errors are counted.

BSP queue mutations preserve incoming IF. The xAPIC ICR pair is IRQ-scoped and
serialized with the BSP producer; its delivery-status wait is bounded. An MFENCE
orders publication before the doorbell; the x2APIC WRMSR path adds LFENCE for
Intel dispatch serialization. Failed
notifications are counted and never establish job completion or halt acknowledgement.
The implemented register protocol follows the
[Intel SDM, section 11.12.3](https://cdrdv2-public.intel.com/819710/252046-sdm-change-document.pdf#page=315).
The local acceptance below exercises QEMU/TCG; KVM, VMware and physical acceptance
remain separate observations.

## Bounded jobs, cancellation and failures

Each AP has eight fixed job descriptors and a bounded FIFO. **Completed results
still consume capacity until explicitly reaped.** A generation-bearing token owns
one descriptor until terminal completion. Stale tokens cannot cancel, start, read
or reap a replacement. Admission rejects a full/stopped/failed queue and unsupported
kind/size/round limits. No arbitrary code pointer or caller-owned managed buffer can
be submitted. Scalar arguments are copied before publication.

The current typed jobs are:

- Bounded integer checksum work, up to 100,000,000 iterations internally; the
  ordinary `cpu test` command retains its 1..10,000,000 limit.
- An owned opaque pixel-buffer blend pilot, up to 4,096 pixels and 128 passes.
  Each descriptor owns its fixed native buffer. Only completed pixels may be read
  on the BSP; queued/running/cancelled data is never returned as successful output.
  Its independent scalar checks and equal-work timings do not make the existing
  managed compositor multicore.
- A root-only diagnostic fault job, used to prove AP fault containment.

Cancellation is checked at entry and at most every 256 checksum/pixel iterations.
Queued jobs are consumed as cancelled; claimed held jobs wake for start/cancel/stop.
A completion that wins a cancellation race remains successful only with its full
result. Host mid-loop cancellation checks use an explicit test-only checkpoint,
not an assumption about thread scheduling speed.
Cancellation during execution publishes a distinct terminal status; a partial
checksum/frame is never successful output. Terminal publication precedes reaping.
The BSP reference workload uses separate storage and is not an AP queue participant.

A fault enters IST2, records the vector/current job and acknowledges permanent halt.
A reaped token stays invalid even after a subsequent AP fault.
A failed queue and all its storage remain reserved; valid outstanding tokens report
fault and cannot be reused. Healthy sibling queues remain usable. BSP lock waits
are bounded and reject failed APs, including a fault inside a critical section;
there is no attempt to repair or restart a partially modified failed queue.

Unexpected diagnostic-owner aborts close admission and request stop rather than
stranding accepted work or replaying it. Poweroff/reboot permanently closes
admission, requests cancellation/stop and wakes
parked APs before disk teardown. Every started AP must acknowledge stopped or
fault-contained halt within five seconds. Missing acknowledgement refuses the power
action. A later filesystem flush failure leaves workers stopped while BSP recovery
remains available. Static worker stacks, descriptors, tables and buffers are never
reclaimed during the boot. Limine bootloader memory is not reclaimed either.

## Diagnostics and acceptance

Root-only commands: `cpu irq-test`, `cpu test [iterations]`, `cpu cancel-test`,
`cpu queue-test`, `cpu idle-test`, `cpu image-test`, `cpu fault-test`, `cpu stop-test`.
`cpu status` reports detected/started CPUs, managed CPU count, stop state and per-AP
park/wake/completion/fault/interrupt-stack/IPI counters. `fault-test` permanently
retires the last participating AP; `stop-test` permanently stops the pool. Use disposable test boots.

The original IDT IF-preservation adaptation remains required. `cpu status` reports
the initial IDT IF state; `cpu irq-test` reloads a valid IDT under both IF states.
Do not reintroduce Cosmos 3.0.89's premature STI during BSP/PIC initialization.

```sh
ZENITH_VALIDATION_ACCELERATOR=tcg python3 -B deploy/distributor/validation-runner.py \
  tools/native-cpu-smoke.py obj/NEW_CPU_RUN
PATH="$PATH:/usr/sbin:/sbin" ZENITH_VALIDATION_ACCELERATOR=tcg \
  python3 -B deploy/distributor/validation-runner.py \
  tools/native-process-smoke.py obj/NEW_PROCESS_RUN --cpu-workers
```

The CPU gate clones the ISO, boots 1/2/4-vCPU opt-in and four-vCPU disabled guests,
verifies queues, cancellation, 200 publish/park cycles per participating AP, every
pilot pixel against an independent scalar reference, equal-work image results,
idle park counters/actual host CPU use, fault isolation and acknowledged poweroff.
CPU commands save their own exit status before logging captured output; a logger
pipeline cannot turn a failed CPU assertion into success. Idle measurement requires Linux `/proc` and a direct QEMU PID, not a launcher/container
process whose CPU counters do not belong to the guest emulator.

For source trees containing this C1 runtime, the full release plan includes the CPU
gate and the native-process gate defaults to two CPUs with native workers enabled.
`--bsp-only` is a focused comparison, not a replacement for that full gate. Older
immutable tags retain their original harness behavior. All existing independent
storage/package/UI gates remain mandatory; no successful result is manufactured.

[Local evidence and limits](archive/2026-11-03/native-worker-queues.md).
C3 extends this foundation as described below; migration remains **C4**.

## C2 execution-state boundary

Copied Cosmos 3.0.89 IRQ code is adapted only after its complete source SHA256
matches. CPU-local switch targets/new-thread flags/exception scratch use 64-byte
native slots indexed by the supported legacy APIC identity; the return temporary
is thread-stack-local. The accessor preserves incoming IF and does not depend on
GS, which protected user entry clears. Scheduler and IRQ dispatch use logical CPU
identity, including a nonzero physical BSP ID; unknown CPUs never alias CPU zero.

The native IRQ frame and managed ThreadContext use a matching 512-byte FXSAVE area
with valid initial x87/MXCSR state. This preserves baseline x87/MMX/SSE, not AVX.
`cpu fp-test` checks non-default FP controls and live x87/XMM values through actual
BSP interrupt returns. `cpu state-test` checks each AP's identity/switch state,
FP state through an AP-local timer interrupt, and targeted reschedule delivery.
Neither command enables managed AP execution.

AP diagnostic timers use their own one-shot vector and are masked before job
completion. They establish local delivery, not an elapsed-time calibration.
A stop request wakes/disarms a waiting timer probe. Native reschedule IPIs record
acknowledgement on IST1 and never invoke managed scheduler callbacks.

The protected process bridge refuses non-BSP entry before any shared bridge state
is written. Existing raw process serialization remains required. The
[C2 audit](archive/2026-11-03/per-cpu-execution-state.md) records locks, credentials,
TLS, exception-chain and GC restrictions; C3 must satisfy these before it admits
managed APs. The ARM64 path remains uniprocessor and has no new AP admission.

## C3 pinned managed compute

Activation remains `smp=native`; it does not distribute arbitrary apps, services,
raw process entry or UI objects. Only a fixed kernel-owned compute entry can be
registered. Scalar job descriptors never retain a managed reference or delegate.
The BSP prewarms supported code and registers a permanent pinned scheduler owner
for every AP, with private thread-static storage, exception chain and TLAB. Those
owners never enter the general scheduler policy or migrate. Its bootstrap now
creates **one real BSP idle owner**, instead of the pinned runtime's dormant
physical-count idle records. Physical topology reporting remains unchanged.

All six allocation/header publication entries and seven unmanaged heap/page
entries share a recursive native ownership gate with IRQ preservation. Heap
reservation alone is insufficient: method tables and array lengths must be visible
before another CPU can collect. The BSP is the sole collector. It selects active
managed participants under that gate, issues a new stop epoch and requires every
participant's register/work-stack acknowledgement for that exact epoch. The native
F5 handler uses IST3 and never enters managed code. Publication waiters with IF
clear cooperate through an explicit native poll instead of blocking the collector.
The scanner uses those validated snapshots, not the BSP's current RSP for an AP.
Resume follows a fully completed collection. A collector exception after admission
may have changed mark/free metadata: it keeps the heap locked, quarantines stopped
APs through their fault ISTs and fail-stops the BSP; it never resumes mutators.

Unknown CPU identity, missing/invalid snapshots or a failed stop notification
refuse collection **before mark/sweep**. A faulted AP's storage and roots remain
reserved; collection stays refused until reboot rather than freeing unobserved
references. A fault while owning the heap gate may require fail-stop; no attempt
repairs an interrupted allocator. Managed job exceptions can instead be caught,
reported and reaped without retiring that owner. Native exception links and catch
scratch are per registered thread, including BSP workers across preemption. Shared
exception-name metadata is serialized with publication ownership and publishes a
cache key only after its resolver is complete. Its constructors may not leave a
visible placeholder on failure. All early serial/GOP writers remain BSP-owned;
an unhandled AP dispatch failure uses the private native fault path.

AP-requested collection is deferred to the BSP pump. Fixed jobs are bounded and
can report allocation failure; this is not a general AP OOM/retry scheduler.
No AP code may create threads, block on managed monitors, call device/filesystem
services, acquire another owner's mutable state or mutate the UI. GC remains
nonmoving/conservative. Baseline x87/SSE preservation does not imply AVX support.

Managed image jobs use private arrays, then copy a complete tile into descriptor-
owned native storage. BSP reads require terminal success and exact dimensions;
cancelled, partial, faulted and stale-token frames are unavailable. Only the BSP
assembles a complete display frame and opens/paints its diagnostic window. The
`fps cpu` request runs on the UI tick; the AP never owns a Window or Surface.
This is a measured pilot, not parallelization of the entire compositor.

Short compute waits use a native-only BSP F6 completion doorbell and an
interrupt-shadow-safe predicate/HLT path. Registration requires the original
pinned IRQ246 gate to match before replacement. The handler preserves GPRs, uses
no FP or managed code, acknowledges APIC and returns without scheduler dispatch.
The existing BSP timer is a fallback, not a fabricated completion acknowledgement.
Ordinary sleeps retain their semantics. A callback borrows its permanent cancel
word only until return; shutdown publishes cancellation before waiting for halt.

Diagnostics (root, opt-in):

- `cpu managed-test`: retained chains, interior/POH references, allocation with
  repeated collection epochs, TLS/credential isolation, exceptions, cancellation
  and live BSP FP state across completion interrupts.
- `cpu managed-image-test`: identical pixels and work at 1/2/4 CPUs, with elapsed
  time including dispatch, completion waits and complete-frame copying.
- `fps cpu`: display a complete managed frame through the trusted UI owner.
- `cpu managed-hold` / `cpu managed-release`: park a retained-root diagnostic
  batch while BSP services run, then verify and reap it. Poweroff cancels it.
- `cpu managed-fault-test`: **disposable boots only**. Permanently faults an AP,
  proves missing roots refuse collection, and checks BSP/healthy sibling operation.
  Shut down or reboot afterward; normal reclamation remains unavailable.

The mandatory `native-managed-smoke.py` gate checks equal-work frames, actual
rendered pixels against an independent scalar oracle, focused input and shutdown.
The process gate retains managed AP roots through its foreground process and
64-pipeline/fault sequence. Distributor/installed, KVM/VMware and physical evidence
remain separate from local TCG observations.

## C4 production material computation and IRQ leases

The first production consumer uses native immutable snapshots rather than migrating
managed UI objects. MaterialComposition invokes CpuWorkers.TryBlur for changed
sampled regions of at least 16384 pixels and two or more tiles. Each AP owns one
256×64 tile payload until its terminal generation is reaped. The BSP copies a
four-pixel clamped halo from the whole region, not the tile boundary, then releases
the descriptor exactly once. Horizontal and vertical sums round at the same pass
boundaries as the scalar filter. No source array/pointer is retained in the native
payload; output is readable only after terminal success with the exact length.

Idle-owner selection rotates ties and skips busy, held, stopped, failed and retained
payloads. The BSP computes one tile while APs work. This is bounded load distribution,
not general thread migration. UI ownership, frame publication, credentials and
services remain on the BSP. A nonblocking producer gate prevents an unrelated CPU
diagnostic from blocking a frame's admission. Cancellation, timeout or failed copy
quarantines blur admission; accepted nonterminal buffers remain reserved, and the
caller overwrites the entire unpublished region through its scalar path. Failed
partial tiles never become an observable frame. Shutdown still requires native
halt acknowledgements. Allocation pressure also falls back without reusing a live
AP payload. Source and destination must be distinct, validated arrays.

The x64 copied InterruptManager is the central registry: SetHandler, AllocateVector
and FreeVector refuse AP entry before taking its global lock. Native CPU vectors
F1/F3/F4/F5/F6 are reserved from managed registration/freeing from boot onward,
including boots without worker activation. The exact pinned MSI allocator is
verified to stay within 40–EE. BSP timer EF and spurious FF retain their owners.
This lease check complements the native completion gate's original-handler/selector/
flags/IST validation; it does not silently overwrite an existing route.

`fps parallel on|off` is a root diagnostic switch, not a persisted user preference.
Its revision invalidates material captures so comparisons really use the selected
filter, while geometry, contrast, sampling and material preferences stay unchanged.
`cpu blur-test` verifies every pixel of an odd-sized region and guards, and reports
median elapsed time over five warmed samples including dispatch/waits/copying.
`fps parallel-bench` compares actual complete scene pixels and cold-capture timings;
its optional `fullscreen` argument temporarily resizes the top supported window on
the UI owner and restores its original state. `fps` includes AP tile/fallback counts.
The mandatory native-compositor-smoke gate requires both ordinary and fullscreen
scenes at 1/2/4 CPUs, exact pixels, focused public input and clean shutdown.

General queue migration, per-CPU protected processes, driver/service concurrency
and real installed I/O contention remain the rest of C4, as ordered in the
[component sequence](components/MULTICORE.md#c4--migration-and-broad-concurrency).
Default boots do not activate AP work, and this change makes no whole-application
multicore performance or external hypervisor/physical qualification claim.
