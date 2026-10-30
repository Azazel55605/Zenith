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
        public ProcessSurface Open(int width, int height, string title, int format = 1)
        { var s = new ProcessSurface(width, height, title, format); Surfaces.Add(s); return s; }
    }
    [Fact]
    public void PointerMotionIsOptInCoalescedAndNeverDisplacesInput()
    {
        var h = new Host(); using var g = new ProcessGraphics(h);
        int handle = g.Open(160, 100, "M");
        Assert.Equal(-9, g.EnablePointerMotion(handle + 1));
        var surface = h.Surfaces[0];
        surface.SendPointerMove(1, 2);
        Assert.Equal(0, surface.Poll().Type);                         // legacy clients see nothing
        Assert.Equal(0, g.EnablePointerMotion(handle));
        surface.Send(new(3, 'a', 0, 0, 0, 0));
        for (int i = 0; i < 10000; i++) { surface.SendPointerMove(i % 160, i % 100); }
        Assert.Equal(3, surface.Poll().Type);                         // input first, never reset
        var move = surface.Poll();
        Assert.Equal(new ProcessGraphicsEvent(11, 0, 9999 % 160, 9999 % 100, 0, 0), move);
        Assert.Equal(0, surface.Poll().Type);                         // exactly one pending move
        surface.SendPointerLeave();
        Assert.Equal(new ProcessGraphicsEvent(11, 0, -1, -1, 0, 0), surface.Poll());
        surface.SendPointerMove(3, 3); surface.Dispose();
        Assert.Equal(1, surface.Poll().Type);                         // close stays sticky
        Assert.Equal(-9, g.EnablePointerMotion(handle + 1));
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
    public void RenewPresentationQuotaRetainsOwnedHandleAndRevocation()
    {
        var host = new Host(); using var graphics = new ProcessGraphics(host);
        int handle = graphics.Open(160, 100, "interactive"); byte[] pixels = new byte[64000];
        for (int i = 0; i < 3600; i++) { Assert.Equal(0, graphics.Present(handle, pixels)); }
        Assert.Equal(-27, graphics.Size(handle));
        var profile = new ProcessInteractive(); Assert.Equal(0, profile.Enable(graphics.OwnsLive(handle), 0));
        Assert.True(profile.Advance(graphics.Live, 60000, out bool renew)); Assert.True(renew);
        graphics.RenewBudget(); Assert.Equal(64000, graphics.Size(handle));
        Assert.Equal(0, graphics.Present(handle, pixels));
        host.Surfaces[0].Dispose(); Assert.False(graphics.Live);
        Assert.False(graphics.OwnsLive(handle));
        Assert.True(profile.Advance(graphics.Live, 60001, out renew)); Assert.False(renew);
        Assert.False(profile.Advance(graphics.Live, 62001, out renew)); Assert.False(renew);
        graphics.RenewBudget(); Assert.Equal(-32, graphics.Present(handle, pixels));
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
    [Fact]
    public void ResizeRequiresOptInOwnedHandleAndExactCurrentAcknowledgement()
    {
        var host = new Host();using var g = new ProcessGraphics(host);int handle=g.Open(600,480,"resize",2);var surface=host.Surfaces[0];
        surface.RequestResize(1280,692);Assert.Equal(600,surface.Width);
        Assert.Equal(-9,g.EnableResize(handle+1));Assert.Equal(0,g.EnableResize(handle));
        surface.RequestResize(1280,692);var e=g.Poll(handle)!.Value;Assert.Equal(10,e.Type);Assert.Equal(1280,e.X);Assert.Equal(692,e.Y);
        var snapshot=g.Viewport(handle)!;var request=new byte[24];BinaryPrimitives.WriteUInt32LittleEndian(request,1);
        snapshot.AsSpan(8,16).CopyTo(request.AsSpan(8));
        surface.RequestResize(1000,600);Assert.Equal(-11,g.AcceptResize(handle,request));Assert.Equal(600,surface.Width);
        snapshot=g.Viewport(handle)!;snapshot.AsSpan(8,16).CopyTo(request.AsSpan(8));Assert.Equal(0,g.AcceptResize(handle,request));
        Assert.Equal((1000,600),surface.FrameSize);Assert.Equal(2400000,g.Size(handle));
        Assert.False(surface.CopyFrame(new int[1000*600],false));
        surface.RequestResize(int.MaxValue,int.MaxValue);Assert.Equal((1000,600),surface.FrameSize);
        request[4]=1;Assert.Equal(-22,g.AcceptResize(handle,request));g.Close(handle);Assert.Null(g.Viewport(handle));
    }
}
