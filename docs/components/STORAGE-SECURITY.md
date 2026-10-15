# Storage and security

[Documentation](../README.md) · [Current status](../STATUS.md)

**State:** M2 complete for the supported ext2 profile. Installed VM usage is
operator confirmed; larger disks/features and physical hardware are separate work.

## Implemented steps

- [x] Provide RAM live root, installed ext2 root, FAT32 ESP and virtual proc/dev.
- [x] Implement supported-profile ext2 allocation, resize/delete, rename/directory
  moves, bounded symlinks and numeric ownership/mode metadata.
- [x] Add guarded formatting/mounting and read-only consistency checking; refuse
  inconsistent roots instead of mounting them writable.
- [x] Add users, salted password records, hidden authentication and caller credentials.
- [x] Enforce traversal/read/write, groups, sticky/setgid and privileged operations
  across shell, GUI, System.IO, direct inode and held-handle paths.
- [x] Recheck caller access through the native read-only filesystem bridge.

## Current boundary

New installed roots are bounded to **512 MiB**. The formatter, checker, inode/memory
budgets and mapping rules must change together before raising that cap. ext2 is
not journaled. `sync` flushes data; a completed unmount marks the volume clean.
`fsck.ext2` checks supported unmounted volumes; repair remains host-side e2fsck.
The VFS rejects parent traversal (`..`) and uses deferred namespace deletion while
handles remain open. These are explicit compatibility limits.

FAT cannot supply persisted Unix permissions. Existing kernel-linked code remains
trusted; access enforcement does not isolate arbitrary kernel code. Protected
native processes have the separate boundary described in [userland](USERLAND.md).

## Remaining steps

- [ ] Design a larger supported storage profile with memory/inode/installer budgets.
- [ ] Choose journal/repair or generation-based recovery policy before claiming
  general crash durability.
- [ ] Extend filesystem semantics only with traversal, symlink and held-handle tests.
- [ ] Establish physical device compatibility and recovery procedures.

## Source and acceptance

Owners: [storage](../../src/Core/Storage), [security](../../src/Core/Security),
[native VFS adapter](../../src/Core/Processes/NativeProcessFiles.cs).
Contracts: [architecture storage section](../ARCHITECTURE.md#storage-and-installation),
[accounts](../ACCOUNTS.md), [installation](../INSTALLATION.md).

Use [security smoke](../../tools/security-smoke.py),
[filesystem/desktop smoke](../../tools/smoke-test.py) and
[native process smoke](../../tools/native-process-smoke.py) for relevant changes.
Require allow/deny checks as ordinary user and root, reboot persistence, clean
shutdown and independent host filesystem audit on disposable images. Recorded
runs are in the [execution archive](../archive/2026-10-15/PROGRESS.md).
