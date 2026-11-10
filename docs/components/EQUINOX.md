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

An edit that cannot work is refused before it starts: the token comes back with the job already
failed and its reason as the message. Otherwise it runs as root on its own thread; the snapshot
reports it running until it is done or failed. Clients poll 647 (Equinox: every 150 ms while
applying). Both calls take the service gate and count toward the renewable call budget.

## The app

- Layout follows the [concept](../design/equinox-concept.svg) (0.2.0, 2026-11-10). The sidebar
  has Disks (name and size) and Changes (Plan with its step count, History). A disk page shows
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
- `native run equinox --probe` prints the snapshot; `--apply-test DISK` runs a fixed plan on a
  scratch disk (gate use).

## Acceptance

`tools/equinox-smoke.py` (release plan): the signed package installs, the snapshot lists the
system and a blank scratch disk, the fixed plan runs through the ABI (new table, create, create,
delete, grow, check clean), the system disk and a non-root caller are refused, the window opens,
takes focus and closes; the host checks the scratch disk's GPT copies and ext2. Installed VMware
acceptance with the operator remains (Q2).

## Next

Shrinking and moving partitions (relocating ext2 data) and running them at restart, formatting
existing partitions and FAT volumes from Equinox, labels, a sized grow with drag handles on the
bar, and a "last checked" time per volume, as in the concept.
