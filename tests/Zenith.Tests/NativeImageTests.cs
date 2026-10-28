using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using Zenith.Core.Processes;
namespace Zenith.Tests;
public class NativeImageTests
{
    private static byte[] Image()
    {
        byte[] bytes = new byte[8192]; bytes[0]=127;bytes[1]=(byte)'E';bytes[2]=(byte)'L';bytes[3]=(byte)'F';bytes[4]=2;bytes[5]=1;bytes[6]=1;
        U16(bytes,16,2);U16(bytes,18,62);U32(bytes,20,1);U64(bytes,24,0x400000);U64(bytes,32,64);U16(bytes,52,64);U16(bytes,54,56);U16(bytes,56,2);
        U32(bytes,64,1);U32(bytes,68,5);U64(bytes,72,4096);U64(bytes,80,0x400000);U64(bytes,96,32);U64(bytes,104,4096);U64(bytes,112,4096);
        U32(bytes,120,4);U64(bytes,128,4128);U64(bytes,152,24);
        U32(bytes,4128,7);U32(bytes,4132,4);U32(bytes,4136,1);"ZENITH\0"u8.CopyTo(bytes.AsSpan(4140));U32(bytes,4148,1);
        return bytes;
    }
    private static void U16(byte[] b,int o,ushort n)=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o),n);
    private static void U32(byte[] b,int o,uint n)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o),n);
    private static void U64(byte[] b,int o,ulong n)=>BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o),n);
    [Fact] public void ValidStaticFileHasBoundedExecutableSegment()
    { var image=ElfImage.Read(Image());Assert.Equal(0x400000UL,image.Entry);Assert.Equal(5U,Assert.Single(image.Segments).Flags); }
    [Fact] public void LargerBoundedStaticCanvasRetainsEightMiBImageLimit()
    {
        var bytes=Image();U64(bytes,104,0x800000);Assert.Equal(2048,ElfImage.Read(bytes).Segments[0].Pages);
        U64(bytes,104,0x800001);Assert.Throws<IOException>(()=>ElfImage.Read(bytes));
    }
    [Theory]
    [InlineData(16,3UL)] [InlineData(18,183UL)] [InlineData(32,ulong.MaxValue)] [InlineData(56,17UL)]
    [InlineData(68,7UL)] [InlineData(72,ulong.MaxValue)] [InlineData(80,0xffff800000000000UL)]
    [InlineData(96,8193UL)] [InlineData(104,0x900000UL)] [InlineData(112,1UL)] [InlineData(120,2UL)]
    [InlineData(4148,2UL)] [InlineData(24,0x401000UL)]
    public void BadFormatAlignmentKernelAddressesWxAndAbiAreRefused(int offset,ulong value)
    {
        var bytes=Image();
        if(offset is 16 or 18 or 56)U16(bytes,offset,(ushort)value);
        else if(offset is 68 or 120 or 4148)U32(bytes,offset,(uint)value);
        else U64(bytes,offset,value);
        Assert.Throws<IOException>(()=>ElfImage.Read(bytes));
    }
    [Fact] public void OverlappingPagesAreRejectedEvenWhenByteRangesDiffer()
    {
        var bytes=Image();U16(bytes,56,3);U32(bytes,176,1);U32(bytes,180,6);U64(bytes,184,4096);U64(bytes,192,0x400000);U64(bytes,208,1);U64(bytes,216,4096);U64(bytes,224,4096);
        Assert.Throws<IOException>(()=>ElfImage.Read(bytes));
    }
    [Fact] public void ArgumentStackHasAlignedPointersAndUtf8WithoutKernelPointers()
    {
        var stack=ProcessArguments.Create(new[]{"sunbeam","echo","Grüße"});int cursor=(int)(stack.StackPointer-(ProcessArguments.Top-ProcessArguments.StackBytes));
        Assert.Equal(0UL,stack.StackPointer%16);Assert.Equal(3UL,BinaryPrimitives.ReadUInt64LittleEndian(stack.Bytes.AsSpan(cursor)));
        for(int i=0;i<3;i++)
        {
            ulong pointer=BinaryPrimitives.ReadUInt64LittleEndian(stack.Bytes.AsSpan(cursor+8+i*8));
            Assert.InRange(pointer,ProcessArguments.Top-ProcessArguments.StackBytes,ProcessArguments.Top-1);
            int offset=(int)(pointer-(ProcessArguments.Top-ProcessArguments.StackBytes)),end=Array.IndexOf(stack.Bytes,(byte)0,offset);
            Assert.Equal(new[]{"sunbeam","echo","Grüße"}[i],Encoding.UTF8.GetString(stack.Bytes,offset,end-offset));
        }
        Assert.Equal(0UL,BinaryPrimitives.ReadUInt64LittleEndian(stack.Bytes.AsSpan(cursor+32)));
    }
    [Fact] public void OversizedArgumentsAndNulAreRejected()
    {
        Assert.Throws<IOException>(()=>ProcessArguments.Create(new[]{"sunbeam","bad\0argument"}));
        Assert.Throws<IOException>(()=>ProcessArguments.Create(new[]{new string('x',1025)}));
        Assert.Throws<IOException>(()=>ProcessArguments.Create(Enumerable.Repeat("x",33).ToArray()));
    }
}
