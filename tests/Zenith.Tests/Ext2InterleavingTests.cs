using System;
using System.Threading;
using System.Threading.Tasks;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.HAL.Vfs;
using Zenith.Core.Storage.Ext2;

namespace Zenith.Tests;

public sealed class Ext2InterleavingTests
{
    private sealed class OpenFile(Ext2Inode inode) : IVfsOpenFile
    {
        public IVfsInode Inode => inode;
        public IFileOperations Operations => inode.Superblock.FileOps;
        public long Position { get; set; }
    }

    private sealed class InterruptingDisk : IBlockDevice
    {
        private readonly Ext2AllocationTests.Disk _inner;
        public Action? AfterRead;
        public InterruptingDisk(Ext2AllocationTests.Disk inner) { _inner = inner; }
        public string Name => "interrupted-inode-table";
        public ulong BlockSize => _inner.BlockSize;
        public ulong BlockCount => _inner.BlockCount;
        public void ReadBlock(ulong start, ulong count, Span<byte> bytes)
        {
            _inner.ReadBlock(start, count, bytes);
            var interrupt = AfterRead;
            AfterRead = null;
            interrupt?.Invoke();
        }
        public void WriteBlock(ulong start, ulong count, ReadOnlySpan<byte> bytes) => _inner.WriteBlock(start, count, bytes);
        public void Flush() => _inner.Flush();
    }

    private static Ext2AllocationTests.Disk Baseline()
    {
        var storage = new Ext2AllocationTests.Disk(1024);
        Ext2Formatter.Format(storage, "INTERLEAVE");
        Assert.True(Ext2Superblock.TryCreate(storage, out var mounted));
        for (uint number = 12; number <= 57; number++)
        {
            IVfsInode? created;
            bool ok = number == 49
                ? mounted!.InodeOps.Mkdir(mounted.Root, "directory", (VfsMode)0x1ed, out created)
                : mounted!.InodeOps.Create(mounted.Root, "file-" + number, (VfsMode)0x1a4, out created);
            Assert.True(ok);
            Assert.Equal(number, Assert.IsType<Ext2Inode>(created).InodeNumber);
        }
        Assert.True(Ext2Checker.Check(storage, out var report), report);
        return storage;
    }

    [Fact]
    public void InterruptedMetadataWriteMustNotTurnNeighbourDirectoryIntoRegularFile()
    {
        var storage = Baseline();
        // Inodes 49..56 and 57..64 occupy different table blocks. The first
        // slot is a directory in block one and a regular file in block two.
        var disk = new InterruptingDisk(storage);
        Assert.True(Ext2Superblock.TryCreate(disk, out var mounted));
        var changing = mounted!.ReadInode(50, "file-50");
        changing.Mtime = 123;
        // Force a different inode-table read between the outer RMW's read
        // and write, exactly where a preempted filesystem worker can resume.
        disk.AfterRead = () => mounted.ReadInode(57, "file-57");
        mounted.WriteInode(changing);
        Assert.True(Ext2Superblock.TryCreate(storage, out var reopened));
        Assert.True(reopened!.ReadInode(49, "directory").IsDirectory);
        Assert.True(reopened.ReadInode(57, "file-57").IsRegularFile);
        Assert.Equal(123u, reopened.ReadInode(50, "file-50").Mtime);
        Assert.True(Ext2Checker.Check(storage, out var report), report);
    }

    [Fact]
    public void ConcurrentWritesToNeighbourInodesPreserveBothUpdates()
    {
        var storage = Baseline();
        var disk = new InterruptingDisk(storage);
        Assert.True(Ext2Superblock.TryCreate(disk, out var mounted));
        var file = mounted!.ReadInode(50, "file-50");
        var directory = mounted.ReadInode(49, "directory");
        file.Mtime = 123; directory.Uid = 456;
        using var started = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        Exception? failure = null;
        var second = new Thread(() =>
        {
            started.Set();
            try { mounted.WriteInode(directory); }
            catch (Exception error) { failure = error; }
            finally { finished.Set(); }
        });
        disk.AfterRead = () =>
        {
            second.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            // Before serialization the second RMW finishes against the first's
            // stale snapshot. With the gate it waits, then reads the new block.
            Assert.True(SpinWait.SpinUntil(() => finished.IsSet
                || (second.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
        };
        mounted.WriteInode(file);
        Assert.True(second.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
        Assert.True(Ext2Superblock.TryCreate(storage, out var reopened));
        Assert.Equal(456u, reopened!.ReadInode(49, "directory").Uid);
        Assert.True(reopened.ReadInode(49, "directory").IsDirectory);
        Assert.Equal(123u, reopened.ReadInode(50, "file-50").Mtime);
        Assert.True(Ext2Checker.Check(storage, out var report), report);
    }

    [Fact]
    public async Task FailedMetadataReadReleasesVolumeForAnotherWorker()
    {
        var storage = Baseline();
        Assert.True(Ext2Superblock.TryCreate(storage, out var mounted));
        Assert.Throws<ArgumentOutOfRangeException>(() => mounted!.ReadInode(0, "invalid"));
        var directory = await Task.Run(() => mounted!.ReadInode(49, "directory"))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(directory.IsDirectory);
    }

    [Fact]
    public async Task ConcurrentFileCreationAndIndirectWritesSurviveRemountAndAudit()
    {
        var storage = Baseline();
        Assert.True(Ext2Superblock.TryCreate(storage, out var mounted));
        using var start = new ManualResetEventSlim();
        var workers = new Task[4];
        for (int worker = 0; worker < workers.Length; worker++)
        {
            int id = worker;
            workers[id] = Task.Run(() =>
            {
                start.Wait();
                for (int file = 0; file < 3; file++)
                {
                    string name = $"worker-{id}-{file}";
                    Assert.True(mounted!.InodeOps.Create(mounted.Root, name, (VfsMode)0x1a4, out var inode));
                    var open = new OpenFile(Assert.IsType<Ext2Inode>(inode));
                    byte[] data = new byte[20 * 1024 + 17];
                    Array.Fill(data, (byte)(id * 3 + file + 1));
                    Assert.Equal(data.Length, open.Operations.Write(open, data));
                }
            });
        }
        start.Set();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15));
        mounted!.Flush();
        Assert.True(Ext2Superblock.TryCreate(storage, out var reopened));
        for (int worker = 0; worker < workers.Length; worker++)
        {
            for (int file = 0; file < 3; file++)
            {
                Assert.True(reopened!.InodeOps.Lookup(reopened.Root, $"worker-{worker}-{file}", out var inode));
                var open = new OpenFile(Assert.IsType<Ext2Inode>(inode));
                byte[] data = new byte[20 * 1024 + 17];
                Assert.Equal(data.Length, open.Operations.Read(open, data));
                Assert.All(data, value => Assert.Equal((byte)(worker * 3 + file + 1), value));
            }
        }
        Assert.True(reopened!.ReadInode(49, "directory").IsDirectory);
        Assert.True(Ext2Checker.Check(storage, out var report), report);
    }
}
