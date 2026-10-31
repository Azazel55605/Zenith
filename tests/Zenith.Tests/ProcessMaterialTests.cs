using System;
using System.Buffers.Binary;
using Zenith.Core.Processes;
using Zenith.Gui.Graphics;

namespace Zenith.Tests;

public class ProcessMaterialTests
{
    private static byte[] Foreground(int width = 160, int height = 100)
    {
        byte[] b = new byte[width * height * 4];
        for (int i = 0; i < b.Length; i += 4) { b[i + 3] = 255; }
        return b;
    }
    [Fact]
    public void CompactBackgroundBlurDiffusesDetailAndRetainsOpaqueForeground()
    {
        const int w = 601, h = 481, scale = 4;
        int[] scene = new int[w*h], frame = new int[w*h], output = new int[w*h];
        for (int i=0;i<scene.Length;i++) { scene[i] = (i % w & 1) == 0 ? -1 : unchecked((int)0xff000000); }
        frame[300] = unchecked((int)0xffabcdef);
        var region = new[] { new ProcessMaterialRegion(0,0,w,h,0,1,2,210) };
        int samples = MaterialComposition.SampleLength(w,scale)*MaterialComposition.SampleLength(h,scale);
        var composition = new MaterialComposition(samples,true,scale);
        composition.Capture(scene,w,h,new(0,0,w,h),region,1,0xff25262b);
        composition.Compose(frame,output,w,h,new(0,0,w,h),region,1,0xff25262b,0xff25262b,0xff25262b,0xff25262b);
        Assert.Equal(unchecked((int)0xffabcdef),output[300]);
        Assert.InRange(Math.Abs((output[w*200+200]&255)-(output[w*200+201]&255)),0,1);
        Assert.Equal(1,composition.BlurRebuilds);
        composition.Capture(scene,w,h,new(0,0,w,h),region,1,0xff25262b);
        Assert.Equal(1,composition.BlurRebuilds);
        Array.Fill(scene,unchecked((int)0xff102030));
        composition.Capture(scene,w,h,new(-2,-3,w,h),region,1,0xff25262b);
        Assert.Equal(2,composition.BlurRebuilds);
    }

    [Fact]
    public void NewOpenIsExplicitAndLegacyFormatCannotBecomeTransparent()
    {
        byte[] r = new byte[48];BinaryPrimitives.WriteUInt32LittleEndian(r, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), 1);BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(8), 160);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(12), 100);BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(24), 1);
        Assert.True(ProcessGraphicsRequest.TryRead(r, out var parsed));Assert.Equal(2, parsed.Format);
        r[4] = 0;Assert.False(ProcessGraphicsRequest.TryRead(r, out _));r[0] = 1;
        Assert.True(ProcessGraphicsRequest.TryRead(r, out parsed));Assert.Equal(1, parsed.Format);
        var old = new ProcessSurface(160, 100, "old");Assert.Equal(-22, old.PresentMaterials(Foreground(), Array.Empty<ProcessMaterialRegion>()));
        var modern = new ProcessSurface(160, 100, "new", 2);Assert.Equal(-22, modern.Present(Foreground()));
    }
    [Fact]
    public void RegionsRejectOverlapOverflowUnsupportedRolesAndWorkAmplification()
    {
        var good = new ProcessMaterialRegion(0, 0, 80, 100, 8, 3, 2);
        Assert.True(ProcessMaterialRegion.Validate(new[] { good }, 160, 100));
        foreach (var bad in new[] { good with { X = -1 }, good with { Width = int.MaxValue }, good with { Radius = 25 },
            good with { Role = 1 }, good with { Effect = 3 }, good with { Width = 0 } })
        { Assert.False(ProcessMaterialRegion.Validate(new[] { bad }, 160, 100)); }
        Assert.False(ProcessMaterialRegion.Validate(new[] { good, good with { X = 40 } }, 160, 100));
        Assert.False(ProcessMaterialRegion.Validate(new[] { new ProcessMaterialRegion(0, 0, 640, 480, 0, 4, 2) }, 640, 480));
        Assert.False(ProcessMaterialRegion.TryRead(new byte[32], 9, out _));
        byte[] wire = new byte[32];wire[28] = 1;Assert.False(ProcessMaterialRegion.TryRead(wire, 1, out _));
    }
    [Fact]
    public void FramesValidatePremultiplicationScopeAndAtomicOwnership()
    {
        var s = new ProcessSurface(160, 100, "new", 2);byte[] frame = Foreground();
        var regions = new[] { new ProcessMaterialRegion(0, 0, 80, 100, 0, 3, 2) };
        frame[0] = 80;frame[3] = 128;Assert.Equal(0, s.PresentMaterials(frame, regions));
        frame[0] = 200;Assert.Equal(-22, s.PresentMaterials(frame, regions));
        frame[0] = 80;frame[80 * 4 + 3] = 0;Assert.Equal(-22, s.PresentMaterials(frame, regions));
        regions[0] = regions[0] with { X = -1 };int[] pixels = new int[16000];var copied = new ProcessMaterialRegion[8];
        Assert.True(s.CopyFrame(pixels, false, copied, out int count));Assert.Equal(1, count);Assert.Equal(0, copied[0].X);
        Assert.Equal(0x80000050U, (uint)pixels[0]); // failed frames and caller edits never replace the accepted snapshot
        Assert.False(s.CopyFrame(pixels, false, copied, out _));s.Dispose();Assert.Equal(-32, s.PresentMaterials(frame, regions));
    }
    [Fact]
    public void WireLengthsVersionsReservedAndCapabilitiesAreFixed()
    {
        byte[] b = new byte[48];b[0] = 1;Assert.True(ProcessMaterialFrame.TryRead(b, out _));
        b[32] = 9;Assert.False(ProcessMaterialFrame.TryRead(b, out _));b[32] = 0;
        b[40] = 1;Assert.False(ProcessMaterialFrame.TryRead(b, out _));b[40] = 0;
        b[24] = 1;Assert.False(ProcessMaterialFrame.TryRead(b, out _));
        Assert.False(ProcessMaterialFrame.TryRead(new byte[47], out _));
        var caps = ProcessMaterialFrame.Capabilities();Assert.Equal(32, caps.Length);
        Assert.Equal(131072U, BinaryPrimitives.ReadUInt32LittleEndian(caps.AsSpan(16)));
    }
    [Fact]
    public void CachedBlurUsesOnlyLowerSceneAndNeverFadesOpaqueForeground()
    {
        const int w = 160, h = 100;int[] scene = new int[w * h], frame = new int[w * h];
        for (int y = 0; y < h; y++)for (int x = 0; x < w; x++)
        { scene[y * w + x] = (x % 2 == 0) ? unchecked((int)0xff000000) : -1;frame[y * w + x] = x < 80 ? 0 : unchecked((int)0xff112233); }
        var regions = new[] { new ProcessMaterialRegion(0, 0, 80, 100, 0, 3, 2) };
        var composition = new MaterialComposition();var content = new Rect(0, 0, w, h);
        frame[20 * w + 20] = unchecked((int)0xffffffff);
        composition.Capture(scene, w, h, content, regions, 1, 0xff202125);Assert.Equal(1, composition.BlurRebuilds);
        composition.Capture(scene, w, h, content, regions, 1, 0xff202125);Assert.Equal(1, composition.BlurRebuilds);
        var output = (int[])scene.Clone();composition.Compose(frame, output, w, h, content, regions, 1, 0xff202125, 0xff2a2c31, 0xff25262b, 0xff2a2c31);
        Assert.Equal(-1, output[20 * w + 20]);Assert.Equal(unchecked((int)0xff112233), output[90]);
        // Neighboring stripes are diffused rather than merely faded with the window.
        Assert.InRange(Math.Abs((output[40] & 255) - (output[41] & 255)), 0, 6);
        scene[0] = unchecked((int)0xffff0000);composition.Capture(scene, w, h, content, regions, 1, 0xff202125);
        Assert.Equal(2, composition.BlurRebuilds);
        regions[0] = regions[0] with { Effect = 0 };composition.Capture(scene, w, h, content, regions, 1, 0xff202125);
        composition.Compose(frame, output, w, h, content, regions, 1, 0xff202125, 0xff2a2c31, 0xff25262b, 0xff2a2c31);
        Assert.Equal(unchecked((int)0xff25262b), output[40]);
    }
    [Fact]
    public void MovingAndClippedMaterialWindowsRefreshTheirOwnLowerSceneOnly()
    {
        const int size = 100;var scene = new int[size * size];var frame = new int[160 * 100];
        for (int y = 0; y < size; y++)for (int x = 0; x < size; x++)
        { scene[y * size + x] = unchecked((int)0xff000000) | (x << 16) | (y << 8); }
        for (int y = 0; y < 100; y++)for (int x = 80; x < 160; x++) { frame[y * 160 + x] = -1; }
        var regions = new[] { new ProcessMaterialRegion(0, 0, 80, 100, 12, 3, 2) };
        var c = new MaterialComposition();var rect = new Rect(-40, 10, 160, 100);
        c.Capture(scene, size, size, rect, regions, 1, 0xff202125);
        var output = (int[])scene.Clone();c.Compose(frame, output, size, size, rect, regions, 1, 0xff202125, 0xff2a2c31, 0xff25262b, 0xff2a2c31);
        for (int x = 0; x < size; x++) { Assert.Equal(scene[x], output[x]); } // above this window
        Assert.Equal(-1, output[20 * size + 90]);Assert.Equal(1, c.BlurRebuilds);
        c.Capture(scene, size, size, rect.Offset(1, 0), regions, 1, 0xff202125);Assert.Equal(2, c.BlurRebuilds);
        c.Capture(scene, size, size, rect.Offset(1, 0), regions, 1, 0xff202125);Assert.Equal(2, c.BlurRebuilds);
    }
    [Fact]
    public void ExtendedBackgroundRegionsRequireResizeOptInAndStayBounded()
    {
        var regions=new[]{new ProcessMaterialRegion(0,0,600,480,0,1,2)};
        Assert.False(ProcessMaterialRegion.Validate(regions,600,480));
        Assert.True(ProcessMaterialRegion.Validate(regions,600,480,true));
        Assert.False(ProcessMaterialRegion.Validate(new[]{regions[0] with {Role=2}},600,480,true));
        Assert.False(ProcessMaterialRegion.Validate(new[]{new ProcessMaterialRegion(0,0,1920,1001,0,1,2)},1920,1001,true));
        using var surface=new ProcessSurface(600,480,"background",2);var pixels=new byte[600*480*4];
        Assert.Equal(-22,surface.PresentMaterials(pixels,regions));Assert.Equal(0,surface.EnableResize());
        Assert.Equal(0,surface.PresentMaterials(pixels,regions));var copy=new int[600*480];
        Assert.True(surface.CopyFrame(copy,false));Assert.All(copy,value=>Assert.Equal(0,value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void RepaintFromTheCacheMatchesLivePaintExactly(int scale)
    {
        const int width = 200, height = 120;
        var scene = new int[width * height];
        for (int i = 0; i < scene.Length; i++) { scene[i] = unchecked((int)0xff000000) | (i * 2654435 & 0xffffff); }
        var content = new Rect(10, 8, 160, 100);
        ProcessMaterialRegion[] regions = { new(0, 0, 160, 60, 12, 4, 2, 200), new(0, 60, 80, 40, 0, 1, 1, 180) };
        int samples = MaterialComposition.SampleLength(160, scale) * MaterialComposition.SampleLength(60, scale)
            + MaterialComposition.SampleLength(80, scale) * MaterialComposition.SampleLength(40, scale);
        var composition = new MaterialComposition(samples, true, scale);
        var live = new int[scene.Length]; var cached = new int[scene.Length];
        System.Array.Fill(live, 0x123456); System.Array.Fill(cached, 0x123456);

        composition.Capture(scene, width, height, content, regions, 2, 0xff202020);
        composition.Paint(live, width, height, content, regions, 2, 0xff202020, 0xff304050, 0xff304050, 0xff304050, false);
        Assert.False(composition.HasPaintedCache);

        // The backdrop repeats, whether found by scanning or promised by the caller.
        composition.Capture(scene, width, height, content, regions, 2, 0xff202020);
        composition.Paint(cached, width, height, content, regions, 2, 0xff202020, 0xff304050, 0xff304050, 0xff304050, false);
        Assert.True(composition.HasPaintedCache);
        Assert.Equal(live, cached);
        composition.Capture(scene, width, height, content, regions, 2, 0xff202020, backdropUnchanged: true);
        System.Array.Fill(cached, 0x123456);
        composition.Paint(cached, width, height, content, regions, 2, 0xff202020, 0xff304050, 0xff304050, 0xff304050, false);
        Assert.Equal(live, cached);

        // A new backdrop drops the cache and paints the new pixels.
        scene[(20 * width) + 30] ^= 0xffffff;
        composition.Capture(scene, width, height, content, regions, 2, 0xff202020);
        Assert.False(composition.HasPaintedCache);
        System.Array.Fill(cached, 0x123456);
        composition.Paint(cached, width, height, content, regions, 2, 0xff202020, 0xff304050, 0xff304050, 0xff304050, false);
        Assert.NotEqual(live, cached);
        composition.ReleasePainted();
    }

    [Fact]
    public void PaintCacheFallsBackWhenTheBudgetIsSpent()
    {
        int spare = MaterialResources.MaximumPaintedBytes - MaterialResources.PaintedBytes;
        Assert.True(MaterialResources.ReservePainted(spare));
        try
        {
            var scene = new int[64 * 64]; var target = new int[scene.Length];
            var content = new Rect(0, 0, 64, 64);
            ProcessMaterialRegion[] regions = { new(0, 0, 64, 64, 0, 4, 2, 200) };
            var composition = new MaterialComposition(64 * 64);
            composition.Capture(scene, 64, 64, content, regions, 1, 0xff202020);
            composition.Capture(scene, 64, 64, content, regions, 1, 0xff202020);
            composition.Paint(target, 64, 64, content, regions, 1, 0xff202020, 0xff304050, 0xff304050, 0xff304050, false);
            Assert.False(composition.HasPaintedCache);
            Assert.NotEqual(0, target[0]);
        }
        finally { MaterialResources.ReleasePainted(spare); }
    }

    [Theory]
    [InlineData(255)]
    [InlineData(210)]
    [InlineData(1)]
    public void Sse2AndScalarRowsAreIdentical(int opacity)
    {
        var random = new Random(opacity);
        for (int count = 1; count < 40; count++)
        {
            var a = new int[count + 1]; var b = new int[count + 1];
            for (int i = 0; i <= count; i++) { a[i] = random.Next(int.MinValue, int.MaxValue); b[i] = random.Next(int.MinValue, int.MaxValue); }
            foreach (int width in new[] { count * 4, count * 4 - 3 })
            {
                int[] Run(bool sse)
                {
                    bool saved = MaterialRows.UseSse2;
                    MaterialRows.UseSse2 = sse;
                    try
                    {
                        var coarse = new int[count]; var row = new int[Math.Max(1, width)];
                        var results = new System.Collections.Generic.List<int>();
                        for (int weight = 0; weight < 4; weight++)
                        {
                            MaterialRows.Mix(a, 1, b, 0, coarse, count, weight); results.AddRange(coarse);
                        }
                        MaterialRows.Tint(coarse, 0, coarse, 0, count, 0xff305070, opacity); results.AddRange(coarse);
                        MaterialRows.Expand(coarse, count, row, 0, width); results.AddRange(row);
                        return results.ToArray();
                    }
                    finally { MaterialRows.UseSse2 = saved; }
                }
                Assert.Equal(Run(false), Run(true));
            }
        }
    }

    [Fact]
    public void TintStaysWithinTwoStepsOfTheExactBlend()
    {
        var random = new Random(7);
        var output = new int[1];
        for (int i = 0; i < 20000; i++)
        {
            int[] source = { random.Next(int.MinValue, int.MaxValue) | unchecked((int)0xff000000) };
            uint tint = (uint)random.Next(int.MinValue, int.MaxValue) | 0xff000000U;
            int opacity = random.Next(256);
            MaterialRows.Tint(source, 0, output, 0, 1, tint, opacity);
            int exact = RoundedComposition.Blend((int)tint, source[0], opacity);
            for (int shift = 0; shift < 24; shift += 8)
            { Assert.InRange(((output[0] >> shift) & 255) - ((exact >> shift) & 255), -2, 2); }
        }
    }

    [Fact]
    public void TintEndsMeetTheSourceAndTheTint()
    {
        int[] pixel = { unchecked((int)0xff102030) }; var output = new int[1];
        MaterialRows.Tint(pixel, 0, output, 0, 1, 0xffa0b0c0, 255);
        Assert.Equal(unchecked((int)0xffa0b0c0), output[0]);
        MaterialRows.Tint(pixel, 0, output, 0, 1, 0xffa0b0c0, 0);
        Assert.Equal(unchecked((int)0xff102030), output[0]);
    }
}
