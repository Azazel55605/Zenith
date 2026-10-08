using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using Cosmos.Kernel.HAL.Vfs;

namespace Zenith.Core.Storage.Proc;

/// <summary>A read-only, flat virtual filesystem. Content is sampled on the first
/// read (or end-relative seek) of each handle, so partial reads see a consistent snapshot.</summary>
internal sealed class ProcFilesystemType : IVfsFilesystemType
{
    private readonly Func<string>[] _contents;
    private static readonly string[] s_names = { "meminfo", "mounts", "uptime", "cmdline" };

    public ProcFilesystemType(Func<string> meminfo, Func<string> mounts, Func<string> uptime, Func<string> cmdline)
    {
        _contents = new[] { meminfo, mounts, uptime, cmdline };
    }

    public bool TryMount(ReadOnlySpan<char> source, MountFlags flags, [NotNullWhen(true)] out IVfsSuperblock? superblock)
    {
        superblock = source.IsEmpty ? new Superblock(_contents) : null;
        return superblock is not null;
    }

    public bool TryFormat(ReadOnlySpan<char> source, IVfsFormatOptions? options) => false;
    public bool TryDestroy(ReadOnlySpan<char> source) => false;

    private sealed class Node : IVfsInode
    {
        public Node(string name, ulong number, Operations operations, Func<string>? content = null)
        {
            Name = name;
            Number = number;
            InodeOperations = operations;
            Content = content;
        }

        public string Name { get; }
        public ulong Number { get; }
        public Func<string>? Content { get; }
        public IInodeOperations InodeOperations { get; }
        public IFileOperations? FileOperations => Content is null ? null : (Operations)InodeOperations;
    }

    private sealed class Superblock : IVfsSuperblock
    {
        public Superblock(Func<string>[] contents)
        {
            var operations = new Operations();
            Root = new Node("", 1, operations);
            operations.Root = Root;
            var children = new IVfsInode[s_names.Length];
            for (int i = 0; i < children.Length; i++)
            {
                children[i] = new Node(s_names[i], (ulong)i + 2, operations, contents[i]);
            }
            operations.Children = children;
            SuperOperations = operations;
        }

        public IVfsInode Root { get; }
        public ISuperblockOperations SuperOperations { get; }
        public long BlockSize => 4096;
        public ulong MaxNameLength => 255;
    }

    private sealed class Operations : IInodeOperations, IFileOperations, ISuperblockOperations
    {
        public IVfsInode Root = null!;
        public IReadOnlyList<IVfsInode> Children = Array.Empty<IVfsInode>();
        private readonly Dictionary<IVfsOpenFile, byte[]> _snapshots = new();
        private readonly object _gate = new();

        public bool Lookup(IVfsInode dir, ReadOnlySpan<char> name, [NotNullWhen(true)] out IVfsInode? child)
        {
            child = null;
            if (!ReferenceEquals(dir, Root))
            {
                return false;
            }
            foreach (IVfsInode entry in Children)
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
            entries = isRoot ? Children : Array.Empty<IVfsInode>();
            return isRoot;
        }

        public bool GetAttr(IVfsInode inode, out VfsStat stat)
        {
            stat = default;
            if (inode is not Node node || !ReferenceEquals(node.InodeOperations, this))
            {
                return false;
            }
            bool directory = node.Content is null;
            stat.Ino = node.Number;
            stat.Mode = directory ? VfsMode.Directory | (VfsMode)0x16D : VfsMode.RegularFile | (VfsMode)0x124; // 0555 / 0444
            stat.NLink = directory ? 2u : 1u;
            // Virtual files report zero size; callers must read until EOF.
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
        public bool SetAttr(IVfsInode inode, SetAttrFlags flags, in VfsStat attributes) => false;

        private byte[] Snapshot(IVfsOpenFile file)
        {
            if (!_snapshots.TryGetValue(file, out byte[]? data))
            {
                data = Encoding.UTF8.GetBytes(((Node)file.Inode).Content!());
                _snapshots.Add(file, data);
            }
            return data;
        }

        public long Read(IVfsOpenFile openFile, Span<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }
            lock (_gate)
            {
                byte[] data = Snapshot(openFile);
                if (openFile.Position < 0)
                {
                    throw new IOException("Invalid /proc file position");
                }
                if (openFile.Position >= data.Length)
                {
                    return 0;
                }
                int count = Math.Min(buffer.Length, data.Length - (int)openFile.Position);
                data.AsSpan((int)openFile.Position, count).CopyTo(buffer);
                return count; // The VFS advances the position.
            }
        }

        public long Write(IVfsOpenFile openFile, ReadOnlySpan<byte> buffer) => throw new IOException("/proc is read-only");

        public bool Seek(IVfsOpenFile openFile, long offset, SeekWhence whence, out long newPosition)
        {
            newPosition = 0;
            lock (_gate)
            {
                long origin;
                switch (whence)
                {
                    case SeekWhence.Set: origin = 0; break;
                    case SeekWhence.Cur: origin = openFile.Position; break;
                    case SeekWhence.End: origin = Snapshot(openFile).LongLength; break;
                    default: return false;
                }
                if (offset < -origin || offset > long.MaxValue - origin)
                {
                    return false;
                }
                newPosition = origin + offset;
                openFile.Position = newPosition;
                return true;
            }
        }

        public bool Fsync(IVfsOpenFile openFile) => true;
        public void Release(IVfsOpenFile openFile)
        {
            lock (_gate)
            {
                _snapshots.Remove(openFile);
            }
        }
        public bool Sync(IVfsSuperblock superblock) => true;
        public void Drop(IVfsSuperblock superblock)
        {
            lock (_gate)
            {
                _snapshots.Clear();
            }
        }
        public bool StatFs(IVfsSuperblock superblock, out VfsStatFs statFs)
        {
            statFs = new VfsStatFs { Type = 0x9FA0, BlockSize = 4096, Frsize = 4096, Files = (ulong)Children.Count + 1, NameMax = 255 };
            return true;
        }
    }
}
