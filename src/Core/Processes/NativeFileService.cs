using System;
using System.Collections.Generic;
using System.IO;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Vfs;
using Zenith.Core.Files;
using Zenith.Core.Security;

namespace Zenith.Core.Processes;

/// <summary>The VFS side of zenith-native-files-abi 1.0, as the caller's own identity (the native service path
/// enters it before these run). Listing re-reads the folder for every page; at most 4096 entries.</summary>
internal static class NativeFileService
{
    public static long List(string path, uint flags, int offset, out byte[] page)
    {
        page = Array.Empty<byte>();
        if (path.Length > 1) { path = path.TrimEnd('/'); }
        if (!VfsManager.TryStat(path, out var self)) { return -2; }
        if (!self.IsDirectory) { return -20; }
        if (!VfsManager.TryOpenDirectory(path, out var directory)) { return -2; }
        var entries = new List<FileListEntry>();
        try
        {
            FileAccessPolicy.Require(directory.Inode, PermissionPolicy.Read | PermissionPolicy.Search);
            if (!directory.TryReadDir(out var nodes)) { directory.Dispose(); return -5; }
            if (nodes.Count > ProcessFileService.MaximumEntries) { directory.Dispose(); return -27; }
            int counted = 0;
            foreach (var node in nodes)
            {
                if (node.Name is "." or "..") { continue; }
                if (!node.InodeOperations.GetAttr(node, out var stat)) { continue; }
                string child = path == "/" ? "/" + node.Name : path + "/" + node.Name;
                uint mode = (uint)stat.Mode, target = 0; int items = -1;
                if ((mode & 0xf000) == 0xa000 && VfsManager.TryStat(child, out var resolved)) { target = (uint)resolved.Mode; }
                bool folder = (mode & 0xf000) == 0x4000 || (target & 0xf000) == 0x4000;
                if (folder && (flags & ProcessFileService.CountItems) != 0 && counted < ProcessFileService.CountedFolders)
                { counted++; items = Count(child); }
                entries.Add(new(node.Name, mode, stat.Uid, stat.Gid, stat.Size, stat.Mtime.TvSec, stat.NLink, items, target));
            }
            directory.Dispose();
        }
        catch { directory.Dispose(); throw; }
        var caller = CredentialContext.Current;
        bool writable = !path.StartsWith("/proc", StringComparison.Ordinal)
            && PermissionPolicy.Allows(caller, self.Uid, self.Gid, (ushort)self.Mode, PermissionPolicy.Write | PermissionPolicy.Search);
        long now = new DateTimeOffset(Zenith.Core.Time.SystemClock.UtcNow).ToUnixTimeSeconds();
        page = ProcessFileService.Page(entries, offset, self.Mtime.TvSec, (uint)self.Mode, writable, now, ZoneOffset, out int count);
        return count;
    }

    private static int ZoneOffset(long seconds)
    {
        if (seconds is < -62135596800 or > 253402300799) { return 0; }
        return (int)Zenith.Core.Time.SystemClock.Zone.OffsetAt(DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime).TotalSeconds;
    }

    /// <summary>Entries in a folder the caller may read, or -1.</summary>
    private static int Count(string path)
    {
        if (!VfsManager.TryOpenDirectory(path, out var directory)) { return -1; }
        try
        {
            var stat = default(VfsStat);
            if (!directory.Inode.InodeOperations.GetAttr(directory.Inode, out stat)
                || !PermissionPolicy.Allows(CredentialContext.Current, stat.Uid, stat.Gid, (ushort)stat.Mode, PermissionPolicy.Read | PermissionPolicy.Search))
            { directory.Dispose(); return -1; }
            int n = -1;
            if (directory.TryReadDir(out var nodes))
            { n = 0; foreach (var node in nodes) { if (node.Name is not ("." or "..")) { n++; } } }
            directory.Dispose(); return n;
        }
        catch { directory.Dispose(); throw; }
    }

    public static long Filesystem(string path, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (path.Length > 1) { path = path.TrimEnd('/'); }
        if (!VfsManager.TryStat(path, out _)) { return -2; }
        VfsManager.VfsMount? best = null;
        foreach (var m in VfsManager.Mounts)
        {
            if (Within(path, m.MountPoint) && (best is null || m.MountPoint.Length > best.MountPoint.Length)) { best = m; }
        }
        if (best is null || !VfsManager.TryStatFs(best.MountPoint, out VfsStatFs st)) { return -5; }
        bool unix = VfsManager.TryOpenDirectory(best.MountPoint, out var root) && Unix(root);
        string type = best.MountPoint is "/proc" ? "proc" : best.MountPoint is "/dev" ? "dev" : unix ? "ext2" : "fat";
        data = ProcessFileService.Filesystem(st.Blocks * st.BlockSize, st.Bavail * st.BlockSize, st.BlockSize, best.MountPoint == "/proc", unix, best.MountPoint, type);
        return 0;
    }
    private static bool Unix(IVfsDirectoryHandle root) { bool unix = GuardedFilesystem.IsUnix(root.Inode); root.Dispose(); return unix; }
    private static bool Within(string path, string mount) => mount == "/" || path == mount || path.StartsWith(mount + "/", StringComparison.Ordinal);

    /// <summary>The job store over System.IO (which applies the VFS policy as the thread's identity) and the
    /// native namespace calls for links and renames.</summary>
    public sealed class Store : IFileJobStore
    {
        private readonly NativeProcessFiles _files = new();
        /// <summary>lstat without exceptions: a missing name is the common case (every target is probed).</summary>
        public FileKind Kind(string path, out ulong size)
        {
            size = 0;
            VfsStat stat;
            bool mount = path == "/";
            foreach (var m in VfsManager.Mounts) { mount |= m.MountPoint == path; }
            if (mount) { if (!VfsManager.TryStat(path, out stat)) { return FileKind.Missing; } }
            else
            {
                int slash = path.LastIndexOf('/');
                if (!VfsManager.TryOpenDirectory(slash <= 0 ? "/" : path[..slash], out var parent)) { return FileKind.Missing; }
                stat = default; bool known;
                try
                {
                    bool found = parent.Inode.InodeOperations.Lookup(parent.Inode, path[(slash + 1)..], out var node);
                    known = found && node!.InodeOperations.GetAttr(node, out stat);
                }
                catch { parent.Dispose(); throw; }
                parent.Dispose();
                if (!known) { return FileKind.Missing; }
            }
            size = stat.Size;
            return ((uint)stat.Mode & 0xf000) switch { 0x8000 => FileKind.File, 0x4000 => FileKind.Directory, 0xa000 => FileKind.Link, _ => FileKind.Special };
        }
        public List<string> Names(string directory)
        {
            if (!VfsManager.TryOpenDirectory(directory, out var handle)) { throw new DirectoryNotFoundException("the folder is gone"); }
            try
            {
                FileAccessPolicy.Require(handle.Inode, PermissionPolicy.Read | PermissionPolicy.Search);
                if (!handle.TryReadDir(out var nodes)) { throw new IOException("the folder cannot be read"); }
                var names = new List<string>();
                foreach (var node in nodes) { if (node.Name is not ("." or "..")) { names.Add(node.Name); } }
                handle.Dispose(); return names;
            }
            catch { handle.Dispose(); throw; }
        }
        public void CreateDirectory(string path)
        {
            long r = _files.Mutate(ProcessPathOperation.Mkdir, path, null, 0x1ed, 0);
            if (r < 0) { throw new IOException(r == -17 ? "it already exists" : "the folder could not be created"); }
        }
        public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read);
        public Stream CreateNew(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        public void DeleteFile(string path)
        {
            if (_files.Mutate(ProcessPathOperation.Unlink, path, null, 0, 0) < 0) { throw new IOException("it could not be removed"); }
        }
        public void DeleteDirectory(string path)
        {
            if (_files.Mutate(ProcessPathOperation.Rmdir, path, null, 0, 0) < 0) { throw new IOException("the folder could not be removed"); }
        }
        public bool TryRename(string from, string to) => _files.Mutate(ProcessPathOperation.Rename, from, to, 0, 0) == 0;
        public string ReadLink(string path)
        {
            if (!new Zenith.Core.Storage.VfsFileMetadata().TryRead(path, out var metadata) || metadata.Target is not string target)
            { throw new IOException("the link cannot be read"); }
            return target;
        }
        public void Symlink(string path, string target)
        {
            if (_files.Mutate(ProcessPathOperation.Symlink, path, target, 0, 0) < 0) { throw new IOException("the link could not be created"); }
        }
        public void SetMode(string path, uint mode) { try { _files.Mutate(ProcessPathOperation.Chmod, path, null, mode & 0x1ff, 0); } catch (Exception) { } }
        public uint Mode(string path) => new Zenith.Core.Storage.VfsFileMetadata().TryRead(path, out var metadata) ? (uint)metadata.Mode & 0x1ff : 0;
        public ulong Available(string directory)
        {
            string best = "/";
            foreach (var m in VfsManager.Mounts) { if (Within(directory, m.MountPoint) && m.MountPoint.Length > best.Length) { best = m.MountPoint; } }
            return VfsManager.TryStatFs(best, out VfsStatFs st) ? st.Bavail * st.BlockSize : 0;
        }
        public bool SameFilesystem(string a, string b) => MountOf(a) == MountOf(b);
        private static string MountOf(string path)
        {
            string best = "/";
            foreach (var m in VfsManager.Mounts) { if (Within(path, m.MountPoint) && m.MountPoint.Length > best.Length) { best = m.MountPoint; } }
            return best;
        }
    }
}
