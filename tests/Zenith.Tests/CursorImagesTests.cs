using System;
using Zenith.Gui.Graphics;

namespace Zenith.Tests;

public class CursorImagesTests
{
    [Fact]
    public void EveryShapeVariantAndSizeDecodesPremultiplied()
    {
        Assert.Equal(12, CursorImages.Names.Length);
        Assert.Equal(Enum.GetValues<CursorShape>().Length, CursorImages.Names.Length);
        foreach (int size in CursorImages.Sizes)
        for (int variant = 0; variant < CursorImages.Variants; variant++)
        foreach (var shape in Enum.GetValues<CursorShape>())
        {
            uint[] image = CursorImages.Get(variant, shape, size, out int hotX, out int hotY);
            Assert.Equal(size * size, image.Length);
            Assert.InRange(hotX, 0, size - 1); Assert.InRange(hotY, 0, size - 1);
            int opaque = 0;
            foreach (uint p in image)
            {
                uint a = p >> 24;
                Assert.True(((p >> 16) & 255) <= a && ((p >> 8) & 255) <= a && (p & 255) <= a, "premultiplied");
                if (a > 200) { opaque++; }
            }
            Assert.True(opaque > size * size / 20, shape + " at " + size + " has ink");
        }
    }

    [Fact]
    public void LightHasAWhiteBodyAndDarkADarkOneWithTheHotspotAtTheTip()
    {
        // Inside the arrow, a little below and right of the tip.
        uint light = CursorImages.Get(0, CursorShape.Default, 24, out int hx, out int hy)[(hy + 8) * 24 + hx + 2];
        uint dark = CursorImages.Get(1, CursorShape.Default, 24, out _, out _)[(hy + 8) * 24 + hx + 2];
        Assert.Equal((4, 3), (hx, hy));
        Assert.True((light & 0xFF) > 0xE0 && light >> 24 == 255, "light body " + light.ToString("x8"));
        Assert.True((dark & 0xFF) < 0x40 && dark >> 24 == 255, "dark body " + dark.ToString("x8"));
        // Hotspots scale with the size.
        CursorImages.Get(0, CursorShape.Text, 48, out int tx, out int ty);
        Assert.Equal((24, 24), (tx, ty));
        Assert.Equal(24, CursorImages.SizeFor(20)); Assert.Equal(32, CursorImages.SizeFor(30)); Assert.Equal(48, CursorImages.SizeFor(48)); Assert.Equal(48, CursorImages.SizeFor(72));
        Assert.Throws<ArgumentOutOfRangeException>(() => CursorImages.Get(0, CursorShape.Default, 40, out _, out _));
    }
}
