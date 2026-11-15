using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Zenith.Core.Files;
using Zenith.Core.Processes;
using Zenith.Core.Security;

namespace Zenith.Tests;

/// <summary>Horizon's transfer jobs (zenith-native-files-abi 1.0) over a temporary directory, and the ABI encoding.</summary>
public sealed class FileJobTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-jobs-" + Guid.NewGuid().ToString("N"));
    private static int s_pid = 70000;
    private readonly int _pid = Interlocked.Increment(ref s_pid);

    public FileJobTests() { Directory.CreateDirectory(_root); }
    public void Dispose() { FileJobs.ProcessExited(_pid); try { Directory.Delete(_root, true); } catch (IOException) { } }

    private sealed class HostStore : IFileJobStore
    {
        public ulong Space = ulong.MaxValue / 2;
        public bool Same = true;
        public Action<string>? OnRead;
        public FileKind Kind(string path, out ulong size)
        {
            size = 0;
            var info = new FileInfo(path);
            if (info.LinkTarget is not null) { return FileKind.Link; }
            if (Directory.Exists(path)) { return FileKind.Directory; }
            if (!info.Exists) { return FileKind.Missing; }
            size = (ulong)info.Length; return FileKind.File;
        }
        public List<string> Names(string directory)
        {
            var names = new List<string>();
            foreach (string entry in Directory.GetFileSystemEntries(directory)) { names.Add(Path.GetFileName(entry)); }
            names.Sort(StringComparer.Ordinal); return names;
        }
        public void CreateDirectory(string path) { if (Directory.Exists(path) || File.Exists(path)) { throw new IOException("it already exists"); } Directory.CreateDirectory(path); }
        public Stream OpenRead(string path) { OnRead?.Invoke(path); return new FileStream(path, FileMode.Open, FileAccess.Read); }
        public Stream CreateNew(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        public void DeleteFile(string path) => File.Delete(path);
        public void DeleteDirectory(string path) => Directory.Delete(path);
        public bool TryRename(string from, string to)
        {
            if (!Same) { return false; }
            if (Directory.Exists(from)) { Directory.Move(from, to); } else { File.Move(from, to); }
            return true;
        }
        public string ReadLink(string path) => new FileInfo(path).LinkTarget!;
        public void Symlink(string path, string target) => File.CreateSymbolicLink(path, target);
        public ulong Available(string directory) => Space;
        public bool SameFilesystem(string a, string b) => Same;
    }

    private string P(string relative) => relative.Length == 0 ? _root : _root + "/" + relative;
    private void Write(string relative, string text) { Directory.CreateDirectory(Path.GetDirectoryName(P(relative))!); File.WriteAllText(P(relative), text); }

    private FileJobStatus Run(FileJobOperation op, string destination, string[] sources, HostStore? store = null, uint flags = 0, Func<FileJob, bool>? onQuestion = null)
    {
        store ??= new HostStore();
        long id = FileJobs.Start(_pid, Credentials.Root, op, flags, destination, sources, store, new object());
        Assert.True(id > 0, "start returned " + id);
        var job = FileJobs.Find(id, _pid)!;
        for (int i = 0; i < 1000 && !job.Finished; i++)
        {
            if (job.Status.State == FileJobState.Asking && onQuestion is not null)
            {
                if (!onQuestion(job)) { break; }
                // One answer per question: wait until the job has taken it before looking again.
                for (int k = 0; k < 500 && job.Status.State == FileJobState.Asking; k++) { Thread.Sleep(2); }
            }
            Thread.Sleep(10);
        }
        Assert.True(job.Finished, "the job finished");
        return job.Status;
    }

    [Fact]
    public void CopiesATreeAndCountsItemsAndBytes()
    {
        Write("a/one.txt", "12345"); Write("a/sub/two.txt", "abc"); Directory.CreateDirectory(P("dest"));
        var s = Run(FileJobOperation.Copy, P("dest"), new[] { P("a") });
        Assert.Equal(FileJobState.Done, s.State);
        Assert.Equal(0, s.Errors);
        Assert.Equal(4, s.ItemsTotal); Assert.Equal(4, s.ItemsDone);
        Assert.Equal(8UL, s.BytesTotal); Assert.Equal(8UL, s.BytesDone);
        Assert.Equal("abc", File.ReadAllText(P("dest/a/sub/two.txt")));
        Assert.True(File.Exists(P("a/one.txt")), "a copy keeps the source");
    }

    [Fact]
    public void CopyIntoTheSameFolderKeepsBoth()
    {
        Write("plan.md", "x");
        var s = Run(FileJobOperation.Copy, _root, new[] { P("plan.md") });
        Assert.Equal(FileJobState.Done, s.State);
        Assert.True(File.Exists(P("plan (2).md")));
        Run(FileJobOperation.Copy, _root, new[] { P("plan.md") });
        Assert.True(File.Exists(P("plan (3).md")));
    }

    [Fact]
    public void AQuestionWaitsAndEachAnswerDoesWhatItSays()
    {
        Write("src/f.txt", "new"); Write("dest/f.txt", "old");
        var replaced = Run(FileJobOperation.Copy, P("dest"), new[] { P("src/f.txt") }, onQuestion: j =>
        {
            Assert.Equal(1, j.Status.QuestionKind); Assert.Equal("f.txt", j.Status.Question);
            Assert.False(j.Answer(FileJobAnswer.Merge, false), "merge is only for folders");
            return j.Answer(FileJobAnswer.Replace, false);
        });
        Assert.Equal(FileJobState.Done, replaced.State);
        Assert.Equal("new", File.ReadAllText(P("dest/f.txt")));

        Write("src/f.txt", "newer");
        Run(FileJobOperation.Copy, P("dest"), new[] { P("src/f.txt") }, onQuestion: j => j.Answer(FileJobAnswer.Skip, false));
        Assert.Equal("new", File.ReadAllText(P("dest/f.txt")));

        Run(FileJobOperation.Copy, P("dest"), new[] { P("src/f.txt") }, onQuestion: j => j.Answer(FileJobAnswer.KeepBoth, false));
        Assert.Equal("newer", File.ReadAllText(P("dest/f (2).txt")));
    }

    [Fact]
    public void FoldersMergeOnlyWhenAsked()
    {
        Write("src/docs/a.txt", "a"); Write("dest/docs/b.txt", "b");
        var s = Run(FileJobOperation.Copy, P("dest"), new[] { P("src/docs") }, onQuestion: j => { Assert.Equal(2, j.Status.QuestionKind); return j.Answer(FileJobAnswer.Merge, true); });
        Assert.Equal(FileJobState.Done, s.State);
        Assert.True(File.Exists(P("dest/docs/a.txt")) && File.Exists(P("dest/docs/b.txt")));
    }

    [Fact]
    public void ReplaceAllNeverAsks()
    {
        Write("src/x.txt", "1"); Write("src/y.txt", "2"); Write("dest/x.txt", "old"); Write("dest/y.txt", "old");
        var s = Run(FileJobOperation.Copy, P("dest"), new[] { P("src/x.txt"), P("src/y.txt") }, flags: FileJob.ReplaceAll,
            onQuestion: _ => throw new InvalidOperationException("asked"));
        Assert.Equal(FileJobState.Done, s.State);
        Assert.Equal("2", File.ReadAllText(P("dest/y.txt")));
    }

    [Fact]
    public void MoveWithinAFilesystemRenames()
    {
        Write("src/big/a.txt", "a"); Directory.CreateDirectory(P("dest"));
        var s = Run(FileJobOperation.Move, P("dest"), new[] { P("src/big") });
        Assert.Equal(FileJobState.Done, s.State);
        Assert.Equal(1, s.ItemsTotal);
        Assert.False(Directory.Exists(P("src/big")));
        Assert.True(File.Exists(P("dest/big/a.txt")));
    }

    [Fact]
    public void MoveAcrossFilesystemsCopiesThenRemovesTheSource()
    {
        Write("src/big/a.txt", "abc"); Write("src/big/b.txt", "de"); Directory.CreateDirectory(P("dest"));
        var s = Run(FileJobOperation.Move, P("dest"), new[] { P("src/big") }, new HostStore { Same = false });
        Assert.Equal(FileJobState.Done, s.State);
        Assert.Equal(5UL, s.BytesTotal);
        Assert.False(Directory.Exists(P("src/big")));
        Assert.Equal("de", File.ReadAllText(P("dest/big/b.txt")));
    }

    [Fact]
    public void DeleteRemovesATree()
    {
        Write("gone/a/b.txt", "x"); Write("gone/c.txt", "y");
        var s = Run(FileJobOperation.Delete, "", new[] { P("gone") });
        Assert.Equal(FileJobState.Done, s.State);
        Assert.Equal(4, s.ItemsTotal); Assert.Equal(4, s.ItemsDone);
        Assert.False(Directory.Exists(P("gone")));
    }

    [Fact]
    public void ACopyThatCannotFitFailsBeforeWriting()
    {
        Write("src/f.bin", new string('x', 4000)); Directory.CreateDirectory(P("dest"));
        var s = Run(FileJobOperation.Copy, P("dest"), new[] { P("src/f.bin") }, new HostStore { Space = 1000 });
        Assert.Equal(FileJobState.Failed, s.State);
        Assert.StartsWith("Not enough space: 3.9 KiB needed, 1000 B available", s.Message);
        Assert.False(File.Exists(P("dest/f.bin")));
    }

    [Fact]
    public void CancelRemovesTheHalfWrittenFile()
    {
        File.WriteAllBytes(P("big.bin"), new byte[FileJob.Chunk * 40]); Directory.CreateDirectory(P("dest"));
        FileJob? job = null; var gate = new object();
        // Cancelled once the destination is about to be written: the created file must go again.
        var store = new HostStore { OnRead = _ => job!.Cancel() };
        Monitor.Enter(gate);
        long id = FileJobs.Start(_pid, Credentials.Root, FileJobOperation.Copy, 0, P("dest"), new[] { P("big.bin") }, store, gate);
        job = FileJobs.Find(id, _pid)!;
        Monitor.Exit(gate);
        for (int i = 0; i < 500 && !job.Finished; i++) { Thread.Sleep(10); }
        Assert.Equal(FileJobState.Cancelled, job.Status.State);
        Assert.False(File.Exists(P("dest/big.bin")), "the partial copy is removed");
    }

    [Fact]
    public void SymbolicLinksAreCopiedAsLinks()
    {
        Write("src/real.txt", "r"); File.CreateSymbolicLink(P("src/link"), "real.txt"); Directory.CreateDirectory(P("dest"));
        var s = Run(FileJobOperation.Copy, P("dest"), new[] { P("src") });
        Assert.Equal(FileJobState.Done, s.State);
        Assert.Equal("real.txt", new FileInfo(P("dest/src/link")).LinkTarget);
    }

    [Fact]
    public void StartRefusesWhatCannotWork()
    {
        Write("a/x.txt", "x"); var store = new HostStore(); var gate = new object();
        Assert.Equal(-22, FileJobs.Start(_pid, Credentials.Root, FileJobOperation.Copy, 0, P("a"), new[] { P("a") }, store, gate));
        Assert.Equal(-22, FileJobs.Start(_pid, Credentials.Root, FileJobOperation.Copy, 0, P("a/x.txt"), new[] { P("a") }, store, gate));
        Write("b.txt", "b");
        Assert.Equal(-20, FileJobs.Start(_pid, Credentials.Root, FileJobOperation.Copy, 0, P("a/x.txt"), new[] { P("b.txt") }, store, gate));
        Assert.Equal(-2, FileJobs.Start(_pid, Credentials.Root, FileJobOperation.Copy, 0, P("a"), new[] { P("missing") }, store, gate));
        Assert.Equal(-22, FileJobs.Start(_pid, Credentials.Root, FileJobOperation.Delete, 0, "", new[] { "/" }, store, gate));
        Assert.Equal(-22, FileJobs.Start(_pid, Credentials.Root, FileJobOperation.Delete, 0, "", new[] { "relative" }, store, gate));
        Assert.Equal(-22, FileJobs.Start(_pid, Credentials.Root, FileJobOperation.Delete, 0, "", new[] { P("a/../a") }, store, gate));
    }

    [Fact]
    public void TwoJobsPerProgramAndCancelledAtExit()
    {
        File.WriteAllBytes(P("big.bin"), new byte[FileJob.Chunk * 4]); Directory.CreateDirectory(P("d1"));
        var store = new HostStore(); var gate = new object();
        Monitor.Enter(gate);
        long a = FileJobs.Start(_pid, Credentials.Root, FileJobOperation.Copy, 0, P("d1"), new[] { P("big.bin") }, store, gate);
        long b = FileJobs.Start(_pid, Credentials.Root, FileJobOperation.Copy, 0, P("d1"), new[] { P("big.bin") }, store, gate);
        long c = FileJobs.Start(_pid, Credentials.Root, FileJobOperation.Copy, 0, P("d1"), new[] { P("big.bin") }, store, gate);
        Assert.True(a > 0 && b > 0); Assert.Equal(-24, c);
        Assert.Null(FileJobs.Find(a, _pid + 1000));
        FileJobs.ProcessExited(_pid);
        Monitor.Exit(gate);
        var job = FileJobs.Find(a, _pid);
        for (int i = 0; job is not null && i < 500 && !job.Finished; i++) { Thread.Sleep(10); }
        Assert.True(job is null || job.Status.State == FileJobState.Cancelled);
    }

    // ---------- Encoding.

    [Fact]
    public void ListPagesSplitAndCarryEveryField()
    {
        var entries = new List<FileListEntry>();
        for (int i = 0; i < 1000; i++) { entries.Add(new("file-" + i + "-" + new string('n', 40) + ".txt", 0x81a4, 1000, 100, (ulong)i * 3, 1700000000 + i, 1, -1, 0)); }
        entries[0] = new("Ordner ü", 0x41ed, 1000, 100, 4096, 1699999999, 2, 6, 0);
        byte[] page = ProcessFileService.Page(entries, 0, 42, 0x41ed, true, out int count);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(page));
        Assert.Equal((uint)count, BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(4)));
        Assert.Equal(1000u, BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(8)));
        uint next = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(12));
        Assert.True(count < 1000 && next == (uint)count, "a full page points at the rest");
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(28)));
        var first = page.AsSpan(ProcessFileService.PageHeader);
        int record = BinaryPrimitives.ReadUInt16LittleEndian(first), nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(first[2..]);
        Assert.Equal(0, record % 8);
        Assert.Equal("Ordner ü", Encoding.UTF8.GetString(first.Slice(ProcessFileService.EntryHeader, nameBytes)));
        Assert.Equal(0, first[ProcessFileService.EntryHeader + nameBytes]);
        Assert.Equal(0x41edu, BinaryPrimitives.ReadUInt32LittleEndian(first[4..]));
        Assert.Equal(1699999999L, BinaryPrimitives.ReadInt64LittleEndian(first[24..]));
        Assert.Equal(6, BinaryPrimitives.ReadInt32LittleEndian(first[36..]));
        byte[] last = ProcessFileService.Page(entries, (int)next, 42, 0x41ed, false, out int rest);
        Assert.True(count + rest <= 1000);
    }

    [Fact]
    public void ListRequestsAreCheckedWhole()
    {
        byte[] b = new byte[ProcessFileService.ListRequestBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), 5); Encoding.ASCII.GetBytes("/home").CopyTo(b, 16);
        Assert.True(ProcessFileService.ParseList(b, out string path, out uint flags, out int offset));
        Assert.Equal("/home", path); Assert.Equal(1u, flags); Assert.Equal(0, offset);
        b[100] = 1; Assert.False(ProcessFileService.ParseList(b, out _, out _, out _)); b[100] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 2); Assert.False(ProcessFileService.ParseList(b, out _, out _, out _));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 0); b[17] = 0xff; Assert.False(ProcessFileService.ParseList(b, out _, out _, out _));
    }

    private static byte[] JobRequest(uint op, uint flags, params string[] parts)
    {
        var text = new List<byte>();
        foreach (string part in parts) { text.AddRange(Encoding.UTF8.GetBytes(part)); text.Add(0); }
        byte[] b = new byte[ProcessFileService.JobHeader + text.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), op);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), flags); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)parts.Length - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), (uint)text.Count); text.CopyTo(b, ProcessFileService.JobHeader);
        return b;
    }

    [Fact]
    public void JobRequestsCarryTheDestinationAndEverySource()
    {
        Assert.True(ProcessFileService.ParseJob(JobRequest(1, 1, "/home/user/Archive", "/home/user/a.txt", "/home/user/Fotos"), out var op, out uint flags, out string destination, out string[] sources));
        Assert.Equal(FileJobOperation.Copy, op); Assert.Equal(1u, flags); Assert.Equal("/home/user/Archive", destination);
        Assert.Equal(new[] { "/home/user/a.txt", "/home/user/Fotos" }, sources);
        Assert.True(ProcessFileService.ParseJob(JobRequest(3, 0, "", "/tmp/x"), out op, out _, out destination, out _));
        Assert.Equal(FileJobOperation.Delete, op); Assert.Equal("", destination);
        Assert.False(ProcessFileService.ParseJob(JobRequest(4, 0, "/a", "/b"), out _, out _, out _, out _));
        Assert.False(ProcessFileService.ParseJob(JobRequest(1, 0, "/a", ""), out _, out _, out _, out _));
        byte[] wrongCount = JobRequest(1, 0, "/a", "/b"); BinaryPrimitives.WriteUInt32LittleEndian(wrongCount.AsSpan(12), 2);
        Assert.False(ProcessFileService.ParseJob(wrongCount, out _, out _, out _, out _));
    }

    [Fact]
    public void StatusAndControl()
    {
        var s = new FileJobStatus(FileJobState.Asking, FileJobOperation.Move, 1, 3, 9, 100, 900, "budget.csv", "x: denied", "plan.md", 1);
        byte[] b = ProcessFileService.Status(s);
        Assert.Equal(512, b.Length);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)));
        Assert.Equal(900UL, BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(32)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(40)));
        Assert.Equal("budget.csv", Encoding.UTF8.GetString(b, 48, 10)); Assert.Equal(0, b[58]);
        Assert.Equal("plan.md", Encoding.UTF8.GetString(b, 368, 7));
        Assert.Equal(-9, ProcessFileService.Control(null, 1, 5, 1, 0));
    }
}
