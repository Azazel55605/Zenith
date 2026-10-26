using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using Zenith.Core.Processes;

namespace Zenith.Tests;

public sealed class ProcessGraphicsTests
{
    private sealed class Host : IProcessGraphicsHost
    {
        public readonly List<ProcessSurface> Surfaces = new();
        public ProcessSurface Open(int width, int height, string title)
        { var s = new ProcessSurface(width, height, title); Surfaces.Add(s); return s; }
    }
    [Fact]
    public void OwnedHandlesNeverAliasOtherInvocationsOrClosedHandles()
    {
        var h = new Host(); using var a = new ProcessGraphics(h); using var b = new ProcessGraphics(h);
        int handle = a.Open(160, 100, "A");
        Assert.Equal(-9, b.Close(handle)); Assert.Equal(-24, a.Open(160, 100, "duplicate"));
        Assert.Equal(0, a.Close(handle)); Assert.Equal(-9, a.Size(handle));
        int next = a.Open(160, 100, "B"); Assert.NotEqual(handle, next);
        Assert.Equal(-9, a.Close(handle)); Assert.False(h.Surfaces[1].Ended);
    }
    [Fact]
    public void DisposalAndStickyCloseCannotBeOverwrittenByInputFlood()
    {
        var h = new Host(); var a = new ProcessGraphics(h); int n = a.Open(160, 100, "A");
        var s = h.Surfaces[0]; s.RequestClose();
        for (int i = 0; i < 1000; i++) { s.Send(new(3, i, 0, 0, 0, 0)); }
        Assert.Equal(1, a.Poll(n)!.Value.Type); Assert.Equal(-32, a.Present(n, new byte[64000]));
        a.Dispose(); a.Dispose(); Assert.Equal(-9, a.Size(n)); Assert.Equal(-9, a.Open(160, 100, "resurrection")); Assert.True(s.Ended);
    }
    [Fact]
    public void QueueOverflowSignalsResetAndRemainsBounded()
    {
        var s = new ProcessSurface(160, 100, "A");
        for (int i = 0; i < 65; i++) { s.Send(new(3, i, 0, 0, 0, 0)); }
        Assert.Equal(7, s.Poll().Type); Assert.Equal(64, s.Poll().Code); Assert.Equal(0, s.Poll().Type);
    }
    [Fact]
    public void FrameIsOwnedOpaqueAndFixedSize()
    {
        var s = new ProcessSurface(160, 100, "A"); byte[] source = new byte[s.Bytes];
        BinaryPrimitives.WriteUInt32LittleEndian(source, 0x00123456);
        Assert.Equal(-22, s.Present(new byte[4])); Assert.Equal(0, s.Present(source));
        Array.Clear(source); int[] frame = new int[16000];
        Assert.True(s.CopyFrame(frame, false)); Assert.Equal(unchecked((int)0xff123456), frame[0]);
        frame[0] = 0; Assert.True(s.CopyFrame(frame, true)); Assert.NotEqual(0, frame[0]);
        Assert.False(s.CopyFrame(frame, false)); s.Dispose(); Assert.False(s.CopyFrame(frame, true));
    }
    [Fact]
    public void InvalidDimensionsTitlesAndHeadlessDoNotCreateSurfaces()
    {
        var h = new Host(); using var a = new ProcessGraphics(h);
        foreach (var title in new[] { "", "a\nb", "\0", new string('x', 81) })
        { Assert.Equal(-22, a.Open(160, 100, title)); }
        Assert.Equal(-22, a.Open(int.MaxValue, 100, "bad")); Assert.Empty(h.Surfaces);
        using var none = new ProcessGraphics(null); Assert.Equal(-19, none.Open(160, 100, "headless"));
    }
    [Fact]
    public void OpenAndPresentationHaveIndependentFiniteBudgets()
    {
        var h = new Host(); using var a = new ProcessGraphics(h);
        for (int i = 0; i < 4; i++) { Assert.Equal(0, a.Close(a.Open(160, 100, "A"))); }
        Assert.Equal(-24, a.Open(160, 100, "A"));
    }
    [Fact]
    public void RequestRejectsUnsupportedVersionFlagsSizesAndReservedFields()
    {
        byte[] b = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 640);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), 480); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), 80);
        Assert.True(ProcessGraphicsRequest.TryRead(b, out var r)); Assert.Equal(640, r.Width);
        foreach (int offset in new[] { 0, 4, 8, 12, 24, 32, 40 })
        {
            byte[] bad = (byte[])b.Clone(); bad[offset] = 255;
            Assert.False(ProcessGraphicsRequest.TryRead(bad, out _));
        }
        Assert.False(ProcessGraphicsRequest.TryRead(b.AsSpan(1), out _));
    }
    [Fact]
    public void EventWireHasFixedLayoutAndSignedCoordinates()
    {
        byte[] b = new ProcessGraphicsEvent(6, 1, -3, 100, 7, 2).Encode();
        Assert.Equal(24, b.Length); Assert.Equal(-3, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8)));
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(16)));
    }
}
