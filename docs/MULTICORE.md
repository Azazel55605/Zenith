# Multicore foundation

> Experimental native worker reference. Managed multicore remains planned. Work steps: [Multicore](components/MULTICORE.md).


The first slice is **experimental native integer workers**, selected only by the
x64 boot argument `smp=native`. Default boots still run managed work on one CPU.
This does not enable multicore managed threads or parallel native application
processes. The distributor can expose additional vCPUs, but ordinary kernel,
network/filesystem and package execution still uses the bootstrap CPU (BSP).

## Implemented boundary

A local Limine MP revision-0 request discovers processors without changing the
Cosmos dependency or upstream checkout. At most eight CPUs, including the BSP,
participate; legacy APIC IDs above 255 are excluded from this prototype. The BSP
publishes each secondary processor's kernel-owned context before releasing its
entry address. See the [Limine MP protocol](https://github.com/limine-bootloader/limine-protocol/blob/trunk/PROTOCOL.md)
and [request layouts](https://github.com/limine-bootloader/limine-protocol/blob/trunk/include/limine.h).

Each AP switches to its own 16 KiB static kernel stack, the captured BSP kernel
page-table root and a private worker descriptor/interrupt environment. The GDT and
IDT tables are shared immutable native tables; fault handling records that worker
as failed and halts it. These workers remain in ring 0 with interrupts disabled;
there is no TSS/ring transition, scheduler callback or managed entry on an AP.
They never allocate managed objects, access disks/network/UART, invoke application
syscalls or hold GC references. Limine bootloader memory is not reclaimed by the
current Cosmos usable-memory allocator. Contexts/stacks/tables remain kernel-owned
for the whole boot; failed or timed-out workers are not restarted.

`cpu status` reports detected and started CPUs, the managed CPU count and whether
interrupts were enabled at the initial IDT load. Root `cpu irq-test` reloads the
current valid IDT under both IF states and verifies each state is preserved.
Cosmos 3.0.89 unconditionally enabled interrupts at IDT installation; a
signature-checked build-copy import now selects Zenith's IF-preserving loader.
Platform/system initialization still explicitly enables interrupts before storage
I/O. This prevents the early window during legacy PIC remapping that produced a
reported fatal vector 0 on installed nested-KVM boot.
Root can run `cpu test [iterations]` (default 2,000,000; range 1..10,000,000).
A serialized native job computes the same bounded integer checksum on each AP
and the BSP. A readiness barrier precedes release. The command checks actual
CPUID/APIC identity, checksum and completion, then reports per-CPU TSC intervals
and their overlap. TSC interval overlap is a QEMU diagnostic, not a portable
cross-platform clock synchronization or performance guarantee. Startup, readiness
and completion waits have five-second managed deadlines; failure disables future
jobs. There is no arbitrary task/function-pointer submission API.

**Idle APs poll with `pause` and consume host CPU.** This is why activation is
explicit and defaults remain unchanged. Boot without `smp=native` to avoid that
cost. This slice proves independent execution and ownership, not an efficient
production scheduler. The existing protected foreground process bridge stays on
the BSP and retains its singleton descriptor/context contract.

## Acceptance

After building a Release ISO:

```sh
python3 -B tools/native-cpu-smoke.py /tmp/zn-native-cpu
python3 -B tools/native-process-smoke.py /tmp/zn-native-process --cpu-workers
```

The first harness clones the ISO and adds the opt-in argument only to that clone.
It boots 1/2/4-vCPU disposable guests plus a default-disabled four-CPU guest,
checks distinct CPU identities, repeated correct concurrent results, limits,
refusals, shell responsiveness and clean poweroff. The second installs a signed
native text package on disposable ext2 with active AP workers, repeats contained
application faults/timeouts and verifies workers afterward. It also checks native
upgrades, credentials, manuals/fonts, desktop rendering and independent ext2 audit.
Actual KVM/VMware and physical multiprocessor acceptance remain separate.

## Next slices

1. Replace idle polling with a per-CPU interrupt stack and safe wakeup/IPI protocol.
   Add bounded queues, job cancellation and clean quiescence before shutdown.
2. Make scheduler interrupt/context state per-CPU: current thread, stacks, GDT/TSS,
   FP state, timer and reschedule IPIs. Cosmos currently returns CPU 0 and shares
   context-switch globals. Additional QEMU CPUs alone cannot change that.
3. Establish allocation and GC synchronization: stop all participating managed
   CPUs, preserve register/stack roots and resume safely. Local IRQ masking is
   insufficient for this barrier.
4. Enable pinned managed threads first, then migration/load balancing. Audit
   credential context, locks, drivers, filesystem/package transactions and the
   existing foreground process bridge before distributing those operations.
5. Verify 1/2/4-CPU fault, GC, contention, I/O and reboot/recovery workloads. Only
   then make multicore application scheduling part of the ordinary boot path.
