using System;
using System.Collections.Generic;
using System.IO;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Vfs;
using Zenith.Core.Security;

namespace Zenith.Core.Processes;

internal sealed class NativeProcessFiles : IProcessFiles
{
    private static ProcessFileInfo Info(VfsStat stat) => new(stat.Size, (uint)stat.Mode, stat.Uid, stat.Gid);
    public ProcessFileInfo Stat(string path)
    {
        if (!VfsManager.TryStat(path, out var stat)) { throw new FileNotFoundException("native path unavailable"); }
        return Info(stat);
    }
    public IProcessFile Open(string path, ulong flags)
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
    private sealed class RegularFile(IVfsFileHandle file, bool writable, bool append) : IProcessFile
    {
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
