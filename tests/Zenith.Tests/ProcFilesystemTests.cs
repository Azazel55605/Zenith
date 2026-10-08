using System;
using System.IO;
using System.Text;
using Cosmos.Kernel.HAL.Vfs;
using Zenith.Core.Storage.Proc;

namespace Zenith.Tests;

public class ProcFilesystemTests
{
    private sealed class OpenFile(IVfsInode inode) : IVfsOpenFile
    {
        public IVfsInode Inode => inode;
        public IFileOperations Operations => inode.FileOperations!;
        public long Position { get; set; }
    }

    private static IVfsSuperblock Mount(Func<string>? memory = null)
    {
        var driver = new ProcFilesystemType(memory ?? (() => "MemTotal: 512 kB\n"), () => "proc /proc proc ro 0 0\n", () => "1.25\n", () => "\n");
        Assert.True(driver.TryMount("", MountFlags.ReadOnly, out var superblock));
        return superblock;
    }

    private static OpenFile Open(IVfsSuperblock superblock, string name)
    {
        Assert.True(superblock.Root.InodeOperations.Lookup(superblock.Root, name, out var inode));
        return new OpenFile(inode);
    }

    private static string Read(OpenFile file, int chunkSize = 3)
    {
        var result = new StringBuilder();
        byte[] buffer = new byte[chunkSize];
        long count;
        while ((count = file.Operations.Read(file, buffer)) > 0)
        {
            file.Position += count;
            result.Append(Encoding.UTF8.GetString(buffer, 0, (int)count));
        }
        return result.ToString();
    }

    [Fact]
    public void DirectoryListsOnlyKnownCaseSensitiveFiles()
    {
        var sb = Mount();
        Assert.True(sb.Root.InodeOperations.ReadDir(sb.Root, out var entries));
        Assert.Equal(new[] { "meminfo", "mounts", "uptime", "cmdline" }, System.Linq.Enumerable.Select(entries, e => e.Name));
        Assert.False(sb.Root.InodeOperations.Lookup(sb.Root, "Meminfo", out _));
        Assert.False(sb.Root.InodeOperations.Lookup(sb.Root, "missing", out _));
        Assert.False(entries[0].InodeOperations.ReadDir(entries[0], out var empty));
        Assert.Empty(empty);
        Assert.Null(sb.Root.FileOperations);
    }

    [Fact]
    public void PartialReadsKeepSnapshotAndNewHandlesRefresh()
    {
        string value = "before\n";
        var sb = Mount(() => value);
        var file = Open(sb, "meminfo");
        byte[] first = new byte[2];
        Assert.Equal(2, file.Operations.Read(file, first));
        Assert.Equal(0, file.Position); // Position belongs to the VFS wrapper.
        file.Position += 2;
        value = "after\n";
        Assert.Equal("before\n", Encoding.UTF8.GetString(first) + Read(file));
        Assert.Equal("after\n", Read(Open(sb, "meminfo")));
        file.Operations.Release(file);
    }

    [Fact]
    public void SeekSupportsRewindEndAndRejectsInvalidOffsets()
    {
        var file = Open(Mount(() => "abc\n"), "meminfo");
        Assert.Equal("abc\n", Read(file));
        Assert.True(file.Operations.Seek(file, 0, SeekWhence.Set, out _));
        Assert.Equal("abc\n", Read(file));
        Assert.True(file.Operations.Seek(file, -2, SeekWhence.End, out var position));
        Assert.Equal(2, position);
        Assert.Equal("c\n", Read(file));
        Assert.False(file.Operations.Seek(file, -5, SeekWhence.End, out _));
        Assert.Equal(4, file.Position);
        Assert.False(file.Operations.Seek(file, long.MaxValue, SeekWhence.Cur, out _));
        Assert.False(file.Operations.Seek(file, 0, (SeekWhence)99, out _));
        Assert.True(file.Operations.Seek(file, 99, SeekWhence.Set, out _));
        Assert.Equal("", Read(file));
    }

    [Fact]
    public void AllMutationsAreRejectedAndMetadataIsReadOnly()
    {
        var sb = Mount();
        var root = sb.Root;
        var ops = root.InodeOperations;
        Assert.False(ops.Create(root, "new", 0, out _));
        Assert.False(ops.Mkdir(root, "new", 0, out _));
        Assert.False(ops.Symlink(root, "new", "meminfo", out _));
        Assert.False(ops.Unlink(root, "meminfo"));
        Assert.False(ops.Rmdir(root, "new"));
        Assert.False(ops.Rename(root, "meminfo", root, "new"));
        var file = Open(sb, "meminfo");
        Assert.False(ops.SetAttr(file.Inode, SetAttrFlags.Size, new VfsStat()));
        Assert.Throws<IOException>(() => file.Operations.Write(file, new byte[] { 1 }));
        Assert.True(ops.GetAttr(root, out var dirStat));
        Assert.True(dirStat.IsDirectory);
        Assert.True(ops.GetAttr(file.Inode, out var stat));
        Assert.True(stat.IsRegularFile);
        Assert.Equal((VfsMode)0x124, stat.Mode & VfsMode.PermissionMask);
        Assert.NotEqual(dirStat.Ino, stat.Ino);
        Assert.Equal(0ul, stat.Size);
    }

    [Fact]
    public void VirtualFilesystemHasNoBackingDiskOrFormatOperation()
    {
        var driver = new ProcFilesystemType(() => "", () => "", () => "", () => "");
        Assert.False(driver.TryMount("disk", MountFlags.None, out _));
        Assert.False(driver.TryFormat("", null));
        Assert.False(driver.TryDestroy(""));
        var sb = Mount();
        Assert.True(sb.SuperOperations.StatFs(sb, out var stat));
        Assert.Equal(0ul, stat.Blocks);
        Assert.Equal(5ul, stat.Files);
        Assert.True(sb.SuperOperations.Sync(sb));
    }

    [Fact]
    public void EmptyReadDoesNotSampleAndReleaseDiscardsSnapshot()
    {
        int samples = 0;
        var file = Open(Mount(() => (++samples).ToString()), "meminfo");
        Assert.Equal(0, file.Operations.Read(file, Span<byte>.Empty));
        Assert.Equal(0, samples);
        Assert.Equal("1", Read(file));
        file.Operations.Release(file);
        file.Position = 0;
        Assert.Equal("2", Read(file));
    }
}
