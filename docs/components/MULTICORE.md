# Multicore execution

[Documentation](../README.md) · [Current status](../STATUS.md)

**State:** experimental opt-in native integer workers. Ordinary managed work,
drivers and foreground process services still run on the BSP. More QEMU vCPUs
do not schedule Zenith's managed tasks across them.

## Implemented steps

- [x] Discover processors through Limine MP and start bounded native AP workers.
- [x] Give workers kernel-owned static stacks/context and no managed object access.
- [x] Verify CPU identity, bounded integer jobs and completion/failure reporting.
- [x] Preserve IF when loading the x64 IDT; test early installed boot handoff.
- [x] Keep workers disabled by default; exercise explicit `smp=native` boots.
- [x] Exercise protected foreground tools/faults with workers active.

Workers poll while idle and consume host CPU. They do not run the managed
scheduler, GC, filesystem/network drivers or application syscalls. The measured
server's nested KVM storage benchmark was slower than TCG; that deployment uses
an explicit measured backend policy. This does not prove a universal KVM defect.

## Remaining steps, in dependency order

1. Per-CPU interrupt stacks, safe IPI/wakeup and bounded cancellable queues; quiesce
   workers before shutdown instead of idle polling.
2. Per-CPU scheduler/current-thread/context/GDT/TSS/FP/timer state.
3. Cross-CPU allocation/GC stop-and-root synchronization and audited locks.
4. Pinned managed threads, then migration/load balancing and service credential audit.
5. Concurrent I/O, memory pressure, faults, cancellation and reboot/recovery acceptance
   before enabling managed multicore on ordinary boots.

## Source and acceptance

Owners: [multiprocessing](../../src/Core/Multiprocessing), [native x64](../../src/Native/x64).
Exact contract: [multicore guide](../MULTICORE.md).
[CPU smoke](../../tools/native-cpu-smoke.py) covers default-disabled and opt-in
1/2/4-vCPU guests. [Native process smoke](../../tools/native-process-smoke.py) with
`--cpu-workers` checks protected tools with AP workers. Latest filesystem gate with
workers passed locally at `f502085`; [record](../archive/2026-10-15/PROGRESS.md#2026-10-15--native-filesystem-tool-unbundling-locally-verified).
Physical multiprocessor and managed multicore acceptance remain unclaimed.
