using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Zenith.Core.Security;

namespace Zenith.Core.Storage.Dev;

/// <summary>Null/zero character devices and a live read-only block-device namespace.</summary>
internal sealed class DevFilesystemType : IVfsFilesystemType
{
    internal static bool Owns(IVfsInode inode) => inode is Node or BlockDeviceNode;

    private readonly Func<IReadOnlyList<IBlockDevice>> _devices;
    private readonly IEntropySource? _entropy;

    public DevFilesystemType(Func<IReadOnlyList<IBlockDevice>>? devices = null, IEntropySource? entropy = null)
    {
        _devices = devices ?? (() => Array.Empty<IBlockDevice>());
        _entropy = entropy;
    }

    public bool TryMount(ReadOnlySpan<char> source, MountFlags flags, [NotNullWhen(true)] out IVfsSuperblock? superblock)
    {
        superblock = source.IsEmpty ? new Superblock((flags & MountFlags.ReadOnly) != 0, _devices, _entropy) : null;
        return superblock is not null;
    }

    public bool TryFormat(ReadOnlySpan<char> source, IVfsFormatOptions? options) => false;
    public bool TryDestroy(ReadOnlySpan<char> source) => false;

    private sealed class Node : IVfsInode
    {
        public Node(string name, ulong number, Operations operations, bool zero = false)
        {
            Name = name;
            Number = number;
            InodeOperations = operations;
            Zero = zero;
        }

        public string Name { get; }
        public ulong Number { get; }
        public bool Zero { get; }
        public IInodeOperations InodeOperations { get; }
        public IFileOperations? FileOperations => Name.Length == 0 ? null : (Operations)InodeOperations;
    }

    private sealed class Superblock : IVfsSuperblock
    {
        public Superblock(bool readOnly, Func<IReadOnlyList<IBlockDevice>> devices, IEntropySource? entropy)
        {
            var operations = new Operations(readOnly, devices, entropy);
            Root = new Node("", 1, operations);
            operations.Root = Root;
            operations.Children = new IVfsInode[]
            {
                new Node("null", 2, operations),
                new Node("zero", 3, operations, zero: true),
            };
            if (entropy is not null)
            { operations.Children = new IVfsInode[] { operations.Children[0], operations.Children[1], new Node("random", 4, operations) }; }
            SuperOperations = operations;
        }

        public IVfsInode Root { get; }
        public ISuperblockOperations SuperOperations { get; }
        public long BlockSize => 4096;
        public ulong MaxNameLength => 255;
    }

    // Cosmos 3.0.89's interface dispatcher does not resolve the contravariant
    // IEqualityComparer<object> implementation on ReferenceEqualityComparer.
    private sealed class DeviceIdentityComparer : IEqualityComparer<IBlockDevice>
    {
        public bool Equals(IBlockDevice? left, IBlockDevice? right) => ReferenceEquals(left, right);
        public int GetHashCode(IBlockDevice device) => RuntimeHelpers.GetHashCode(device);
    }

    private sealed class Operations : IInodeOperations, IFileOperations, ISuperblockOperations
    {
        public IVfsInode Root = null!;
        public IReadOnlyList<IVfsInode> Children = Array.Empty<IVfsInode>();
        private readonly bool _readOnly;
        private readonly IEntropySource? _entropy;

        private static readonly DeviceIdentityComparer s_deviceComparer = new();
        private readonly Func<IReadOnlyList<IBlockDevice>> _devices;
        private readonly object _gate = new();
        private Dictionary<IBlockDevice, BlockDeviceNode> _blocks = new(s_deviceComparer);
        private ulong _nextNumber = 5;

        public Operations(bool readOnly, Func<IReadOnlyList<IBlockDevice>> devices, IEntropySource? entropy)
        {
            _readOnly = readOnly;
            _devices = devices;
            _entropy = entropy;
        }

        private bool IsPresent(IBlockDevice device)
        {
            foreach (IBlockDevice candidate in _devices())
            {
                if (ReferenceEquals(candidate, device))
                {
                    return true;
                }
            }
            return false;
        }

        private IReadOnlyList<IVfsInode> Entries()
        {
            lock (_gate)
            {
                var entries = new List<IVfsInode>(Children);
                var names = new HashSet<string>(StringComparer.Ordinal) { "null", "zero", "random" };
                var current = new Dictionary<IBlockDevice, BlockDeviceNode>(s_deviceComparer);
                foreach (IBlockDevice device in _devices())
                {
                    string name = device.Name;
                    if (name.Length == 0 || name == "." || name == ".." || name.Contains('/') || name.Contains('\\')
                        || !BlockDeviceNode.IsSupported(device) || !names.Add(name))
                    {
                        continue;
                    }
                    if (!_blocks.TryGetValue(device, out BlockDeviceNode? node))
                    {
                        node = new BlockDeviceNode(device, _nextNumber++, this, IsPresent);
                    }
                    current.Add(device, node);
                    entries.Add(node);
                }
                _blocks = current; // Removed devices survive only while an old handle references them.
                return entries;
            }
        }

        public bool Lookup(IVfsInode dir, ReadOnlySpan<char> name, [NotNullWhen(true)] out IVfsInode? child)
        {
            child = null;
            if (!ReferenceEquals(dir, Root))
            {
                return false;
            }
            foreach (IVfsInode entry in Entries())
            {
                if (name.SequenceEqual(entry.Name.AsSpan()))
                {
                    child = entry;
                    return true;
                }
            }
            return false;
        }

        public bool ReadDir(IVfsInode dir, out IReadOnlyList<IVfsInode> entries)
        {
            bool isRoot = ReferenceEquals(dir, Root);
            entries = isRoot ? Entries() : Array.Empty<IVfsInode>();
            return isRoot;
        }

        public bool GetAttr(IVfsInode inode, out VfsStat stat)
        {
            stat = default;
            if (inode is BlockDeviceNode block && ReferenceEquals(block.InodeOperations, this))
            {
                return block.TryStat(out stat);
            }
            if (inode is not Node node || !ReferenceEquals(node.InodeOperations, this))
            {
                return false;
            }
            bool directory = ReferenceEquals(node, Root);
            stat.Ino = node.Number;
            stat.Mode = directory ? VfsMode.Directory | (VfsMode)0x16D
                : VfsMode.CharacterDevice | (_readOnly || node.Name == "random" ? (VfsMode)0x124 : (VfsMode)0x1B6); // 0555 / 0444 / 0666
            stat.NLink = directory ? 2u : 1u;
            // Conventional major 1, minor 3 (null) and 5 (zero); neither stores data.
            stat.Rdev = directory ? 0ul : node.Name == "random" ? 0x108ul : node.Zero ? 0x105ul : 0x103ul;
            stat.BlkSize = 4096;
            return true;
        }

        public bool Create(IVfsInode dir, ReadOnlySpan<char> name, VfsMode mode, [NotNullWhen(true)] out IVfsInode? inode)
        {
            inode = null;
            return false;
        }
        public bool Mkdir(IVfsInode dir, ReadOnlySpan<char> name, VfsMode mode, [NotNullWhen(true)] out IVfsInode? inode)
        {
            inode = null;
            return false;
        }
        public bool Symlink(IVfsInode dir, ReadOnlySpan<char> name, ReadOnlySpan<char> target, [NotNullWhen(true)] out IVfsInode? inode)
        {
            inode = null;
            return false;
        }
        public bool Unlink(IVfsInode dir, ReadOnlySpan<char> name) => false;
        public bool Rmdir(IVfsInode dir, ReadOnlySpan<char> name) => false;
        public bool Rename(IVfsInode oldParent, ReadOnlySpan<char> oldName, IVfsInode newParent, ReadOnlySpan<char> newName) => false;
        // The pinned Cosmos PAL requests truncation when opening for shell output.
        // Accept only zero-size truncation as a no-op; device size never changes.
        public bool SetAttr(IVfsInode inode, SetAttrFlags flags, in VfsStat attributes)
        {
            return !_readOnly && inode is Node node && !ReferenceEquals(node, Root)
                && ReferenceEquals(node.InodeOperations, this)
                && flags == SetAttrFlags.Size && attributes.Size == 0;
        }

        public long Read(IVfsOpenFile openFile, Span<byte> buffer)
        {
            if (openFile.Inode.Name == "random")
            {
                int count = Math.Min(buffer.Length, 4096);
                if (_entropy is null || !_entropy.TryFill(buffer.Slice(0, count))) { buffer.Clear(); throw new IOException("secure entropy unavailable"); }
                return count;
            }
            if (!((Node)openFile.Inode).Zero)
            {
                return 0;
            }
            buffer.Clear();
            return buffer.Length; // The VFS advances the position.
        }

        public long Write(IVfsOpenFile openFile, ReadOnlySpan<byte> buffer)
        {
            if (openFile.Inode.Name == "random") { throw new IOException("random device is read-only"); }
            if (_readOnly)
            {
                throw new IOException("/dev is mounted read-only");
            }
            return buffer.Length;
        }

        public bool Seek(IVfsOpenFile openFile, long offset, SeekWhence whence, out long newPosition)
        {
            // Like Unix null/zero, valid seeks are a no-op and always return zero.
            newPosition = 0;
            if (whence != SeekWhence.Set && whence != SeekWhence.Cur && whence != SeekWhence.End)
            {
                return false;
            }
            openFile.Position = 0;
            return true;
        }

        public bool Fsync(IVfsOpenFile openFile) => true;
        public void Release(IVfsOpenFile openFile) { }
        public bool Sync(IVfsSuperblock superblock) => true;
        public void Drop(IVfsSuperblock superblock) { }
        public bool StatFs(IVfsSuperblock superblock, out VfsStatFs statFs)
        {
            statFs = new VfsStatFs { Type = 0x01021994, BlockSize = 4096, Frsize = 4096, Files = (ulong)Entries().Count + 1, NameMax = 255 };
            return true;
        }
    }
}
