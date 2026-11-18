using System;
using System.IO;
using Zenith.Core.Files;

namespace Zenith.Tests;

/// <summary>Horizon HZ4 thumbnails (call 660): images with four solid quadrants (red, green / blue, white), made
/// by Pillow and libjpeg (tests/Zenith.Tests/Fixtures/images), must shrink to the same four colours.</summary>
public class ThumbnailTests
{
    private static string Fixture(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "tests/Zenith.Tests/Fixtures/images", name);
            if (File.Exists(path)) { return path; }
        }
        throw new FileNotFoundException(name);
    }
    private static Thumbnails.Result Make(string name, int max = 64)
    {
        using var s = File.OpenRead(Fixture(name));
        var r = Thumbnails.Make(s, max, out string why);
        Assert.True(r is not null, name + ": " + why);
        return r!;
    }
    private static void Near(uint pixel, int r, int g, int b, string what)
    {
        int pr = (int)(pixel >> 16 & 255), pg = (int)(pixel >> 8 & 255), pb = (int)(pixel & 255);
        Assert.True(Math.Abs(pr - r) < 45 && Math.Abs(pg - g) < 45 && Math.Abs(pb - b) < 45, $"{what}: got {pr},{pg},{pb} want {r},{g},{b}");
        Assert.Equal(255u, pixel >> 24);
    }
    private static void Quadrants(Thumbnails.Result t, string name, bool grey = false)
    {
        uint At(int fx, int fy) => t.Pixels[(t.Height * fy / 4) * t.Width + t.Width * fx / 4];
        if (grey)
        {
            int Y(uint p) => (int)(p >> 8 & 255);
            Assert.True(Y(At(3, 3)) > Y(At(1, 1)) && Y(At(1, 1)) > Y(At(1, 3)), name + " grey levels");
            return;
        }
        Near(At(1, 1), 230, 30, 30, name + " red"); Near(At(3, 1), 30, 200, 40, name + " green");
        Near(At(1, 3), 40, 60, 220, name + " blue"); Near(At(3, 3), 250, 250, 250, name + " white");
    }

    [Theory]
    [InlineData("quads-420.jpg", 640, 480)] [InlineData("quads-444.jpg", 96, 64)] [InlineData("restart.jpg", 640, 480)]
    [InlineData("quads-large.jpg", 3000, 2000)] [InlineData("palette.png", 300, 200)] [InlineData("rgba.png", 120, 80)] [InlineData("quads.bmp", 77, 33)]
    public void ColourImagesShrinkToTheirQuadrants(string name, int width, int height)
    {
        var t = Make(name);
        Assert.Equal(Math.Max(width, height) <= 64 ? width : (int)(width * Math.Min(1.0, 64.0 / Math.Max(width, height))), t.Width);
        Assert.Equal(t.Width * t.Height, t.Pixels.Length);
        Assert.True(Math.Abs((double)t.Width / t.Height - (double)width / height) < 0.15, "aspect kept");
        Quadrants(t, name);
    }

    [Theory]
    [InlineData("rgba-interlaced.png", "rgba.png")] [InlineData("rgba16-interlaced.png", "rgba.png")] [InlineData("palette-interlaced.png", "palette.png")]
    public void InterlacedPngMatchesTheSameImageWithoutInterlacing(string interlaced, string plain)
    {
        var a = Make(interlaced); var b = Make(plain);
        Assert.Equal(b.Width, a.Width); Assert.Equal(b.Height, a.Height);
        Quadrants(a, interlaced);
        for (int i = 0; i < a.Pixels.Length; i++)
        {
            for (int shift = 0; shift < 32; shift += 8)
            { Assert.True(Math.Abs((int)(a.Pixels[i] >> shift & 255) - (int)(b.Pixels[i] >> shift & 255)) <= 12, interlaced + " pixel " + i); }
        }
        // 7x5: some of the seven passes are empty.
        var tiny = Make("tiny-interlaced.png");
        Assert.Equal(7, tiny.Width); Assert.Equal(5, tiny.Height);
        foreach (uint p in tiny.Pixels) { Assert.Equal(255u, p >> 24); }
    }

    /// <summary>Progressive JPEG against the same image losslessly converted to baseline (jpegtran keeps every DCT
    /// coefficient): the thumbnails must be identical, at icon size and at full size.</summary>
    [Theory]
    [InlineData("progressive")] [InlineData("prog-420")] [InlineData("prog-444-restart")] [InlineData("prog-grey")] [InlineData("prog-large")]
    public void ProgressiveJpegDecodesLikeItsBaselineTwin(string name)
    {
        foreach (int max in new[] { 64, 256 })
        {
            var progressive = Make(name + ".jpg", max); var baseline = Make(name + "-baseline.jpg", max);
            Assert.Equal(baseline.Width, progressive.Width); Assert.Equal(baseline.Height, progressive.Height);
            Assert.Equal(baseline.Pixels, progressive.Pixels);
        }
        Quadrants(Make(name + ".jpg"), name, grey: name == "prog-grey");
    }

    [Fact]
    public void GreyJpegAndUnsupportedImages()
    {
        Quadrants(Make("grey.jpg"), "grey.jpg", grey: true);
        using var s = File.OpenRead(Fixture("arithmetic.jpg"));
        Assert.Null(Thumbnails.Make(s, 64, out string why)); Assert.Contains("arithmetic", why);
        Assert.Null(Thumbnails.Make(new MemoryStream(new byte[] { 1, 2, 3 }), 64, out why)); Assert.Contains("not a PNG", why);
        Assert.Null(Thumbnails.Make(new MemoryStream(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0 }), 64, out why));
    }
}
