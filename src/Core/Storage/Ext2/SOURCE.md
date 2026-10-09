# Ext2 driver source

Adapted from CosmosOS/Cosmos, revision
`81259dd941a39b60226dc6f9db14befd6558e592`,
`src/Cosmos.Kernel.System/Filesystems/Ext2` (BSD 3-Clause; see LICENSE).
The upstream formatter and filesystem-type resolver are not included. Zenith
uses a local formatter for the gated profile and a local partition resolver.

The shipped Cosmos 3.0.89 driver reused a host-created file block during writes
on a 1 KiB ext2 volume. Host e2fsck detected multiply-claimed blocks. Zenith
keeps a local source adaptation so the allocator fix is reviewable and
host-testable without replacing global Cosmos packages or changing the sibling
checkout. Namespace changed to Zenith.Core.Storage.Ext2; block allocation,
release and group counts account for FirstDataBlock. A small local filesystem
type resolves Cosmos partitions; generic VFS formatting/destruction remain
disabled. The guarded `mkfs.ext2` command invokes the local formatter directly.

The lifecycle adaptation additionally prunes data and empty indirect tables on
shrink/deletion, zeroes partial-block tails on resize, treats fast symlink targets
as inline bytes, cleans unfinished allocations on short/failed writes, and
updates write/resize timestamps. Freed inodes persist zero links and deletion
time; empty directory removal reclaims every data/pointer block. Writes and size changes are bounded by the
direct + single + double indirect mapper; triple-indirect file I/O remains
unsupported. Host regression tests and the externally formatted QEMU fixture
cover these changes. Allocation/free now updates volume free counters before
persisting the group descriptor and superblock; remount regressions cover both
block and inode counters, including a full first group and second-group reuse.

Keep changes narrow and reconcile with upstream before replacing this copy.

The local formatter uses 1 KiB blocks, 128-byte inodes, 8192 blocks and 1024
inodes per group, with filetype as its only feature. It accounts for block one as
the group origin, marks bitmap padding unavailable, writes superblock/GDT backups
in every group, initializes root/lost+found, and computes counters before writing.
Geometry/labels are validated before I/O; the old primary is invalidated first
and the complete clean primary is published last with intervening flushes.
This is not a journal or secure wipe. UUID is unassigned until entropy support.
Layout references: [superblock](https://docs.kernel.org/filesystems/ext4/super.html),
[group descriptors](https://docs.kernel.org/filesystems/ext4/group_descr.html),
[bitmaps](https://docs.kernel.org/filesystems/ext4/bitmaps.html).
Host formatter tests and guest-created volumes are independently checked by e2fsck.

Allocator and free paths reuse bitmap, zero-block, descriptor and superblock
scratch buffers under the driver's existing serialized I/O contract. Metadata is
still reread on each operation; this does not cache disk state. A host allocation
budget regression and the guest 9 MiB group-boundary workload cover the change.

Symlink creation follows Linux ext2's one-block limit (including the terminating
NUL) and 59-byte inline limit; mode is 0777. Reads distinguish inline storage by
`i_blocks`, bound sizes before allocation, and reject missing/NUL-filled targets.
Names are validated as non-dot single components with at most 255 UTF-8 bytes
before allocation. This fixes the upstream routine's treatment of indirect slots
as direct target blocks. Reference: [Linux ext2 symlink creation](https://raw.githubusercontent.com/torvalds/linux/master/fs/ext2/namei.c).

Linux ownership now uses both low/high UID/GID halves (inode offsets 2/120 and
24/122); the writable profile rejects other creator layouts. Mode and ownership
updates preserve file type and unselected attributes, validate flags/timestamps
before changes, and update ctime. The high size field is used only for regular
files, leaving the directory ACL slot untouched. Layout reference:
[Linux inode fields](https://docs.kernel.org/filesystems/ext4/inodes.html).
