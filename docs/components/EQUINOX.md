# Equinox

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md)

Work IDs below are detailed steps of the master tracker (U8), not a separate schedule.

Equinox is the partition manager: disks, partitions and free space, drawn twice, as they are and
as the plan will leave them, in the spirit of GParted and KDE Partition Manager.
[Concept](../design/equinox-concept.svg). It is a native package, `zenith-equinox`, built from the
shared toolkit (`userland/ui`), over the same rules as the `disk` command (`man disk`).

## The boundary

The kernel owns every rule; Equinox plans and shows. `DiskService`
([source](../../src/Core/Storage/DiskService.cs)) is shared by `disk` and the ABI:

- Edits touch GPT disks of 512-byte sectors with nothing mounted. Creating a partition in free
  space of the running system disk is the one exception; it appears after a restart. The root is
  grown from the rescue entry of the boot menu.
- Partitions start on 1 MiB boundaries and have whole-MiB lengths; nothing goes in the first MiB or
  the last 34 sectors. Both GPT copies are rewritten at the real disk end after each change.
- Grow extends a partition into the free space after it, then its ext2 volume (offline, using the
  reserved descriptor blocks of the 4 KiB profile), then checks it. The grow is planned on the
  enlarged range before the table changes. Shrinking and moving are not supported.
- New partitions are formatted ext2: the compact 1 KiB profile below 64 MiB, otherwise 4 KiB
  blocks with room to grow to 2 TiB.

## Native ABI: `zenith-native-disk-abi` 1.0

[Header](../../userland/include/zenith_disks.h).

| Call | Arguments | Result |
|---|---|---|
| 647 | 16384-byte buffer, 16384, 0 | Snapshot: up to 8 disks with 16 partitions (name, start, length, grow limit, type, filesystem, block size, role, label, mount) and 8 free regions each; the revision; the background edit's state, action, token and message. Bit 0 of flags: the caller may edit. |
| 648 | 72-byte request, 72, 0 | Starts one edit (root only): init, create (sectors 0: the largest free region), delete, grow (sectors 0: as far as possible). Partitions are named by disk and start sector. Returns the edit's token, or -13 (not root), -11 (older revision), -16 (another edit or a `disk` command running), -22 (invalid). |

**Extras** (kernel 0.0.54 and later, same capability version): the snapshot's reserved tail
(`struct zenith_disks_extra`, offset 14856) carries `version` 1, bytes in use and the filesystem's
state (clean, needs a check, mounted) for each partition, and whether both GPT copies are intact
and list the same partitions. Mounted volumes report from the filesystem; unmounted ext2 from its
superblock. Older kernels leave the tail zero; Equinox then shows "state unknown".

**Mounts** (kernel 0.0.56 and later, extras `version` 2, same capability version): flags bit 1
(`Z_DISKS_CAN_MOUNT`) says the kernel understands actions 5 to 7. Request `flags` bit 0 (zero for
actions 1 to 4) modifies them:

| Action | Request | Effect |
|---|---|---|
| 5 mount | partition, `label` = name, flag: also at startup | Mounts an ext2 or FAT32 partition at `/mnt/<name>` (1 to 15 of `A-Z a-z 0-9 . _ -`, not starting with a dot); creates the directory |
| 6 unmount | partition, flag: also stop mounting at startup | Unmounts it |
| 7 at startup | partition, flag: on or off | Adds or removes its `/etc/fstab` line; turning it on needs the partition mounted (that mount point is kept) |

The root, the boot partition and every system mount point (`/`, `/boot`, `/boot/efi`, `/tmp`,
`/proc`, `/dev`) are refused for all three; the kernel's own `umount` also refuses `/`. The extras
add `startup[8][16]` (1 when `/etc/fstab` mounts the partition at startup, offset 16024) and
`fstab_skipped` (how many lines Zenith does not understand, offset 16152).

## /etc/fstab

People edit this file by hand, so Zenith treats it as theirs and changes as little as it can:

- **One line per change.** Equinox (and the kernel at its request) adds, rewrites or removes only
  the line for the partition it was asked about. Comments, blank lines, order, spacing, CRLF line
  ends and every other line stay byte for byte. A rewritten line keeps its options and dump/pass
  fields. A line Equinox writes looks like
  `PARTUUID=<partition GUID> /mnt/data ext2 defaults 0 2`.
- **Never from an old copy.** The file is read fresh for every change and read again just before
  it is replaced (staged, then renamed). If an editor saved it in between, the change is made
  again on the new text, so neither edit is lost.
- **Understood:** `PARTUUID=` (the GPT partition GUID: stable when the disk moves port) or
  `/dev/<partition>`; any absolute mount point; `ext2`, `fat`/`vfat` or `auto`; the option
  `noauto`. Lines for system mount points (the installer writes `/`, `/boot/efi` and `/tmp`) are
  kept and left to the kernel, which mounts those itself.
- **Not understood** (for example `UUID=`, `LABEL=` or another filesystem): kept as written,
  skipped at startup with a log line, and counted in the snapshot. Equinox says how many lines
  that is. If the same mount point is listed twice, the first line wins.
- **At startup** (installed systems): each understood line is mounted after the root. A partition
  that is missing, a check that fails or a mount point outside `/mnt` that does not exist is
  logged and skipped. A second disk never stops the boot. Mount points under `/mnt` are created.

An edit that cannot work is refused before it starts: the token comes back with the job already
failed and its reason as the message. Otherwise it runs as root on its own thread; the snapshot
reports it running until it is done or failed. Clients poll 647 (Equinox: every 150 ms while
applying). Both calls take the service gate and count toward the renewable call budget.

## The app

- Layout follows the [concept](../design/equinox-concept.svg) (0.2.0, 2026-11-10). The sidebar
  has Disks (name and size), Mounts, and Changes (Plan with its step count, History). A disk page shows
  the "Now" bar and, when the plan touches the disk, the "After" bar computed with the kernel's
  placement rules: the EFI partition as a narrow shielded tile, partitions with their use, free
  space hatched, planned partitions dashed. Then the partition table as the plan leaves it
  (new rows marked, size changes as "before → after"), actions for the selection (Grow, Delete,
  New partition, New partition table), three status cards (ext2 state, GPT copies, 1 MiB
  alignment) and, for the system, mounted or unpartitioned disks, a notice saying why.
- New partition and Grow open an editor with its own "After" preview and what limits the change.
- Actions add steps (at most 8). "Your plan" docks on the right when the window is at least
  about 1100 px wide, otherwise it is the Plan page: numbered steps with what each does, the
  safety notes, Apply (root), the exact `disk` commands on request, Remove last and Clear. Apply
  runs the steps in order against the current revision and stops at the first that fails;
  earlier steps stay. History keeps this session's results.
- Mounts (0.3.0) lists every partition with a filesystem: where it is mounted, whether it is
  mounted at startup, and "System, stays mounted" for the root and boot partitions. Selecting an
  unmounted partition offers a name for `/mnt` (suggested from its label), "Also mount at every
  startup" and Mount. A mounted one offers Unmount (after a confirmation naming what happens at
  the next startup) and "Mount here at every startup". These act at once, outside the plan.
  Mounting changes no data. A disk with a mounted partition cannot be edited until it is
  unmounted.
- Unlock (0.4.0, kernel 0.0.57): run by an account that is not root, Equinox shows Unlock on the
  plan panel and in the mount editor. It asks the [Desktop Portal](PORTAL.md) for
  `org.zenith.disks.manage`; after an administrator's password in the desktop's dialog, this
  Equinox edits disks and mounts until it closes. A refusal says why; with no desktop to ask, it
  points to `sudo`. `--unlock` asks at startup.
- `native run equinox --probe` prints the snapshot; `--apply-test DISK` runs a fixed plan on a
  scratch disk; `--mount-op mount|unmount|startup PARTITION NAME 0|1` runs one mount action
  (gate use).

## Acceptance

`tools/equinox-smoke.py` (release plan): the signed package installs, the snapshot lists the
system and a blank scratch disk, the fixed plan runs through the ABI (new table, create, create,
delete, grow, check clean), the system disk and a non-root caller are refused, the window opens,
takes focus and closes; the host checks the scratch disk's GPT copies and ext2. Mounts, with
hand-written lines appended to the installer's `/etc/fstab`:
- the root cannot be unmounted;
- the new partition mounts at `/mnt/data` and at startup, and only its line is added;
- after a restart it is mounted again, and the line Zenith does not understand is skipped and
  logged;
- unmounting it with "forget" leaves exactly the earlier file. Installed VMware
acceptance with the operator remains (Q2).

## Next

Removable drives (USB sticks): [design](REMOVABLE-STORAGE.md), S6. Shrinking and moving partitions (relocating ext2 data) and running them at restart, formatting
existing partitions and FAT volumes from Equinox, labels, a sized grow with drag handles on the
bar, and a "last checked" time per volume, as in the concept.
