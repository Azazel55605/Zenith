using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Zenith.Core.Processes;

namespace Zenith.Tests;
public class ProcessPathTests
{
    private static byte[] Request(uint operation=1)
    {
        var bytes=new byte[56];BinaryPrimitives.WriteUInt32LittleEndian(bytes,operation);
        Put(bytes,8,0x400100);Put(bytes,16,4);return bytes;
    }
    private static void Put(byte[] bytes,int offset,ulong value)=>BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset),value);
    [Fact]
    public void FixedRequestPreservesAllPointerLengthsAndOwnershipValues()
    {
        var bytes=Request(4);Put(bytes,24,0x400200);Put(bytes,32,8);
        Assert.True(ProcessPathRequest.TryRead(bytes,out var request));Assert.Equal(ProcessPathOperation.Rename,request.Operation);
        Assert.Equal(0x400100UL,request.Path);Assert.Equal(4,request.Length);Assert.Equal(8,request.TargetLength);
        Assert.False(ProcessPathRequest.TryRead(bytes.AsSpan(0,55),out _));
    }
    [Theory]
    [InlineData(0,0UL)] [InlineData(0,8UL)] [InlineData(4,1UL)] [InlineData(8,0UL)]
    [InlineData(16,0UL)] [InlineData(16,1025UL)] [InlineData(16,ulong.MaxValue)]
    [InlineData(24,1UL)] [InlineData(32,1UL)] [InlineData(40,4096UL)] [InlineData(48,1UL)]
    public void InvalidOrUnusedFieldsAreRefusedBeforeCallingTheFilesystem(int offset,ulong value)
    {
        var bytes=Request();Put(bytes,offset,value);Assert.False(ProcessPathRequest.TryRead(bytes,out _));
    }
    [Fact]
    public void SecondaryPathsAndNumericOwnershipHaveExplicitBounds()
    {
        var bytes=Request(7);Assert.False(ProcessPathRequest.TryRead(bytes,out _));
        Put(bytes,24,0x400300);Put(bytes,32,5);Assert.True(ProcessPathRequest.TryRead(bytes,out _));
        Put(bytes,40,1);Assert.False(ProcessPathRequest.TryRead(bytes,out _));
        bytes=Request(6);Put(bytes,40,1000);Put(bytes,48,ulong.MaxValue);Assert.True(ProcessPathRequest.TryRead(bytes,out _));
        Put(bytes,40,(ulong)uint.MaxValue+1);Assert.False(ProcessPathRequest.TryRead(bytes,out _));
    }
    private sealed class Files : IProcessFiles,IProcessNamespace
    {
        public (ProcessPathOperation Op,string Path,string? Target,ulong Value,ulong Extra)? Last;
        public IProcessFile Open(string path,ulong flags)=>throw new NotSupportedException();
        public ProcessFileInfo Stat(string path)=>throw new NotSupportedException();
        public int Mutate(ProcessPathOperation op,string path,string? target,ulong value,ulong extra)
        { Last=(op,path,target,value,extra);return 0; }
    }
    [Fact]
    public void RelativeMutationPathsRetainAncestorChecksAndSymlinkTargetsStayLiteral()
    {
        var source=new Files();using var files=new ProcessFiles(source,"/home/user");
        files.Mutate(ProcessPathOperation.Rename,"private/../old","new",0,0);
        Assert.Equal("/home/user/private/../old",source.Last!.Value.Path);Assert.Equal("/home/user/new",source.Last.Value.Target);
        files.Mutate(ProcessPathOperation.Symlink,"link","../target",0,0);
        Assert.Equal("/home/user/link",source.Last.Value.Path);Assert.Equal("../target",source.Last.Value.Target);
        Assert.Throws<ArgumentException>(()=>files.Mutate(ProcessPathOperation.Unlink,"bad\0path",null,0,0));
    }
}
