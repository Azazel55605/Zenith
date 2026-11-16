# C5.1 lock audit (2026-11-15)

[Multicore component page](../../components/MULTICORE.md#c5--general-smp) · [Roadmap C5](../../ROADMAP.md)

Pinned Cosmos 3.0.89 sources (`../Cosmos/src`), read only. Question per site: does it exclude
another CPU, or only an interrupt on the same CPU?

## Already SMP-safe

- `Scheduler/SpinLock`: `Interlocked.CompareExchange` acquire, `Exchange` release. `AcquireIrqSafe`
  masks interrupts and takes the lock, so it excludes both the local ISR and other CPUs.
- Users of `AcquireIrqSafe`: `ConditionVariable`, `Mutex`, `InterruptEvent`, the scheduler policy
  swap (`SetScheduler`), and the drivers `VirtioGpu`, `VirtioNet`, `NvmeController`, `XhciController`.
  Managed `lock`/`Monitor` blocks through these primitives, so blocking and waking are already guarded
  by real locks.
- Heap, GC, TLAB publication: masked sections, but C3 put all allocation/header publication and
  unmanaged heap/page entries under one recursive native ownership gate with IRQ preservation, and
  collection stops every registered participant (`AllocationPublicationFixup`, `RuntimeHeapFixup`,
  `GcBarrierFixup`).

## Masking only: must become a lock under C5.2

| Site | Protects | Change |
|---|---|---|
| `SchedulerManager` `CreateThread`, `ReadyThread`, `BlockThread`, `ExitThread`, `YieldThread`, `MarkSleeping`, `RegisterThread`/`UnregisterThread`, `GetBusyCpuTimeNs` | thread registry, policy run queues, thread state | one IRQ-safe scheduler spinlock (global first; per-CPU queue locks later if contention shows) |
| `SchedulerManager.OnTimerInterrupt` / `CheckSleepingThreads` | walks every thread and readies expired sleepers | take the scheduler lock; only the BSP tick (or one elected CPU) wakes sleepers |
| `SchedulerManager.ScheduleFromInterrupt` | "No lock needed - interrupts are already disabled" | take the scheduler lock around `PickNext`/requeue |
| `libSystemNative` thread creation | thread id allocation, `CpuId = 0` | scheduler lock; initial CPU from `SelectCpu` instead of 0 |
| `KeyboardManager` queue (`Enqueue`, `TryReadKey`, `Peek`) | IRQ producer versus reader | IRQ-safe spinlock (the IRQ runs on the BSP, readers may run anywhere) |
| `Diagnostics/Log`, `Graphics/KernelConsole` | log ring and boot console state | IRQ-safe spinlock |
| `HAL/Devices/Timer/TimerDevice` | timer reprogramming | BSP-owned device: calls from an AP are queued to the BSP or take an IRQ-safe lock |
| `LibraryInitializer` | boot-time initialization | runs before APs start; unchanged |

## Single-CPU assumptions besides masking

- Done in C2: `tools/adapt-cpu-interrupts.py` moves `_context_switch_target_rsp`, `_context_switch_is_new_thread`,
  `_temp_is_new_thread` and `_irq_last_error_code` into per-CPU storage, so each CPU stages its own switch.

- `SchedulerThread.CpuId` is fixed at creation (0); `StrideScheduler.OnThreadMigrate` exists but
  nothing calls it. Migration and idle stealing are new in C5.2.
- `SchedulerManager.GetCurrentCpuId` already returns the hardware logical id (`CpuIdentityFixup`);
  the dispatch CPU count stays 1 (`BootCpuFixup`, `PinnedCpuFixup`). C5.2 raises it to the online
  count under `smp=managed`.
- AP interrupts use Zenith's native IDT (C1); the managed timer path runs only on the BSP. C5.2 needs
  an AP timer vector that enters `OnTimerInterrupt` for that CPU with the full saved frame, like the
  BSP IRQ stub, and a reschedule IPI for cross-CPU wakes (vector F3 exists for native contexts).
- Drivers expect their ISR and their callers on one CPU. Device interrupts stay routed to the BSP;
  any driver entry reachable from another CPU must already hold its own IRQ-safe lock (the virtio,
  NVMe and xHCI drivers do; the PS/2 keyboard queue, AHCI/IDE and e1000 paths need checking in C5.3).

## Zenith kernel code

No Zenith source masks interrupts as a lock (`DisableInterrupts*`: 0 uses). Shared state is already
guarded with `lock` because threads are preempted on the BSP today; that stays correct with more
CPUs. Places that rely on running only on the BSP: the raw process bridge (`s_rawGate`), the
desktop/UI owner, `CpuWorkers` producer paths and anything that touches the framebuffer or device
registers. They keep BSP affinity in C5.3.
