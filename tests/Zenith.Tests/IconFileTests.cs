using System;
using Zenith.Core.Packages;

namespace Zenith.Tests;

public sealed class IconFileTests
{
    private static byte[] File1()
    {
        byte[] runs = { 1, 2, 9, 255, 1, 0 };   // 0, 9, 255, 0 for a 2x2 mask
        var f = new byte[5 + 6 + runs.Length]; "ZIC1"u8.CopyTo(f); f[4] = 1; f[5] = 2; f[7] = (byte)runs.Length; runs.CopyTo(f, 11); return f;
    }
    [Fact]
    public void ReadsAndDecodesARequestedSize()
    {
        Assert.True(IconFile.TryFind(File1(), 2, out var runs));
        var out4 = new byte[4]; Assert.True(IconFile.TryDecode(runs, 2, out4));
        Assert.Equal(new byte[] { 0, 9, 255, 0 }, out4);
        Assert.False(IconFile.TryFind(File1(), 3, out _));
    }
    [Fact]
    public void RefusesMalformedFilesAndNames()
    {
        var f = File1(); f[0] = 0; Assert.False(IconFile.TryFind(f, 2, out _));
        f = File1(); f[7] = 200; Assert.False(IconFile.TryFind(f, 2, out _));      // run length past the end
        f = File1(); Array.Resize(ref f, f.Length + 1); Assert.False(IconFile.TryFind(f, 2, out _)); // trailing data
        Assert.False(IconFile.TryFind(new byte[IconFile.MaximumBytes + 1], 2, out _));
        var small = new byte[4];
        Assert.False(IconFile.TryDecode(new byte[] { 200, 0 }, 2, small));         // wrong total
        Assert.False(IconFile.TryDecode(new byte[] { 0, 200, 1 }, 2, small));      // literals past the input
        Assert.False(IconFile.TryDecode(new byte[] { 0, 9, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, 2, small)); // past the mask
        Assert.True(IconFile.ValidName("arrow-left")); Assert.False(IconFile.ValidName("../x")); Assert.False(IconFile.ValidName("")); Assert.False(IconFile.ValidName("A"));
    }
}
