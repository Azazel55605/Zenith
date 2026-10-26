using System;
using System.Collections.Generic;
using System.IO;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Vfs;
using Zenith.Core.Security;

namespace Zenith.Core.Processes;

internal sealed class NativeProcessFiles : IProcessFiles, IProcessNamespace, IProcessCopyFiles, IProcessLinkMetadata
{
    public int Mutate(ProcessPathOperation operation, string path, string? target, ulong value, ulong extra)
    {
        if (path.Length > 1) { path = path.TrimEnd('/'); }
        if (operation is ProcessPathOperation.CheckCopyDirectory or ProcessPathOperation.CheckRemovalDirectory)
        {
            if (!VfsManager.TryOpenDirectory(path, out var directory)) { return -2; }
            try
            {
                var node = directory.Inode;
                if (operation == ProcessPathOperation.CheckRemovalDirectory)
                {
                    foreach (var mount in VfsManager.Mounts)
                    { if (ReferenceEquals(node, mount.Superblock.Root)) { directory.Dispose(); return -22; } }
                }
                else
                {
                    if (VfsManager.TryOpenDirectory("/", out var root))
                    {
                        bool sameRoot = ReferenceEquals(node, root.Inode); root.Dispose();
                        if (sameRoot) { directory.Dispose(); return -22; }
                    }
                    string prefix = "";
                    foreach (string part in target!.Split('/'))
                    {
                        if (part.Length == 0) { continue; }
                        prefix += "/" + part;
                        if (!VfsManager.TryOpenDirectory(prefix, out var ancestor)) { break; }
                        bool same = ReferenceEquals(node, ancestor.Inode); ancestor.Dispose();
                        if (same) { directory.Dispose(); return -22; }
                    }
                }
                directory.Dispose(); return 0;
            }
            catch { directory.Dispose(); throw; }
        }
        if (operation == ProcessPathOperation.Touch)
        {
            if (!VfsManager.TryStat(path, out var before)) { return -2; }
            if (!CredentialContext.Current.IsRoot && CredentialContext.Current.Uid != before.Uid) { return -13; }
            if (before.IsDirectory)
            {
                if (!VfsManager.TryOpenDirectory(path, out var handle)) { return -2; }
                try { bool updated = TouchNode(handle.Inode); handle.Dispose(); return updated ? 0 : -5; }
                catch { handle.Dispose(); throw; }
            }
            if (!VfsManager.TryOpenFile(path, out var file)) { return -2; }
            try { bool updated = TouchNode(file.Inode); file.Dispose(); return updated ? 0 : -5; }
            catch { file.Dispose(); throw; }
        }
        var metadata = new Zenith.Core.Storage.VfsFileMetadata();
        if (operation == ProcessPathOperation.Chmod) { return metadata.SetMode(path, (ushort)value) ? 0 : -5; }
        if (operation == ProcessPathOperation.Chown) { return metadata.SetOwner(path, (uint)value, extra == ulong.MaxValue ? null : (uint)extra) ? 0 : -5; }
        if (operation == ProcessPathOperation.Rename) { return VfsManager.TryRename(path, target!) ? 0 : -5; }
        int slash = path.LastIndexOf('/'); string name = path[(slash + 1)..];
        if (name.Length == 0 || name is "." or "..") { return -22; }
        string parentPath = slash == 0 ? "/" : path[..slash];
        if (!VfsManager.TryOpenDirectory(parentPath, out var parent)) { return -2; }
        int result;
        try
        {
            if (operation == ProcessPathOperation.Mkdir)
            {
                if (parent.Inode.InodeOperations.Lookup(parent.Inode, name, out _)) { parent.Dispose(); return -17; }
                bool made = parent.TryCreateDirectory(name, (VfsMode)value, out var created); created?.Dispose(); result = made ? 0 : -5;
            }
            else if (operation == ProcessPathOperation.Symlink)
            {
                if (parent.Inode.InodeOperations.Lookup(parent.Inode, name, out _)) { parent.Dispose(); return -17; }
                bool made = parent.TrySymlink(name, target!, out var created); created?.Dispose(); result = made ? 0 : -5;
            }
            else
            {
                if (!parent.Inode.InodeOperations.Lookup(parent.Inode, name, out _)) { parent.Dispose(); return -2; }
                result = (operation == ProcessPathOperation.Unlink ? parent.TryUnlink(name) : parent.TryRemoveDirectory(name)) ? 0 : -5;
            }
            parent.Dispose(); return result;
        }
        catch { parent.Dispose(); throw; }
    }
    private static bool TouchNode(IVfsInode node)
    {
        var now = new VfsTimespec(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 0);
        return node.InodeOperations.SetAttr(node, SetAttrFlags.Mtime, new VfsStat { Mtime = now });
    }
    private static ProcessFileInfo Info(VfsStat stat) => new(stat.Size, (uint)stat.Mode, stat.Uid, stat.Gid);
    public ProcessFileInfo Stat(string path)
    {
        if (!VfsManager.TryStat(path, out var stat)) { throw new FileNotFoundException("native path unavailable"); }
        return Info(stat);
    }
    public ProcessFileInfo LStat(string path)
    {
        if (path.Length > 1) { path = path.TrimEnd('/'); }
        foreach (var mount in VfsManager.Mounts)
        { if (path == mount.MountPoint) { return Stat(path); } }
        int slash = path.LastIndexOf('/'); string name = path[(slash + 1)..];
        if (!VfsManager.TryOpenDirectory(slash == 0 ? "/" : path[..slash], out var parent)) { throw new DirectoryNotFoundException("native parent unavailable"); }
        try
        {
            if (!parent.Inode.InodeOperations.Lookup(parent.Inode, name, out var node)
                || !node.InodeOperations.GetAttr(node, out var stat)) { throw new FileNotFoundException("native path unavailable"); }
            parent.Dispose(); return Info(stat);
        }
        catch { parent.Dispose(); throw; }
    }
    public IProcessFile OpenCopyDestination(string path, IProcessFile source)
    {
        if (source is not RegularFile regular || regular.Writable) { throw new ArgumentException("copy source must be a readable regular file"); }
        return OpenFile(path, ProcessFiles.WriteOnly | ProcessFiles.Create | ProcessFiles.Truncate, regular.Node);
    }
    public IProcessFile Open(string path, ulong flags) => OpenFile(path, flags, null);
    private IProcessFile OpenFile(string path, ulong flags, IVfsInode? copying)
    {
        if (flags == 1)
        {
            if (!VfsManager.TryOpenDirectory(path, out var dir)) { throw new FileNotFoundException("native directory unavailable"); }
            try
            {
                if (!dir.TryReadDir(out var entries)) { throw new IOException("directory enumeration failed"); }
                if (entries.Count > 4096) { throw new IOException("directory exceeds native limit"); }
                return new DirectoryFile(dir, entries);
            }
            catch { dir.Dispose(); throw; }
        }
        bool writable = (flags & ProcessFiles.WriteOnly) != 0;
        IVfsInode? expected = writable ? WriteTarget(path, flags) : null;
        if (copying is not null && ReferenceEquals(expected, copying)) { throw new ArgumentException("copy source and destination identify the same file"); }
        if (!VfsManager.TryOpenFile(path, out var file)) { throw new FileNotFoundException("native file unavailable"); }
        try
        {
            if (writable && !ReferenceEquals(expected, file.Inode))
            { throw new IOException("native path changed during open"); }
            if (!file.TryStat(out var stat) || ((uint)stat.Mode & 0xf000) != 0x8000) { throw new IOException("native read requires regular file"); }
            FileAccessPolicy.Require(file.Inode, writable ? PermissionPolicy.Write : PermissionPolicy.Read);
            if (writable && stat.Size > ProcessFiles.MaximumTransfer) { throw new IOException("native write file exceeds 1 MiB"); }
            if ((flags & ProcessFiles.Truncate) != 0
                && !file.TrySetAttr(SetAttrFlags.Size, new VfsStat { Size = 0 }))
            { throw new IOException("native truncate failed"); }
            return new RegularFile(file, writable, (flags & ProcessFiles.Append) != 0);
        }
        catch { file.Dispose(); throw; }
    }
    private static IVfsInode WriteTarget(string path, ulong flags)
    {
        int slash = path.LastIndexOf('/'); string name = path[(slash + 1)..];
        if (name.Length == 0 || name is "." or "..") { throw new ArgumentException("invalid native create path"); }
        string parentPath = slash == 0 ? "/" : path[..slash];
        if (!VfsManager.TryOpenDirectory(parentPath, out var parent)) { throw new DirectoryNotFoundException("native parent unavailable"); }
        try
        {
            if (!parent.Inode.InodeOperations.Lookup(parent.Inode, name, out var target))
            {
                if ((flags & ProcessFiles.Create) == 0) { throw new FileNotFoundException("native file unavailable"); }
                if (!parent.TryCreateFile(name, (VfsMode)0x1a4, out var created)) { throw new IOException("native create failed"); }
                target = created.Inode; created.Dispose();
            }
            if (!target.InodeOperations.GetAttr(target, out var stat) || ((uint)stat.Mode & 0xf000) != 0x8000)
            { throw new IOException("native write requires a regular non-symlink file"); }
            // Pin identity before the path-based tracked open; reject namespace
            // replacement before truncate/write, including same-number foreign inodes.
            parent.Dispose(); return target;
        }
        catch { parent.Dispose(); throw; }
    }
    private sealed class RegularFile(IVfsFileHandle file, bool writable, bool append) : IProcessFile, ISeekableProcessFile
    {
        public bool Writable => writable;
        public IVfsInode Node => file.Inode;
        public long Seek(long offset, int origin)
        {
            if (!file.TrySeek(offset, (SeekWhence)origin)) { return -22; }
            return file.Position;
        }
        public int Read(byte[] buffer) => writable ? -9 : checked((int)file.Read(buffer));
        public int Write(byte[] buffer)
        {
            if (!writable) { return -9; }
            FileAccessPolicy.Require(file.Inode, PermissionPolicy.Write);
            if (append && !file.TrySeek(0, SeekWhence.End)) { throw new IOException("native append seek failed"); }
            if (file.Position < 0 || file.Position > ProcessFiles.MaximumTransfer - buffer.Length) { return -28; }
            return checked((int)file.Write(buffer));
        }
        public int Flush()
        {
            if (!writable) { return -9; }
            FileAccessPolicy.Require(file.Inode, PermissionPolicy.Write);
            if (!file.TryFlush()) { throw new IOException("native flush failed"); }
            return 0;
        }
        public (string, ProcessFileInfo)? Next() => throw new IOException("not a directory");
        public void Dispose()
        {
            // Explicit exceptional cleanup: Cosmos skips unwind-finally.
            try { if (writable && !file.TryFlush()) { throw new IOException("native close flush failed"); } file.Dispose(); }
            catch { file.Dispose(); throw; }
        }
    }
    private sealed class DirectoryFile(IVfsDirectoryHandle directory, IReadOnlyList<IVfsInode> entries) : IProcessFile
    {
        private int _index;
        public int Read(byte[] buffer) => throw new IOException("is a directory");
        public int Write(byte[] buffer) => -9;
        public int Flush() => -9;
        public (string, ProcessFileInfo)? Next()
        {
            FileAccessPolicy.Require(directory.Inode, PermissionPolicy.Read | PermissionPolicy.Search);
            if (_index == entries.Count) { return null; }
            var node = entries[_index++];
            if (!node.InodeOperations.GetAttr(node, out var stat)) { throw new IOException("directory metadata failed"); }
            return (node.Name, Info(stat));
        }
        public void Dispose() => directory.Dispose();
    }
}
