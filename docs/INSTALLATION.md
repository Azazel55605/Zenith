# Installation and boot generations

The x64 CLI installer is self-contained: Limine supplies the original kernel ELF
and the ISO's `BOOTX64.EFI` as read-only boot memory. Zenith validates architecture
and size before writing GPT, then copies and read-verifies those bytes itself.
The install target must have 512-byte sectors and at least 256 MiB capacity.
`install DISK --yes` erases that disk. The running root disk and mounted disks are
refused. Use disposable VM disks while hardware acceptance remains pending.

## Install workflow

1. Boot the ISO. The normal menu entry uses an existing Zenith root when found;
   select the live rescue entry to force a RAM root.
2. `install` lists eligible disks. Inspect `lsblk` and select the intended disk.
3. Run `install sata0 --yes` (substitute the actual device), then `sync; poweroff`.
4. Remove the ISO and boot the disk with x64 UEFI firmware. QEMU uses OVMF.

No host copying step is required. `tools/boot_image.py disk.img` independently
checks both GPT copies, partition identity, boot artifact hashes and menu binding
without modifying the image. The old `make-bootable.sh` name delegates to this
read-only check; it does not upgrade or repair old installations. The graphical
Sunrise installer, account setup wizard, BIOS installation and hardware/USB
acceptance remain future work. Existing filesystem/account behavior is retained.

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
| Installed `/boot/efi` | Mounted ESP for future update service |
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

Kernel updates are a required future package capability. This layout makes them
possible without replacing the running kernel's bytes: stage a new independently
addressed ELF, verify/flush it, retain the current generation, then publish a new
menu for the next boot. The running kernel remains active until reboot. A menu
builder already supports current, previous and RAM rescue entries; initial install
has no previous generation to retain.

The installer accepts ELF files up to 32 MiB and EFI files up to 2 MiB. Its 128 MiB
ESP budget accommodates three maximum-size ELF copies (current, previous, staging),
one EFI loader and metadata with headroom. This is capacity planning, not an update
space allocator. Future packages must still check actual free space and bounds.
The ext2 root size limit must grow before substantial SDK/runtime packages.

P1 packages remain signed scripts/data only. Kernel package type/metadata, versioned
ABI dependencies, conflict policy, signed artifact binding, ownership/protected
paths, activation/retention executor and interruption tests belong to P3. Bootloader
replacement should be a separate explicit operation. Do not implement kernel updates
by loosening script/data path restrictions or overwriting the current kernel path.
A failed new kernel must leave a manually selectable previous kernel and rescue
route. Automatic attempt counting/rollback, matching root snapshots and restoration
are not implemented; see [UPDATE-RECOVERY](UPDATE-RECOVERY.md).

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
