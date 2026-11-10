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

New installs use an ext2 root over the rest of the disk (4 KiB blocks, up to
2 TiB, files past 4 GiB); roots installed before 2026-11-10 keep their 512 MiB
1 KiB profile until migrated. ext2 is not journaled. Dirty-root read-only
checking can refuse writable startup; repairs remain host-side. Parent `..`
traversal and some general filesystem semantics remain limited. FAT cannot persist
Unix permissions. Trusted kernel code is not isolated by VFS policy.

## R1–R3 — Capacity, semantics and recovery

- [x] Larger profile (2026-11-10, local): shared geometry; 4 KiB blocks with
  sparse_super/large_file up to 2 TiB, inodes scaled to about 4 M at 2 TiB;
  triple-indirect mapping; descriptor updates write one block; a checker at two
  bits per block bounded by free memory; whole-disk installer root. Host tests
  compare with e2fsck/debugfs; `large-ext2-smoke.py` in the release plan.
- [ ] Existing 512 MiB roots: grow in place or migrate to the 4 KiB profile. A
  1 KiB root's descriptor table is full at 512 MiB, so growing it relocates group
  metadata; copying the root to a new 4 KiB partition is the simpler bridge.
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
