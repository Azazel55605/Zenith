# Installation and boot generations

> Operating guide for the implemented VM installer. Work steps: [Boot/recovery](components/BOOT-RECOVERY.md).

## On this page

- [Install workflow](#install-workflow)
- [Installed structure](#installed-structure)
- [Kernel packages and reboot activation](#kernel-packages-and-reboot-activation)
- [Validation](#validation)

The x64 installer is self-contained: Limine supplies the original kernel ELF
and the ISO's `BOOTX64.EFI` as read-only boot memory. Zenith validates architecture
and size before writing GPT, then copies and read-verifies those bytes itself.
The install target must have 512-byte sectors and at least 256 MiB capacity.
`install DISK --yes` erases that disk. The running root disk and mounted disks are
refused. Use disposable VM disks while hardware acceptance remains pending.

## Install workflow

1. Boot the ISO. The normal menu entry uses an existing Zenith root when found;
   select the live rescue entry to force a RAM root.
2. Open **Sunrise** from the launcher (or `open Sunrise`). Choose a disk, enter
   hostname, user name, user password and root password with confirmations, then
   review the target and explicitly confirm erasure. Tab/Shift+Tab move through
   fields; Enter advances, Space toggles the erase checkbox, Escape goes back.
3. In headless mode use `install sata0 --setup` (substitute the actual device).
   It prompts for the same details with hidden passwords and requires typing
   `ERASE sata0`. Cancellation or invalid setup leaves the target untouched.
4. After completion run `sync; poweroff`, remove the ISO and boot the disk with
   x64 UEFI firmware. QEMU uses OVMF. Log in as your chosen user or root.

Sunrise and `--setup` require root in a live RAM session; choose live rescue if an
installation is already attached. Passwords are salted/hashed before disk erasure.
Opening Sunrise on an installed root displays a live-session requirement, not an
installation health assessment. Older builds incorrectly label this blocked launch
"Installation did not complete"; that message alone does not mean installation failed.
Use `boot status` and a disk-only reboot/persistence check to inspect your installation.
Target passwd/group/shadow records are generated together; shadow is created with
mode 0600 and the initial user gets uid/gid 1000 and a private 0700 home. The live
session's identity remains unchanged. Configuring root disables automatic root login
on the installed system. `install DISK --yes` remains the low-level copy workflow
for existing tests; it does not prompt for new credentials. GUI copy progress runs
on a worker, overlapping installations are refused, and normal shutdown/window
closing is blocked during writes. No interruption-safe update transaction is implied.

No host copying step is required. `tools/boot_image.py disk.img` independently
checks both GPT copies, partition identity, boot artifact hashes and menu binding
without modifying the image. The old `make-bootable.sh` name delegates to this
read-only check; it does not upgrade or repair old installations. BIOS installation and hardware/USB
acceptance remain future work.

## Installed structure

| Location | Contents |
|---|---|
| ESP, 128 MiB FAT32, starting at 1 MiB | Firmware-visible boot files |
| ESP `/EFI/BOOT/BOOTX64.EFI` | Limine removable-media UEFI entry point |
| ESP `/boot/zenith/<sha256>/kernel.elf` | Original ELF, generation ID is SHA256 of its bytes |
| ESP `/boot/zenith/<sha256>/manifest.tsv` | Installed generation identity and artifact sizes |
| ESP `/boot/zenith/manifest.tsv` | Current installed boot inventory |
| ESP `/limine.conf` | Current kernel/root and RAM rescue menu |
| Root `/var/lib/zenith/boot/installed.tsv` | Matching initial installation inventory |
| Root `/etc/fstab` | Root/ESP PARTUUID records and volatile `/tmp` |
| Installed `/boot/efi` | Mounted ESP for signed kernel updates |
| Root ext2 | `/etc`, `/home`, `/root`, `/usr`, `/var`; maximum 512 MiB |

Root begins after the ESP and uses remaining aligned space up to the current ext2
limit; larger disks retain unallocated space. `/tmp` remains RAM-backed ext2.
An explicit `root=PARTUUID=<GPT UUID>` chooses one exact installed root; missing,
damaged or duplicate identity fails closed. `kernel=<sha256>` checks that the
loaded ELF matches the menu's generation. Default ISO `root=auto` retains legacy
discovery; `root=live` ignores installed roots and starts a RAM session.
`boot status` reports the loaded kernel ID, root UUID and ESP availability.

The install writer flushes and verifies boot artifacts before writing the boot
menu. This is initial destructive installation, not a power-loss-safe update
transaction. Failed installation can require repeating installation. Ext2 has no
journal; unclean shutdown may require host e2fsck before writable mounting.
Legacy roots remain discoverable, but are not automatically migrated to this layout.

## Kernel packages and reboot activation

Signed kernel updates are implemented through a dedicated container and coordinator:
`pkg kernel check|install FILE`, `boot status`, and `boot confirm|rollback|recover`.
The loaded kernel stays unchanged until reboot; the previous generation and RAM
rescue remain available. See [kernel update guide](KERNEL-UPDATES.md) for signing,
delivery, explicit confirmation, recovery and a real-test procedure.

P1 remains signed scripts/data only. Kernel packages cannot replace the EFI loader,
change root/account files or install arbitrary payload paths. Root snapshots,
automatic boot-attempt rollback and general FAT/ext2 power-loss recovery remain
separate work. Offline VM acceptance does not establish physical hardware readiness;
retain the external live ISO and verify an upgrade on a disposable installation first.

The installer accepts ELF files up to 32 MiB and EFI files up to 2 MiB. Its 128 MiB
ESP accommodates current, previous and staging kernels with metadata headroom;
the updater still checks actual free space. Larger SDK/runtime packages need a
larger supported ext2 root profile.

The local boot bridge follows the upstream [Limine protocol](https://github.com/Limine-Bootloader/limine-protocol/blob/trunk/PROTOCOL.md).
Its internal module uses `/EFI/BOOT/BOOTX64.EFI` on the executable's boot volume;
absence rejects installation before disk erasure. The bridge is x64-only; ARM64
entry points return no assets. Secure Boot authentication is not established by
boot inventory hashes. These hashes bind files and detect mismatches, not trust.

## Validation

`python3 tools/install-smoke.py obj/install-smoke` installs on a new disposable
image, verifies the guest-written GPT/EFI/ELF independently, boots without an ISO,
checks mounted root/ESP and file persistence across a second UEFI boot, and starts
RAM rescue with the installed disk attached. It performs no host boot-file repair.
The test also attaches a duplicate installation and checks that root UUID ambiguity
stops startup rather than selecting a different root. The broader desktop, headless
and package suites also boot guest-written images.
Exact local results and environment limits are recorded in [PROGRESS](PROGRESS.md).

`python3 tools/guided-install-smoke.py gui obj/guided-gui` and the equivalent
`headless` mode verify rejected input/erasure, secret-free transcripts, account setup,
private shadow/home attributes, actual logins and persistent data across two disk-only
UEFI boots. The GUI run captures disk/review/progress/completion screenshots.
