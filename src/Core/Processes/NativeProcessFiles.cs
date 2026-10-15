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
    public IProcessFile Open(string path, bool directory)
    {
        if (directory)
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
        if (!VfsManager.TryOpenFile(path, out var file)) { throw new FileNotFoundException("native file unavailable"); }
        try
        {
            if (!file.TryStat(out var stat) || ((uint)stat.Mode & 0xf000) != 0x8000) { throw new IOException("native read requires regular file"); }
            return new RegularFile(file);
        }
        catch { file.Dispose(); throw; }
    }
    private sealed class RegularFile(IVfsFileHandle file) : IProcessFile
    {
        public int Read(byte[] buffer) => checked((int)file.Read(buffer));
        public (string, ProcessFileInfo)? Next() => throw new IOException("not a directory");
        public void Dispose() => file.Dispose();
    }
    private sealed class DirectoryFile(IVfsDirectoryHandle directory, IReadOnlyList<IVfsInode> entries) : IProcessFile
    {
        private int _index;
        public int Read(byte[] buffer) => throw new IOException("is a directory");
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
