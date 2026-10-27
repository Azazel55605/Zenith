# Storage and account security

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md)

Work IDs below are detailed steps of the master tracker, not a separate schedule.

## Implemented boundary

RAM live root, bounded installed ext2, FAT32 ESP, proc/dev and private temporary
storage work. Accounts, hidden authentication, ownership/groups/modes, sticky/setgid,
symlinks and held-handle authorization are enforced. Complete ext2 operations are
serialized per volume with private metadata buffers; the old inode corruption fix
has been followed by later operator-installed kernels, so it is not a pending
0.0.13 deployment blocker.

The root profile remains **512 MiB**; ext2 is not journaled. Dirty-root read-only
checking can refuse writable startup; repairs remain host-side. Parent `..`
traversal and some general filesystem semantics remain limited. FAT cannot persist
Unix permissions. Trusted kernel code is not isolated by VFS policy.

## R1–R3 — Capacity, semantics and recovery

- [ ] Measure a larger profile; change formatter/checker/inode/memory/mapping and
  installer budgets together. A constant increase is not capacity support.
- [ ] Define crash repair/journal versus generation policy and mutable-data scope.
- [ ] Extend traversal/symlink/link/rename/held-handle semantics only with auth and
  interruption/concurrent-I/O regression tests.
- [ ] Validate trial/restore, retention, out-of-space/power-loss and damaged ESP/root
  on disposable generations; physical-device coverage belongs to H1.

## Source and acceptance

[Storage](../../src/Core/Storage), [security](../../src/Core/Security),
[accounts](../ACCOUNTS.md), [installer](../INSTALLATION.md),
[recovery design](../UPDATE-RECOVERY.md).
Preserve ordinary/root denials, reboot persistence, clean shutdown and independent
ext2 audits. [Corruption investigation](../archive/2026-10-16/kernel-update-ancestor-investigation.md)
is historical evidence, not an open retry queue.
