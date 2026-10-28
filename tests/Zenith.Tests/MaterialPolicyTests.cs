using System.Collections.Generic;
using Zenith.Gui.Graphics;
namespace Zenith.Tests;
public class MaterialPolicyTests
{
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
