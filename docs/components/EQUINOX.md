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

An edit that cannot work is refused before it starts: the token comes back with the job already
failed and its reason as the message. Otherwise it runs as root on its own thread; the snapshot
reports it running until it is done or failed. Clients poll 647 (Equinox: every 150 ms while
applying). Both calls take the service gate and count toward the renewable call budget.

## The app

- The sidebar lists disks, then Plan and History. A disk page shows the "Now" bar and, when the
  plan touches the disk, the "After" bar computed with the kernel's placement rules; the partition
  table; and actions for the selection: Grow, Delete, New partition (size and label),
  New partition table. Mounted and system disks are shown with the reason they are not changed.
- Actions add steps (at most 8). Plan lists them with the exact `disk` command of each. Apply
  (root) runs them in order against the current revision and stops at the first that fails;
  earlier steps stay. History keeps this session's results.
- `native run equinox --probe` prints the snapshot; `--apply-test DISK` runs a fixed plan on a
  scratch disk (gate use).

## Acceptance

`tools/equinox-smoke.py` (release plan): the signed package installs, the snapshot lists the
system and a blank scratch disk, the fixed plan runs through the ABI (new table, create, create,
delete, grow, check clean), the system disk and a non-root caller are refused, the window opens,
takes focus and closes; the host checks the scratch disk's GPT copies and ext2. Installed VMware
acceptance with the operator remains (Q2).

## Next

Shrinking and moving partitions (relocating ext2 data), formatting existing partitions and FAT
volumes from Equinox, labels, and a sized grow; drag handles on the bar as in the concept.
