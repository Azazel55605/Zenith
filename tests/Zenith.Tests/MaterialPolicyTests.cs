using System.Collections.Generic;
using Zenith.Gui.Graphics;
namespace Zenith.Tests;
public class MaterialPolicyTests
{
    [Fact]
    public void TranslucencyStrengthIsMonotonicAndContrastBounded()
    {
        int solid=MaterialPolicy.Opacity(0xff25262b,0xfff1f2f4,0xffb4b7c0,2,0);
        int low=MaterialPolicy.Opacity(0xff25262b,0xfff1f2f4,0xffb4b7c0,2,20);
        int high=MaterialPolicy.Opacity(0xff25262b,0xfff1f2f4,0xffb4b7c0,2,100);
        Assert.Equal(255,solid);Assert.True(solid>low&&low>high);Assert.InRange(high,145,254);
        var policy=new MaterialPolicy();policy.Begin(7,0);Assert.Equal(0,policy.Effect(2,0xff25262b,0xfff1f2f4,0xffb4b7c0,100));
    }
    [Fact]
    public void MeasuredBackdropsAllowMoreTransparencyButNeverLessContrast()
    {
        const uint tint=0xff25262b,text=0xfff1f2f4,secondary=0xffb4b7c0;
        int worst=MaterialPolicy.Opacity(tint,text,secondary,2,100);
        int dark=MaterialPolicy.AdaptedOpacity(worst,tint,text,secondary,2,100,.01,.06);
        int anything=MaterialPolicy.AdaptedOpacity(worst,tint,text,secondary,2,100,0,1);
        Assert.True(dark<worst,"a dark wallpaper may show through more than any backdrop could");
        Assert.Equal(worst,anything);                                      // the full range is the old clamp exactly
        Assert.True(MaterialPolicy.AdaptedOpacity(worst,tint,text,secondary,2,100,.9,1)<=worst);
        Assert.Equal(255,MaterialPolicy.AdaptedOpacity(255,tint,text,secondary,2,100,0,.01));   // solid stays solid
        Assert.Equal(worst,MaterialPolicy.AdaptedOpacity(worst,tint,text,secondary,0,100,0,.01)); // no material, no change
        // Translucency still scales it, and lower settings stay more opaque.
        Assert.True(MaterialPolicy.AdaptedOpacity(MaterialPolicy.Opacity(tint,text,secondary,2,40),tint,text,secondary,2,40,.01,.06)>dark);
        // Contrast holds against the measured extremes: text over the composited darkest and lightest backdrop.
        static double L(int c){double Ch(int s){double x=((c>>s)&255)/255.0;return x<=.04045?x/12.92:System.Math.Pow((x+.055)/1.055,2.4);}return .2126*Ch(16)+.7152*Ch(8)+.0722*Ch(0);}
        foreach(var (low,high) in new[]{(.01,.06),(.2,.3),(.5,.9)})
        {
            int floor=MaterialPolicy.AdaptedOpacity(255-1,tint,text,secondary,2,100,low,high);
            foreach(double b in new[]{low,high})
            {
                int v=(int)System.Math.Round(255*System.Math.Pow(b,1/2.2));int backdrop=unchecked((int)0xff000000)|v<<16|v<<8|v;
                double mixed=L(RoundedComposition.Blend(unchecked((int)tint),backdrop,floor)),ink=L(unchecked((int)secondary));
                Assert.True((System.Math.Max(ink,mixed)+.05)/(System.Math.Min(ink,mixed)+.05)>=4.4,$"contrast at {low}..{high}");
            }
        }
    }
    [Fact]
    public void FullViewportBlurUsesBoundedSampledWorkAndCache()
    {
        var policy = new MaterialPolicy(); policy.Begin(7);
        int samples = MaterialComposition.SampleLength(1920,4)*MaterialComposition.SampleLength(1000,4);
        Assert.Equal(2,policy.Effect(2,0xff25262b,0xfff1f2f4,0xffb4b7c0,samples));
        Assert.Equal(2,policy.Effect(2,0xff25262b,0xfff1f2f4,0xffb4b7c0,samples));
        int before = MaterialResources.UsedBytes;
        using(var cache = new MaterialCache(1920000))
        {
            Assert.True(cache.Ensure(samples,true,4));
            Assert.Equal(before+samples*12,MaterialResources.UsedBytes);
        }
        Assert.Equal(before,MaterialResources.UsedBytes);
    }

    [Fact]
    public void PreferencesFallbackBudgetsAndContrastRemainDistinct()
    {
        var p = new MaterialPolicy();p.Begin(2);Assert.Equal(0U, p.Effective);Assert.Equal(1U, p.Reason);
        p.Begin(3);Assert.Equal(2, p.Effect(2, 0xff25262b, 0xfff1f2f4, 0xffb4b7c0, 131072));
        Assert.Equal(3U, p.Effective);Assert.InRange(MaterialPolicy.Opacity(0xff25262b, 0xfff1f2f4, 0xffb4b7c0, 2), 210, 244);
        p.Effect(2, 0xff25262b, 0xfff1f2f4, 0xffb4b7c0, 131072);p.Effect(2, 0xff25262b, 0xfff1f2f4, 0xffb4b7c0, 131072);
        Assert.Equal(1, p.Effect(2, 0xff25262b, 0xfff1f2f4, 0xffb4b7c0, 1));Assert.Equal(4U, p.Reason);Assert.Equal(3U, p.Requested);
        p.Begin(3);Assert.Equal(0, p.Effect(2, 0xff777777, 0xff777777, 0xff777777, 100));Assert.Equal(5U, p.Reason);
        p.Begin(0);Assert.Equal(0, p.Effect(2, 0xff25262b, 0xffffffff, 0xffffffff, 100));
    }
    [Fact]
    public void CacheMemoryIsBoundedReleasedAndCannotBeResurrected()
    {
        int original = MaterialResources.UsedBytes;var caches = new List<MaterialCache>();
        try
        {
            for (int i = 0; i < 5; i++) { var c = new MaterialCache(131072);caches.Add(c);Assert.True(c.Ensure()); }
            using var denied = new MaterialCache(131072);Assert.False(denied.Ensure());Assert.True(MaterialResources.UsedBytes <= MaterialResources.MaximumBytes);
            caches[0].Dispose();Assert.False(caches[0].Ensure());Assert.True(denied.Ensure());
        }
        finally { foreach (var c in caches) { c.Dispose(); } }
        Assert.Equal(original, MaterialResources.UsedBytes);
    }
    [Fact]
    public void LargeTintCacheFitsWithoutThreeFullSizeBlurBuffers()
    {
        int before=MaterialResources.UsedBytes;using(var cache=new MaterialCache(1920000))
        {
            Assert.False(cache.Ensure(1280*692,true));Assert.True(cache.Ensure(1280*692,false));
            Assert.Equal(before+1280*692*4,MaterialResources.UsedBytes);
        }
        Assert.Equal(before,MaterialResources.UsedBytes);var policy=new MaterialPolicy();policy.Begin(7);Assert.Equal(7U,policy.Effective);
        policy.Begin(6);Assert.Equal(0U,policy.Effective);Assert.Equal(1U,policy.Reason);
    }
}
