using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Zenith.Core.Files;
using Zenith.Core.Security;

namespace Zenith.Tests;

/// <summary>Horizon HZ4 archives: the kernel's inflate/deflate against .NET's, and extract/compress jobs against
/// archives .NET writes and reads.</summary>
public sealed class ArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-archives-" + Guid.NewGuid().ToString("N"));
    private static int s_pid = 90000;
    private readonly int _pid = Interlocked.Increment(ref s_pid);
    public ArchiveTests() { Directory.CreateDirectory(_root); }
    public void Dispose() { FileJobs.ProcessExited(_pid); try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static byte[] Sample(int size, int seed, bool text)
    {
        var r = new Random(seed); var b = new byte[size];
        if (!text) { r.NextBytes(b); return b; }
        string[] words = { "zenith ", "horizon ", "aurora ", "kernel ", "the ", "file ", "\n", "archive ", "1234 " };
        int i = 0; while (i < size) { foreach (char c in words[r.Next(words.Length)]) { if (i < size) { b[i++] = (byte)c; } } }
        return b;
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(70000, false)] [InlineData(300000, true)] [InlineData(5, true)]
    public void InflateReadsWhatDotNetDeflates(int size, bool text)
    {
        byte[] data = Sample(size, size + 1, text);
        foreach (var level in new[] { CompressionLevel.NoCompression, CompressionLevel.Fastest, CompressionLevel.SmallestSize })
        {
            var packed = new MemoryStream();
            using (var d = new DeflateStream(packed, level, leaveOpen: true)) { d.Write(data); }
            if (packed.Length == 0) { continue; }   // .NET writes nothing at all for empty input
            packed.Position = 0; var output = new MemoryStream();
            Assert.Equal(size, Inflater.Decompress(packed, output, long.MaxValue));
            Assert.Equal(data, output.ToArray());
        }
    }

    [Theory]
    [InlineData(0, false)] [InlineData(3, true)] [InlineData(100000, false)] [InlineData(400000, true)]
    public void DotNetInflatesWhatDeflaterWrites(int size, bool text)
    {
        byte[] data = Sample(size, size + 7, text);
        var packed = new MemoryStream();
        Deflater.Compress(new MemoryStream(data), packed);
        if (text && size > 1000) { Assert.True(packed.Length < size / 2, "text compresses: " + packed.Length + " of " + size); }
        packed.Position = 0; var output = new MemoryStream();
        using (var d = new DeflateStream(packed, CompressionMode.Decompress)) { d.CopyTo(output); }
        Assert.Equal(data, output.ToArray());
    }

    [Fact]
    public void InflateStopsAtItsLimitAndOnDamage()
    {
        var packed = new MemoryStream();
        using (var d = new DeflateStream(packed, CompressionLevel.SmallestSize, leaveOpen: true)) { d.Write(new byte[1 << 20]); }
        packed.Position = 0;
        Assert.Throws<InvalidDataException>(() => Inflater.Decompress(packed, new MemoryStream(), 4096));
        Assert.Throws<InvalidDataException>(() => Inflater.Decompress(new MemoryStream(new byte[] { 0xff, 0xff, 0xff }), new MemoryStream(), 100));
    }

    private string P(string relative) => _root + "/" + relative;
    private FileJobStatus Run(FileJobOperation op, string destination, params string[] sources)
    {
        long id = FileJobs.Start(_pid, Credentials.Root, op, 0, destination, sources, new HostStore(), new object());
        Assert.True(id > 0, "start returned " + id);
        var job = FileJobs.Find(id, _pid)!;
        for (int i = 0; i < 3000 && !job.Finished; i++) { Thread.Sleep(10); }
        Assert.True(job.Finished);
        return job.Status;
    }
    private void Tree()
    {
        Directory.CreateDirectory(P("src/photos/2026"));
        File.WriteAllBytes(P("src/photos/2026/a.bin"), Sample(200000, 3, false));
        File.WriteAllBytes(P("src/notes.txt"), Sample(50000, 4, true));
        File.WriteAllText(P("src/" + new string('n', 120) + ".txt"), "long name");
        Directory.CreateDirectory(P("src/empty"));
        Directory.CreateDirectory(P("out"));
    }
    private void SameTree(string expected, string actual)
    {
        var a = new SortedSet<string>(StringComparer.Ordinal); var b = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string f in Directory.GetFileSystemEntries(expected, "*", SearchOption.AllDirectories)) { a.Add(Path.GetRelativePath(expected, f)); }
        foreach (string f in Directory.GetFileSystemEntries(actual, "*", SearchOption.AllDirectories)) { b.Add(Path.GetRelativePath(actual, f)); }
        Assert.Equal(a, b);
        foreach (string f in a) { if (File.Exists(Path.Combine(expected, f))) { Assert.Equal(File.ReadAllBytes(Path.Combine(expected, f)), File.ReadAllBytes(Path.Combine(actual, f))); } }
    }

    [Fact]
    public void ZipRoundTripsThroughDotNet()
    {
        Tree();
        var s = Run(FileJobOperation.Compress, P("out/src.zip"), P("src"));
        Assert.Equal(FileJobState.Done, s.State); Assert.Equal(0, s.Errors);
        // .NET reads Zenith's zip...
        ZipFile.ExtractToDirectory(P("out/src.zip"), P("dotnet"));
        SameTree(P("src"), P("dotnet/src"));
        // ...and Zenith reads .NET's (dynamic Huffman blocks).
        File.Delete(P("out/src.zip"));
        ZipFile.CreateFromDirectory(P("src"), P("out/theirs.zip"), CompressionLevel.SmallestSize, includeBaseDirectory: true);
        s = Run(FileJobOperation.Extract, P("out"), P("out/theirs.zip"));
        Assert.Equal(FileJobState.Done, s.State); Assert.Equal(0, s.Errors);
        SameTree(P("src"), P("out/theirs/src"));
        // Extracting again makes a second folder instead of mixing into the first.
        Run(FileJobOperation.Extract, P("out"), P("out/theirs.zip"));
        Assert.True(Directory.Exists(P("out/theirs (2)/src")));
    }

    [Fact]
    public void TarAndTarGzRoundTripThroughDotNet()
    {
        Tree();
        var s = Run(FileJobOperation.Compress, P("out/src.tar.gz"), P("src"));
        Assert.Equal(FileJobState.Done, s.State); Assert.Equal(0, s.Errors);
        Directory.CreateDirectory(P("dotnet"));
        using (var gz = new GZipStream(File.OpenRead(P("out/src.tar.gz")), CompressionMode.Decompress)) { TarFile.ExtractToDirectory(gz, P("dotnet"), false); }
        SameTree(P("src"), P("dotnet/src"));
        Run(FileJobOperation.Compress, P("out/plain.tar"), P("src"));
        s = Run(FileJobOperation.Extract, P("out"), P("out/plain.tar"));
        Assert.Equal(0, s.Errors); SameTree(P("src"), P("out/plain/src"));
        // .NET's pax/GNU tar.gz.
        using (var file = File.Create(P("out/theirs.tgz"))) using (var gz = new GZipStream(file, CompressionLevel.Optimal)) { TarFile.CreateFromDirectory(P("src"), gz, true); }
        s = Run(FileJobOperation.Extract, P("out"), P("out/theirs.tgz"));
        Assert.Equal(0, s.Errors); SameTree(P("src"), P("out/theirs/src"));
        // A single gzip file.
        using (var file = File.Create(P("out/notes.txt.gz"))) using (var gz = new GZipStream(file, CompressionLevel.Optimal)) { gz.Write(File.ReadAllBytes(P("src/notes.txt"))); }
        s = Run(FileJobOperation.Extract, P("out"), P("out/notes.txt.gz"));
        Assert.Equal(0, s.Errors); Assert.Equal(File.ReadAllBytes(P("src/notes.txt")), File.ReadAllBytes(P("out/notes.txt")));
    }

    [Fact]
    public void UnsafeAndUnsupportedEntriesAreRefusedOneByOne()
    {
        Directory.CreateDirectory(P("out"));
        using (var zip = ZipFile.Open(P("out/evil.zip"), ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("../escape.txt").Open())) { w.Write("no"); }
            using (var w = new StreamWriter(zip.CreateEntry("/abs.txt").Open())) { w.Write("abs"); }
            using (var w = new StreamWriter(zip.CreateEntry("fine/ok.txt").Open())) { w.Write("ok"); }
        }
        var s = Run(FileJobOperation.Extract, P("out"), P("out/evil.zip"));
        Assert.Equal(FileJobState.Done, s.State); Assert.Equal(1, s.Errors);   // "../escape.txt" only; "/abs.txt" lands inside
        Assert.False(File.Exists(P("escape.txt"))); Assert.False(File.Exists(P("out/escape.txt")));
        Assert.Equal("ok", File.ReadAllText(P("out/evil/fine/ok.txt")));
        Assert.Equal("abs", File.ReadAllText(P("out/evil/abs.txt")));
        Assert.Null(FileJob.SafeEntry("a/../../b")); Assert.Equal("a/b", FileJob.SafeEntry("./a//b/"));
        Assert.Equal(-22, FileJobs.Start(_pid, Credentials.Root, FileJobOperation.Compress, 0, P("out/x.rar"), new[] { P("out") }, new HostStore(), new object()));
        Assert.Equal(-22, FileJobs.Start(_pid, Credentials.Root, FileJobOperation.Extract, 0, P("out"), new[] { P("out/evil.txt") }, new HostStore(), new object()));
    }

    private sealed class HostStore : IFileJobStore
    {
        public FileKind Kind(string path, out ulong size)
        {
            size = 0; var info = new FileInfo(path);
            if (info.LinkTarget is not null) { return FileKind.Link; }
            if (Directory.Exists(path)) { return FileKind.Directory; }
            if (!info.Exists) { return FileKind.Missing; }
            size = (ulong)info.Length; return FileKind.File;
        }
        public List<string> Names(string directory)
        {
            var names = new List<string>(); foreach (string e in Directory.GetFileSystemEntries(directory)) { names.Add(Path.GetFileName(e)); }
            names.Sort(StringComparer.Ordinal); return names;
        }
        public void CreateDirectory(string path) { if (Directory.Exists(path) || File.Exists(path)) { throw new IOException("it already exists"); } Directory.CreateDirectory(path); }
        public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read);
        public Stream CreateNew(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        public void DeleteFile(string path) => File.Delete(path);
        public void DeleteDirectory(string path) => Directory.Delete(path);
        public bool TryRename(string from, string to) { File.Move(from, to); return true; }
        public string ReadLink(string path) => new FileInfo(path).LinkTarget!;
        public void Symlink(string path, string target) => File.CreateSymbolicLink(path, target);
        public ulong Available(string directory) => ulong.MaxValue / 2;
        public bool SameFilesystem(string a, string b) => true;
    }
}
