using System;
using Zenith.Gui.Graphics;

namespace Zenith.Tests;

public class PointerImageTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(150)]
    [InlineData(175)]
    [InlineData(200)]
    public void ArrowIsPremultipliedBorderedAndScaled(int percent)
    {
        uint[] image = PointerImage.Render(percent, out int w, out int h);
        Assert.Equal(w * h, image.Length);
        Assert.InRange(w, 14 * percent / 100, 64);
        Assert.InRange(h, 21 * percent / 100, 64);
        int white = 0, dark = 0;
        foreach (uint p in image)
        {
            uint a = p >> 24, r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
            Assert.True(r <= a && g <= a && b <= a, "premultiplied");
            if (a == 255 && r == 255 && g == 255 && b == 255) { white++; }
            if (a == 255 && r < 0x30 && g < 0x30 && b < 0x40) { dark++; }
        }
        // A white body and a dark border at every scale; both grow with the scale.
        Assert.True(white >= 35 * percent * percent / 10000, "white " + white);
        Assert.True(dark >= 18 * percent / 100, "dark " + dark);
        // The hotspot is the tip: nothing left of or above the outline's first column/row is opaque.
        Assert.True(image[0] >> 24 > 0, "tip");
        Assert.Equal(0u, image[w - 1] >> 24);
        Assert.Equal(0u, image[(h - 1) * w] >> 24 & 0x80);
    }
}
