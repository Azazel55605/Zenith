# Installation, boot and recovery

[Documentation](../README.md) · [Current status](../STATUS.md)

**State:** self-contained VM installation and signed kernel generations work.
Kernel 0.0.12 is operator installed; 0.0.13 acceptance is pending. Whole-system
snapshots remain planned.

## Implemented steps

- [x] Supply original kernel/EFI artifacts from the live ISO; no host boot-file injection.
- [x] Guide erase/account setup through Sunrise and serial, sharing installer logic.
- [x] Write GPT, FAT32 ESP and supported ext2 root; verify copied artifacts and menu.
- [x] Select installed roots by exact PARTUUID; provide explicit RAM rescue and
  refuse missing/ambiguous selected roots.
- [x] Verify signed kernel packages, stage versioned artifacts and retain previous.
- [x] Activate on reboot; require confirmation before stacking another update.
- [x] Support explicit rollback, tested interrupted-activation undo and manual
  previous-entry recovery after a genuinely broken signed kernel.
- [x] Sync/unmount/flush on Zenith reboot and poweroff commands.
- [x] Validate recovery through actual graphical/local login as well as serial.

## Operating boundary

Replacing kernel files does not change the loaded kernel until reboot. Old and new
kernels share root data. Previous-kernel boot does not undo package/configuration
changes; an older kernel can be incompatible with newer installed package records.
Return to a compatible retained kernel before package operations in that case.
No automatic failed-trial fallback or matched root snapshot is implemented.
Hypervisor reset/power loss is not equivalent to Zenith's clean power actions.

## Remaining steps

- [ ] Accept kernel 0.0.13 through all distributor gates and the installed VM.
- [ ] Define saved-root scope: kernel, system files, package DB, config, mutable data.
- [ ] Select a root-generation backend and retention/capacity policy.
- [ ] Implement trial boot/restore only after consistent activation and failure tests.
- [ ] Establish USB/physical hardware install and recovery acceptance.

## Source and acceptance

Owners: [boot](../../src/Core/Boot), [storage/installer](../../src/Core/Storage).
Use [installation guide](../INSTALLATION.md), [kernel update guide](../KERNEL-UPDATES.md)
and [future snapshot design](../UPDATE-RECOVERY.md).

[Guided installation](../../tools/guided-install-smoke.py),
[kernel update](../../tools/kernel-update-smoke.py) and
[login/session](../../tools/login-session-smoke.py) harnesses exercise disposable
UEFI disks. Preserve exact running-kernel hashes, real authentication, denied early
confirmation, interruption/broken-entry tests and independent ext2/FAT audits.
The recovery fix at `61618cd` passed 169 kernel and 69 login checks; see
[record](../archive/2026-10-15/PROGRESS.md#2026-10-15--displayed-kernel-recovery-validation-locally-verified).
