# Ext2 driver source

Adapted from CosmosOS/Cosmos, revision
`81259dd941a39b60226dc6f9db14befd6558e592`,
`src/Cosmos.Kernel.System/Filesystems/Ext2` (BSD 3-Clause; see LICENSE).
The upstream formatter and filesystem-type resolver are not included.

The shipped Cosmos 3.0.89 driver reused a host-created file block during writes
on a 1 KiB ext2 volume. Host e2fsck detected multiply-claimed blocks. Zenith
keeps a local source adaptation so the allocator fix is reviewable and
host-testable without replacing global Cosmos packages or changing the sibling
checkout. Namespace changed to Zenith.Core.Storage.Ext2; block allocation,
release and group counts account for FirstDataBlock. A small local filesystem
type resolves Cosmos partitions; formatting/destruction are not exposed.

The lifecycle adaptation additionally prunes data and empty indirect tables on
shrink/deletion, zeroes partial-block tails on resize, treats fast symlink targets
as inline bytes, cleans unfinished allocations on short/failed writes, and
updates write/resize timestamps. Freed inodes persist zero links and deletion
time; empty directory removal reclaims every data/pointer block. Writes and size changes are bounded by the
direct + single + double indirect mapper; triple-indirect file I/O remains
unsupported. Host regression tests and the externally formatted QEMU fixture
cover these changes.

Keep changes narrow and reconcile with upstream before replacing this copy.
