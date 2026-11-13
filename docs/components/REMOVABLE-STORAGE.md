# Removable storage (USB drives, card readers)

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md)

Work IDs below are detailed steps of the master tracker (S6). **Design only: nothing here is
implemented yet.** Decisions marked *sign-off* need the operator's choice before work starts.

## What exists today

- **Driver support is already in the pinned Cosmos 3.0.89.** It has an xHCI host controller,
  hubs and a USB mass-storage class driver (SCSI over Bulk-Only Transport: sticks, card readers,
  USB disks). It also handles hot-plug. `StorageManager` registers a unit when it is plugged in
  (`usb0`, `usb1`...) and scans its partitions. When the unit is pulled out, `StorageManager` drops
  it and detaches its mounts **without a flush**, because the device can no longer take writes.
- **Zenith has never tested any of this.** No gate boots with `qemu-xhci` and `usb-storage`, and
  nothing in Zenith tells a USB disk apart from an internal one. A USB disk present at boot would
  already show in `lsblk`, `disk` and Equinox, and Zenith would treat it like a second SATA disk.
- **The device table holds at most 8 disks**, internal and USB together.
- **Filesystems:** ext2 read/write (Zenith's driver) and FAT32 (Cosmos's driver). Most sticks over
  32 GB come formatted as exFAT, which Zenith cannot read. NTFS is not supported either.
- **Mounting** is root-only. As of kernel 0.0.56 it is also available through Equinox › Mounts,
  and `/etc/fstab` lines are mounted at startup. A partition that is missing at startup is skipped,
  so an fstab line for a stick that is not plugged in never stops the boot.

## Gaps that matter before anyone plugs in a stick

1. **Revision on hot-plug.** `DiskService.Revision` changes only after an edit. A plan written
   before a stick is swapped for another one can still name the same `usb0`. The revision must
   include the device table's generation, so that Equinox's plan is refused and redrawn.
2. **Surprise removal.** Writes still in flight are lost. ext2 is left marked "not clean".
3. **Removing the boot medium.** When Zenith runs live from a USB stick, removing it is fatal. The
   disk holding the root (and `/boot`) must be marked as one that cannot be ejected.
4. **Disk names are reused.** After unplugging `usb0` and plugging in another stick, the new one is
   `usb0` too. Anything that remembers a disk must use its identity (GPT partition GUID, ext2 UUID,
   FAT volume serial), never its name. `/etc/fstab` written by Equinox already uses PARTUUID.

## Proposed behaviour

**Attach.** The kernel notices a new disk on its own: the device table changes and the generation
moves. It logs the disk, bumps the disk revision and posts a desktop notification such as "USB
drive (14.9 GB) connected".
- *sign-off:* mount it automatically in a graphical session, or only on request?
  - Recommended: **automatic, for the signed-in user**, as GNOME and KDE do.
  - Never automatically on a headless system or at the login screen.

**Where it mounts.** *sign-off:* `/media/<label>` (Linux desktop convention, separate from
administrator mounts in `/mnt`). Recommended. If two drives share a label, the second one gets
`-2` appended.

**Who owns the files.** FAT has no owners. Files on it appear as the signed-in user's, mode `0700`
for folders and `0600` for files. ext2 keeps its own owners. The automount happens on the user's
behalf: the kernel performs it, and the user can eject only what was automounted for them. Root
can do everything, as today.

**Safety options on removable mounts.**
- **No running programs** from them (`noexec`-like). There is no autorun, ever.
- Packages on a stick install through `pkg install`, which checks signatures, so that stays safe.
- **Sync on close:** every file closed on a removable volume is flushed. Copies are slower, but
  pulling the stick out loses at most the file still being written.

**Eject** is a new disk action, number 8 in zenith-native-disk-abi:
1. Refused for the disk holding the root or `/boot`.
2. Syncs, then unmounts every partition of the disk, deepest mount first.
3. Refused (and nothing unmounted) when a program still has a file open there. It names the
   program, using Observatory's process list.
4. Optionally sends SCSI START STOP UNIT, then shows "Safe to remove".

**Surprise removal.** A notification says the drive was removed without ejecting and that recent
changes may be lost. The next time it is plugged in, an ext2 volume left "not clean" mounts
**read-only**, with "Check and repair" in Equinox (`fsck.ext2` exists).

**Where it shows up.**
- **Taskbar:** a USB indicator appears while removable volumes are mounted. Its popover lists
  them with **Open** (a Polaris tab at the mount point, until there is a file manager) and
  **Eject**.
- **Equinox:** removable disks get a USB icon and an Eject button. On the Mounts page, removable
  volumes are listed as automounted and are never offered "Mount at every startup" by default. An
  fstab line for a stick stays possible (it is matched by PARTUUID and skipped when absent).
- **Observatory:** the Disk card already lists every disk, so USB disks appear there with their
  rates.
- **Sunrise:** it lists USB disks as removable with a warning, and never offers the stick it booted
  from as the install target.

**Formatting.** Equinox formats ext2 today. Removable drives need **FAT32** for exchange with other
systems, so a FAT32 format action is needed. exFAT read support comes later: Microsoft published the
specification in 2019. Licensing and the driver are a separate decision.

## Kernel and ABI changes, when this is built

- Disk snapshot: `Z_DISK_REMOVABLE` and `Z_DISK_EJECTABLE` flags (the disk with the root is not
  ejectable), plus the device-table generation folded into `revision`.
- Disk request: action 8 (eject). Mount requests from a non-root session user are accepted only
  for removable volumes, and only under `/media`.
- A storage event the desktop polls each tick (a change of the generation), feeding notifications
  and the taskbar indicator. It is a cheap reference compare, with no new thread.
- Mount options in the VFS layer: no running programs from the volume, and sync on close. These
  are Zenith's own (GuardedFilesystem), with no Cosmos changes.

## Stages

1. **R1 qualify what exists:** a gate with `qemu-xhci` and `usb-storage`, covering:
   - a stick present at boot (listed, mounted, files read and written);
   - hot attach and detach through the QEMU monitor (`device_add` / `device_del`);
   - surprise removal while mounted: no crash, the mount is gone, and the revision moves.
2. **R2 kernel policy:** removable flags, revision generation, eject, read-only for a volume that
   is not clean, sync on close and no-exec.
3. **R3 desktop:** automount for the session user, notifications, the taskbar indicator, and
   Equinox Eject.
4. **R4 formats:** FAT32 formatting in Equinox; exFAT read support after its own decision.

Not planned: NTFS, MTP phones, optical drives, UASP (Cosmos has Bulk-Only Transport only).
