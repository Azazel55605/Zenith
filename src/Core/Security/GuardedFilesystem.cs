using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using Cosmos.Kernel.HAL.Vfs;
using Zenith.Core.Storage.Ext2;

namespace Zenith.Core.Security;

/// <summary>Mount-bound policy adapter: System.IO, handles and direct inode calls share checks.</summary>
internal sealed class GuardedFilesystem : IVfsFilesystemType
{
    private readonly IVfsFilesystemType _driver;
    public GuardedFilesystem(IVfsFilesystemType driver) => _driver = driver;
    public bool TryMount(ReadOnlySpan<char> source, MountFlags flags, [NotNullWhen(true)] out IVfsSuperblock? superblock)
    {
        RequireRoot();
        superblock = null;
        if (!_driver.TryMount(source, flags, out var inner)) { return false; }
        superblock = new Volume(inner);
        return true;
    }
    public bool TryFormat(ReadOnlySpan<char> source, IVfsFormatOptions? options) { RequireRoot(); return _driver.TryFormat(source, options); }
    public bool TryDestroy(ReadOnlySpan<char> source) { RequireRoot(); return _driver.TryDestroy(source); }
    internal static void RequireRoot()
    {
        if (!CredentialContext.Current.IsRoot) { throw new UnauthorizedAccessException("operation requires root"); }
    }
    internal static IVfsInode Unwrap(IVfsInode inode) => inode is Node node ? node.Inner : inode;
    internal static bool IsUnix(IVfsInode inode)
    {
        inode = Unwrap(inode);
        return inode is Ext2Inode || Storage.Proc.ProcFilesystemType.Owns(inode) || Storage.Dev.DevFilesystemType.Owns(inode);
    }
    internal static void Require(IVfsInode inode, int access) => FileAccessPolicy.Require(inode, access);
    internal static void RequireRemoval(IVfsInode parent, ReadOnlySpan<char> name) => FileAccessPolicy.RequireRemoval(parent, name);

    private sealed class IdentityComparer : IEqualityComparer<IVfsInode>
    {
        public bool Equals(IVfsInode? x, IVfsInode? y) => ReferenceEquals(x, y);
        public int GetHashCode(IVfsInode obj) => RuntimeHelpers.GetHashCode(obj);
    }
    private sealed class Volume : IVfsSuperblock, ISuperblockOperations
    {
        private readonly IVfsSuperblock _inner;
        private readonly Dictionary<uint, Node> _ext2 = new();
        private readonly Dictionary<IVfsInode, Node> _other = new(new IdentityComparer());
        private readonly object _gate = new();
        public Volume(IVfsSuperblock inner) { _inner = inner; Root = Wrap(inner.Root); }
        public IVfsInode Root { get; }
        public ISuperblockOperations SuperOperations => this;
        public long BlockSize => _inner.BlockSize;
        public ulong MaxNameLength => _inner.MaxNameLength;
        public bool Sync(IVfsSuperblock sb) => _inner.SuperOperations.Sync(_inner);
        public bool StatFs(IVfsSuperblock sb, out VfsStatFs stat) => _inner.SuperOperations.StatFs(_inner, out stat);
        public void Drop(IVfsSuperblock sb) { RequireRoot(); _inner.SuperOperations.Drop(_inner); _ext2.Clear(); _other.Clear(); }
        public Node Wrap(IVfsInode inner)
        {
            lock (_gate)
            {
                if (inner is Ext2Inode ext)
                {
                    if (!_ext2.TryGetValue(ext.InodeNumber, out var node)) { node = new Node(this, inner); _ext2.Add(ext.InodeNumber, node); }
                    node._inner = inner; return node;
                }
                if (!_other.TryGetValue(inner, out var other)) { other = new Node(this, inner); _other.Add(inner, other); }
                return other;
            }
        }
        public void Forget(IVfsInode inode)
        {
            lock (_gate) { if (inode is Ext2Inode ext) { _ext2.Remove(ext.InodeNumber); } else { _other.Remove(inode); } }
        }
    }

    private sealed class Node : IVfsInode, IInodeOperations, IFileOperations
    {
        private readonly Volume _volume;
        internal IVfsInode _inner;
        public Node(Volume volume, IVfsInode inner) { _volume = volume; _inner = inner; }
        // Re-read ext2 metadata before authorization, including changes through another handle.
        public IVfsInode Inner => _inner is Ext2Inode ext ? ext.Superblock.ReadInode(ext.InodeNumber, ext.Name) : _inner;
        public string Name => _inner.Name;
        public IInodeOperations InodeOperations => this;
        public IFileOperations? FileOperations => _inner.FileOperations is null ? null : this;
        private Node Owned(IVfsInode inode)
            => inode is Node node && ReferenceEquals(node._volume, _volume) ? node : throw new UnauthorizedAccessException("foreign inode");
        public bool GetAttr(IVfsInode inode, out VfsStat stat)
        {
            var inner = Owned(inode).Inner;
            if (!inner.InodeOperations.GetAttr(inner, out stat)) { return false; }
            // Legacy FAT remains readable but cannot grant non-root mutation authority.
            if (!IsUnix(inner)) { stat.Uid = 0; stat.Gid = 0; stat.Mode = (stat.Mode & VfsMode.FileTypeMask) | (VfsMode)0x1ed; }
            return true;
        }
        public bool Lookup(IVfsInode dir, ReadOnlySpan<char> name, [NotNullWhen(true)] out IVfsInode? child)
        {
            var node = Owned(dir); Require(node, PermissionPolicy.Search);
            var inner = node.Inner; child = null;
            if (!inner.InodeOperations.Lookup(inner, name, out var found)) { return false; }
            child = _volume.Wrap(found); return true;
        }
        public bool ReadDir(IVfsInode dir, out IReadOnlyList<IVfsInode> entries)
        {
            var node = Owned(dir); Require(node, PermissionPolicy.Read | PermissionPolicy.Search);
            var inner = node.Inner; entries = Array.Empty<IVfsInode>();
            if (!inner.InodeOperations.ReadDir(inner, out var found)) { return false; }
            var wrapped = new IVfsInode[found.Count];
            for (int i = 0; i < found.Count; i++) { wrapped[i] = _volume.Wrap(found[i]); }
            entries = wrapped; return true;
        }
        private bool CreateNode(IVfsInode dir, ReadOnlySpan<char> name, VfsMode mode, bool directory, string? target, [NotNullWhen(true)] out IVfsInode? created)
        {
            var node = Owned(dir); Require(node, PermissionPolicy.Write | PermissionPolicy.Search);
            var parent = node.Inner; created = null;
            parent.InodeOperations.GetAttr(parent, out var stat);
            var caller = CredentialContext.Current;
            ushort bits = PermissionPolicy.CreationMode((ushort)mode, 0x12, (ushort)stat.Mode, directory);
            IVfsInode? result;
            bool ok = target is not null ? parent.InodeOperations.Symlink(parent, name, target, out result)
                : directory ? parent.InodeOperations.Mkdir(parent, name, (VfsMode)bits, out result)
                : parent.InodeOperations.Create(parent, name, (VfsMode)bits, out result);
            if (!ok || result is null) { return false; }
            if (result is Ext2Inode)
            {
                var attributes = new VfsStat { Uid = caller.Uid, Gid = PermissionPolicy.CreationGroup(caller, stat.Gid, (ushort)stat.Mode), Mode = (VfsMode)bits };
                var flags = SetAttrFlags.Uid | SetAttrFlags.Gid | (target is null ? SetAttrFlags.Mode : SetAttrFlags.None);
                if (!result.InodeOperations.SetAttr(result, flags, attributes))
                {
                    if (directory) { parent.InodeOperations.Rmdir(parent, name); } else { parent.InodeOperations.Unlink(parent, name); }
                    throw new IOException("failed to assign new object ownership");
                }
            }
            created = _volume.Wrap(result); return true;
        }
        public bool Create(IVfsInode dir, ReadOnlySpan<char> name, VfsMode mode, [NotNullWhen(true)] out IVfsInode? inode) => CreateNode(dir, name, mode, false, null, out inode);
        public bool Mkdir(IVfsInode dir, ReadOnlySpan<char> name, VfsMode mode, [NotNullWhen(true)] out IVfsInode? inode) => CreateNode(dir, name, mode, true, null, out inode);
        public bool Symlink(IVfsInode dir, ReadOnlySpan<char> name, ReadOnlySpan<char> target, [NotNullWhen(true)] out IVfsInode? inode) => CreateNode(dir, name, (VfsMode)0x1ff, false, target.ToString(), out inode);
        public bool Unlink(IVfsInode dir, ReadOnlySpan<char> name) => Remove(dir, name, false);
        public bool Rmdir(IVfsInode dir, ReadOnlySpan<char> name) => Remove(dir, name, true);
        private bool Remove(IVfsInode dir, ReadOnlySpan<char> name, bool directory)
        {
            var node = Owned(dir); RequireRemoval(node, name);
            var parent = node.Inner; parent.InodeOperations.Lookup(parent, name, out var child);
            bool ok = directory ? parent.InodeOperations.Rmdir(parent, name) : parent.InodeOperations.Unlink(parent, name);
            if (ok && child is not null) { _volume.Forget(child); }
            return ok;
        }
        public bool Rename(IVfsInode oldParent, ReadOnlySpan<char> oldName, IVfsInode newParent, ReadOnlySpan<char> newName)
        {
            var oldNode = Owned(oldParent); var newNode = Owned(newParent);
            RequireRemoval(oldNode, oldName); Require(newNode, PermissionPolicy.Write | PermissionPolicy.Search);
            var from = oldNode.Inner; var to = newNode.Inner;
            if (to.InodeOperations.Lookup(to, newName, out _)) { RequireRemoval(newNode, newName); }
            return from.InodeOperations.Rename(from, oldName, to, newName);
        }
        public bool SetAttr(IVfsInode inode, SetAttrFlags flags, in VfsStat attributes)
        {
            var node = Owned(inode); var inner = node.Inner;
            if (!inner.InodeOperations.GetAttr(inner, out var stat)) { return false; }
            var caller = CredentialContext.Current;
            if (((flags & SetAttrFlags.Mode) != 0 && !PermissionPolicy.CanChmod(caller, stat.Uid))
                || ((flags & (SetAttrFlags.Uid | SetAttrFlags.Gid)) != 0 && !PermissionPolicy.CanChown(caller)))
            { throw new UnauthorizedAccessException("permission denied changing ownership/mode"); }
            if ((flags & SetAttrFlags.Size) != 0) { Require(node, PermissionPolicy.Write); }
            if ((flags & (SetAttrFlags.Atime | SetAttrFlags.Mtime | SetAttrFlags.Ctime)) != 0 && !caller.IsRoot && caller.Uid != stat.Uid)
            { throw new UnauthorizedAccessException("permission denied changing timestamps"); }
            if (!caller.IsRoot && (flags & SetAttrFlags.Mode) != 0 && ((ushort)attributes.Mode & 0xc00) != 0)
            { throw new UnauthorizedAccessException("setuid/setgid mode requires root"); }
            return inner.InodeOperations.SetAttr(inner, flags, attributes);
        }
        public bool TryReadLink(IVfsInode inode, out string? target)
        {
            var inner = Owned(inode).Inner; return inner.InodeOperations.TryReadLink(inner, out target);
        }
        private sealed class Open : IVfsOpenFile
        {
            private readonly IVfsOpenFile _file;
            public Open(IVfsOpenFile file, IVfsInode inode) { _file = file; Inode = inode; }
            public IVfsInode Inode { get; }
            public IFileOperations Operations => Inode.FileOperations!;
            public long Position { get => _file.Position; set => _file.Position = value; }
        }
        private Open File(IVfsOpenFile file) => new(file, Owned(file.Inode).Inner);
        public long Read(IVfsOpenFile file, Span<byte> buffer) { Require(Owned(file.Inode), PermissionPolicy.Read); var f = File(file); return f.Operations.Read(f, buffer); }
        public long Write(IVfsOpenFile file, ReadOnlySpan<byte> buffer)
        {
            var node = Owned(file.Inode); Require(node, PermissionPolicy.Write);
            var f = File(file); return f.Operations.Write(f, buffer);
        }
        public bool Seek(IVfsOpenFile file, long offset, SeekWhence whence, out long position) { var f = File(file); return f.Operations.Seek(f, offset, whence, out position); }
        public bool Fsync(IVfsOpenFile file) { var f = File(file); return f.Operations.Fsync(f); }
        public void Release(IVfsOpenFile file) { var f = File(file); f.Operations.Release(f); }
    }
}
