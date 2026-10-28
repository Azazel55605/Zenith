# Install, boot and supported hardware

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md)

Work IDs below are detailed steps of the master tracker, not a separate schedule.

## Implemented boundary

UEFI installation creates exact GPT identities, FAT32 boot artifacts and an ext2
root with account setup. Signed kernel staging retains previous artifacts and
supports manual previous boot, confirmation/rollback and durable undo. Installer,
package and kernel mutations share maintenance exclusion. External ISO recovery
and interrupted-update fixtures exist. This is kernel recovery, not root snapshots
or automatic failed-boot fallback.

## U4 / H1 — Installation frontends and device support

- [ ] Native Sunrise wizard over typed root/live-only target and account services;
  preserve pre-erase validation, destructive confirmation and secret handling.
- [ ] Publish the tested QEMU/VMware device matrix and recovery procedure.
- [ ] Exercise USB/physical boot, storage, display, input/network and recovery on
  identified hardware before claiming physical install support.
- [x] Reproduce a stranded virtio-mouse queue with the server ELF/QEMU version;
  implement bounded IRQ-scoped periodic polling in copied dependencies.
  [Qualified evidence](../archive/2026-10-28/virtio-mouse-drain.md): clean source
  `5306703` passes all ten native gates and the exact QEMU 10 restricted-CPU
  reproducer. The operator's fresh standalone distributor Settings gate passed
  on 2026-10-29; Q2 still owns the parallel release retry and installed confirmation.
- [ ] Investigate driver-specific relative/absolute pointer, hardware cursor and
  damage presentation work from measured failures/performance.

## R2 / R3 — Whole-system recovery

Define kernel/root/package/config/account/home/service scope, choose a capacity-
budgeted backend, then implement consistent generations, trial/restore/retention.
Do not choose Btrfs merely from the requested user experience. Prove interrupted
activation, failure to boot, incompatible schemas and external recovery.

## Source and evidence

[Installer/storage](../../src/Core/Storage), [boot](../../src/Core/Boot),
[installation guide](../INSTALLATION.md), [kernel updates](../KERNEL-UPDATES.md),
[future matched recovery](../UPDATE-RECOVERY.md).
All new kernel tags require clean-source full qualification (Q2); old 0.0.13
acceptance requests are superseded, not open tasks.
