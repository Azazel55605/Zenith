using System;
using System.Collections.Generic;
using System.IO;
using Zenith.Core.Processes;

namespace Zenith.Tests;

public class ProcessFilesTests
{
    private sealed class File : IProcessFile
    {
        public bool Closed, FailClose;
        private int _offset;
        public int Read(byte[] buffer)
        { if (_offset++ > 0) { return 0; } if (buffer.Length > 0) { buffer[0] = 42; return 1; } return 0; }
        public (string, ProcessFileInfo)? Next() => _offset++ == 0 ? ("hello", new(9, 0x81a4, 1000, 1000)) : null;
        public void Dispose() { Closed = true; if (FailClose) { throw new IOException("close fixture"); } }
    }
    private sealed class Backend : IProcessFiles
    {
        public List<File> Opened = new();
        public string? Path;
        public IProcessFile Open(string path, bool directory) { Path = path; var file = new File(); Opened.Add(file); return file; }
        public ProcessFileInfo Stat(string path) => new(9, 0x81a4, 1000, 1000);
    }
    [Fact]
    public void HandlesAreBoundedClosedInvalidatedAndNeverInherited()
    {
        var backend = new Backend(); var files = new ProcessFiles(backend, "/home/user");
        int fd = files.Open("test", 0); Assert.Equal("/home/user/test", backend.Path);
        Assert.Equal(1, files.Read(fd, new byte[1])); Assert.Equal(0, files.Read(fd, new byte[1]));
        Assert.Equal(0, files.Close(fd)); Assert.True(backend.Opened[0].Closed);
        Assert.Equal(-9, files.Read(fd, new byte[1])); Assert.Equal(-9, files.Close(fd));
        for (int i = 0; i < 16; i++) { Assert.True(files.Open("test", 0) >= 3); }
        Assert.Equal(-24, files.Open("test", 0)); Assert.Equal(-22, files.Open("test", 2));
        files.Dispose(); Assert.All(backend.Opened, file => Assert.True(file.Closed));
        var next = new ProcessFiles(backend, "/"); Assert.Equal(-9, next.Read(fd, new byte[1]));
    }
    [Fact]
    public void FaultDuringOneCloseDoesNotSkipOtherDescriptors()
    {
        var backend = new Backend(); var files = new ProcessFiles(backend, "/");
        files.Open("first",0); files.Open("second",0); backend.Opened[0].FailClose = true;
        files.Dispose(); Assert.All(backend.Opened, file => Assert.True(file.Closed));
    }
    [Fact]
    public void DirectoryWireRecordAndShortBuffersDoNotConsumeEntry()
    {
        var files = new ProcessFiles(new Backend(), "/"); int fd = files.Open("/tmp", 1);
        Assert.Equal(-22, files.Next(fd, new byte[12]));
        byte[] buffer = new byte[280]; Assert.Equal(280, files.Next(fd, buffer));
        Assert.Equal(9UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(buffer));
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(buffer, 24, 5)); Assert.Equal(0, buffer[29]);
        Assert.Equal(0, files.Next(fd, buffer)); files.Dispose();
    }
    [Fact]
    public void CwdIsPrivateAndPathsDoNotCollapsePermissionRelevantAncestors()
    {
        var files = new ProcessFiles(new Backend(), "/home/user");
        Assert.Equal("/home/user", files.Cwd);
        Assert.Equal("/home/user/denied/../target", files.Resolve("denied/../target"));
        Assert.Equal("/etc/test", files.Resolve("/etc/test"));
        Assert.Throws<ArgumentException>(() => files.Resolve(""));
        Assert.Throws<ArgumentException>(() => files.Resolve("a\0b"));
        Assert.Throws<ArgumentException>(() => files.Resolve(new string('x',1025)));
    }
    [Fact]
    public void CopyoutRejectsReadonlyKernelOverflowAndUnmappedGaps()
    {
        var ranges = new[] { new ProcessMemoryRange(0x400000,0x401000,false), new ProcessMemoryRange(0x401000,0x402000,true), new ProcessMemoryRange(0x403000,0x404000,true) };
        Assert.True(ProcessMemoryRange.Allows(ranges,0x400fff,2,false));
        Assert.False(ProcessMemoryRange.Allows(ranges,0x400fff,2,true));
        Assert.True(ProcessMemoryRange.Allows(ranges,0x401fff,1,true));
        Assert.False(ProcessMemoryRange.Allows(ranges,0x401fff,2,true));
        Assert.False(ProcessMemoryRange.Allows(ranges,0xffff800000000000,4,false));
        Assert.False(ProcessMemoryRange.Allows(ranges,ulong.MaxValue-1,4,false));
        Assert.True(ProcessMemoryRange.Allows(ranges,0,0,true));
    }
}
