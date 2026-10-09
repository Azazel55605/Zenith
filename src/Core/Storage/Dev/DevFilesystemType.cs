using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.HAL.Interfaces.Devices;

namespace Zenith.Core.Storage.Dev;

/// <summary>Null/zero character devices and a live read-only block-device namespace.</summary>
internal sealed class DevFilesystemType : IVfsFilesystemType
{
    internal static bool Owns(IVfsInode inode) => inode is Node or BlockDeviceNode;

    private readonly Func<IReadOnlyList<IBlockDevice>> _devices;

    public DevFilesystemType(Func<IReadOnlyList<IBlockDevice>>? devices = null)
    {
        _devices = devices ?? (() => Array.Empty<IBlockDevice>());
    }

    public bool TryMount(ReadOnlySpan<char> source, MountFlags flags, [NotNullWhen(true)] out IVfsSuperblock? superblock)
    {
        superblock = source.IsEmpty ? new Superblock((flags & MountFlags.ReadOnly) != 0, _devices) : null;
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
        public Superblock(bool readOnly, Func<IReadOnlyList<IBlockDevice>> devices)
        {
            var operations = new Operations(readOnly, devices);
            Root = new Node("", 1, operations);
            operations.Root = Root;
            operations.Children = new IVfsInode[]
            {
                new Node("null", 2, operations),
                new Node("zero", 3, operations, zero: true),
            };
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

        private static readonly DeviceIdentityComparer s_deviceComparer = new();
        private readonly Func<IReadOnlyList<IBlockDevice>> _devices;
        private readonly object _gate = new();
        private Dictionary<IBlockDevice, BlockDeviceNode> _blocks = new(s_deviceComparer);
        private ulong _nextNumber = 4;

        public Operations(bool readOnly, Func<IReadOnlyList<IBlockDevice>> devices)
        {
            _readOnly = readOnly;
            _devices = devices;
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
                var names = new HashSet<string>(StringComparer.Ordinal) { "null", "zero" };
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
                : VfsMode.CharacterDevice | (_readOnly ? (VfsMode)0x124 : (VfsMode)0x1B6); // 0555 / 0444 / 0666
            stat.NLink = directory ? 2u : 1u;
            // Conventional major 1, minor 3 (null) and 5 (zero); neither stores data.
            stat.Rdev = directory ? 0ul : node.Zero ? 0x105ul : 0x103ul;
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
            if (!((Node)openFile.Inode).Zero)
            {
                return 0;
            }
            buffer.Clear();
            return buffer.Length; // The VFS advances the position.
        }

        public long Write(IVfsOpenFile openFile, ReadOnlySpan<byte> buffer)
        {
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
