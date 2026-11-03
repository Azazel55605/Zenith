# Multicore foundation

Work owners: **C1–C4** in the [master roadmap](ROADMAP.md). This is the execution
contract; [component steps](components/MULTICORE.md) own implementation detail.

## Activation and boundary

The x64 boot argument `smp=native` starts **isolated native workers**. Ordinary
boots leave them disabled. Up to eight CPUs including the BSP participate; Limine
MP revision 0 supplies topology and AP entry. Legacy APIC IDs above 255 are excluded.
This is not managed SMP: managed scheduling, GC, device/filesystem services, the
trusted UI owner and the protected foreground application bridge stay on the BSP.
Additional vCPUs alone do not distribute ordinary application work.

The AP runtime is `src/Native/Cpu/workers.c`, entered through
`src/Native/x64/CpuWorkers.s`. It never allocates, enters managed code, retains GC
references, invokes application syscalls, writes UART or accesses disks/network.
`tools/build-cpu.py` builds freestanding general-registers-only code, with no red
zone or host-library dependency. Import and instruction regressions are host-tested.
Nothing edits the installed Cosmos packages or sibling upstream checkout.

## CPU ownership and interrupt-driven idle

Each AP has a permanent 16 KiB work stack, private GDT/TSS, 8 KiB interrupt stack
(IST1) and 8 KiB fault stack (IST2). Its IDT uses a shared immutable native table.
APs share the BSP kernel CR3 but never the managed scheduler/interrupt dispatch.
Their local device/timer LVT entries are masked. Wake vector `0xf1` and spurious
vector `0xff` have native-only handlers; the spurious path issues no EOI.

AP code runs with IF clear. Empty queues and held jobs recheck their predicates
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
Managed AP threads, allocator/GC barriers and migration remain **C2–C4**.

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
