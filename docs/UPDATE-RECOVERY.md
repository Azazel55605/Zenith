# Upgrade generations, snapshots and boot recovery

**Future design**, reviewed 2026-10-15. Current kernel-only recovery is in
[KERNEL-UPDATES](KERNEL-UPDATES.md); implementation steps are in
[Boot/recovery](components/BOOT-RECOVERY.md). Requested direction: investigate Btrfs-like snapshots
for package upgrades, boot-menu selection of saved states, trial boot and restore.
No snapshot backend, root-generation selector or restore workflow exists in Zenith.
The desired recovery experience is distinct from committing to Btrfs itself.

## What a recoverable system state must contain

A rollback point must identify its kernel/boot artifacts, system files, package database
and relevant configuration together. Keeping only an old kernel does not revert a
changed desktop, package inventory or configuration. The running kernel stays loaded
while its replacement is installed on disk; restart/reboot activates new code.

Define the boundary between system state and ongoing personal data before snapshotting.
Prefer preserving `/home` across ordinary system rollback. Mutable service data and
account/configuration state need explicit policies: old software may not understand a
new database schema, and rolling back accounts/passwords has security consequences.
A filesystem snapshot does not by itself make concurrently updated application data
consistent. Quiesce writers or use service-specific preparation as needed.

Btrfs snapshots are subvolumes initially sharing extents through copy-on-write; they
are not independent backups. Nested subvolumes require deliberate snapshot scope.
See [Btrfs subvolume documentation](https://btrfs.readthedocs.io/en/latest/Subvolumes.html).
External backup remains a separate operation.

## Options for Zenith

| Backend | Can provide | Main cost/limit |
|---|---|---|
| Versioned kernels on ESP | Previous-kernel boot selection | No root/app/config rollback; easiest first update stage |
| File/package generations | Retain previous owned system files and package inventory | Need consistent activation/root selection; shared mutable files complicate rollback |
| Full offline ext2 copies/A-B roots | Complete clean root versions with separate kernels | Copy time, disk space, partition/layout changes; not instant CoW snapshots |
| Block-layer CoW snapshots below ext2 | Snapshot semantics without porting Btrfs | New persistent mapping/metadata/crash recovery and reference lifecycle; snapshots must capture consistent/quiesced ext2 |
| Btrfs implementation | Native extent-sharing snapshots/subvolumes and other features | Major filesystem project: trees/allocation/checksums/transactions, recovery and tools; no current driver |

Ext2 currently offers no snapshots. Hard-linked trees are not a substitute: edits can
change both versions, hard links are not implemented, and copying live raw blocks can
capture an inconsistent filesystem. QEMU/host disk snapshots are useful test fixtures
but do not deliver native snapshot support on physical Zenith installations.

Recommended staged investigation: retain versioned kernels first; prototype complete
clean root generations/A-B recovery on disposable disks; evaluate persistent block CoW
versus Btrfs later if efficient snapshots are essential. Root-generation/A-B is a
candidate, not a newly mandated prerequisite for basic kernel updates. Budget beyond
the current installer root cap and ESP size before picking retention/layout.

## Bootloader versus OS responsibilities

Installed entries load a hash-addressed `/boot/zenith/<sha256>/kernel.elf`
from the FAT32 ESP through Limine. The local
EFI binary identifies itself as **Limine 10.8.5**. Current upstream Limine lists FAT
and ISO9660 readers, and its configuration supports menu entries and a kernel command
line. These are reference capabilities, not a claim that snapshot support is built in.
Sources: [Limine README](https://github.com/Limine-Bootloader/Limine/blob/trunk/README.md),
[configuration](https://github.com/Limine-Bootloader/Limine/blob/trunk/CONFIG.md).
Pin/check the installed release's syntax in native tests before using newer options.

A design can keep all kernels on the ESP, avoiding a requirement for Limine to read
Btrfs/ext2 itself. A generated entry chooses a matching kernel and passes an explicit
root/generation/snapshot selector to Zenith. The kernel/recovery environment mounts
that selected state. Installed entries already select an exact GPT PARTUUID; live discovery can scan
for Zenith os-release. Snapshot/generation selection must additionally bind the
selected root state and matching package/configuration data. Existing root identity
is not a whole-system snapshot identifier; do not use partition enumeration order.

Illustrative entry, **not an implemented command-line contract**:

```
/Zenith previous system
    protocol: limine
    path: boot():/boot/generations/release-old/Zenith.elf
    cmdline: root=<stable-root-id> generation=<saved-id> recovery=trial
```

Keep a visible menu timeout for manual fallback; current boot layout uses `timeout: 5`.
The package/recovery service generates entries after validating retained boot artifacts
and root identities. Automatic fallback needs separately verified attempt accounting
and success confirmation; merely selecting a default entry is not that mechanism.
Bootloader root selection can be small; restoring filesystem state should normally run
in Zenith/recovery tools, not as a new complex filesystem writer inside Limine.

## Trial boot and restore

User experience target:

1. Before upgrade, save/retain a consistent known-good system generation and its kernel.
2. Stage/verify new packages and record the matching pending generation.
3. On reboot, choose Current, Previous, Saved states or Recovery from the menu.
4. Trial boot a selected saved state without modifying the preserved copy.
5. Inspect it; choose Restore as default or return to the newer generation.
6. Restore creates/selects a writable state, restores the matching package/config state,
   publishes the boot choice and confirms the successful boot.

“Live-booting a snapshot” needs defined write behavior: read-only root plus ephemeral
writable state, or a disposable writable clone. Btrfs read-only snapshots alone do not
supply an overlay/clone service to Zenith. Current ext2 mounts are writable and dirty
marked; read-only mounting and temporary write layers need implementation. Trial mode
must not accidentally write shared home/service data or change the default generation.
Restoration should run outside the root being destructively replaced.

A future Btrfs design would retain a read-only pre-upgrade system snapshot, create a
writable clone for activation/restore, and track root/subvolume identity plus matching
ESP files. Even then the ESP is outside the root snapshot and needs its own retained
artifacts/config handling. A root snapshot cannot reconstruct a kernel already deleted
from the ESP. Snapshot deletion/retention must protect active/pending/last-good states.

## Validation gates

Tests must prove generation identity, matching package inventory, kernel/root pair,
config/data scope and boot-menu selection across installed disk-only boots. Inject
power loss/out-of-space/short writes during snapshot creation, package staging,
activation and restore. Verify failed activation leaves a bootable retained state.
Test rollback after service-schema changes and garbage collection under limited space.

Dirty supported ext2 roots receive a read-only audit before writable mounting;
inconsistent volumes require host repair. Any design using writable ext2 must
account for that dependency; check-only fsck cannot repair it.
A separate clean generation can avoid depending on the damaged active root, but shared
partitions/state and damaged ESP metadata can still prevent recovery. Recovery media
and external backups remain necessary for failures outside the retained-state scope.

## Decisions still open

What is included in system rollback; number/retention of saved states; full-copy/A-B,
block-CoW or Btrfs backend; read-only/trial write layer; stable root identifiers;
manual versus automatic fallback; and compatibility policy for mutable user/service
data. First prove the boot-generation contract before investing in a full Btrfs port.

## Kernel-only recovery foundation

The dedicated P3 kernel coordinator now provides signed staging, versioned boot
paths, manual previous-kernel selection, explicit confirmation/rollback and a durable
undo record. See [KERNEL-UPDATES](KERNEL-UPDATES.md) and PROGRESS for exact acceptance.
It shares the installed root and does not implement the snapshot/trial-root model
described above, automatic failed-boot rollback or arbitrary physical power-loss repair.
