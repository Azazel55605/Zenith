using System;
using Zenith.Core.Processes;

namespace Zenith.Gui.Graphics;

/// <summary>Host chrome/shell material using exactly the client material compositor.
/// One cache per owner, bounded allocation; oversized/pressure surfaces stay solid.</summary>
internal sealed class HostMaterial : IDisposable
{
    private const int Capacity = ProcessMaterialRegion.MaximumArea;
    private readonly MaterialCache _cache = new(Capacity);
    private readonly ProcessMaterialRegion[] _region = new ProcessMaterialRegion[1];
    private int _effect;
    public bool Active => _effect != 0;
    public void Capture(Surface surface, Rect bounds, int radius, uint tint, bool allowed = true)
    {
        _region[0] = new(0, 0, bounds.W, bounds.H, radius, 4, 0);
        _effect = allowed ? MaterialResources.Policy.Effect(2, tint, Theme.TextPrimary, Theme.TextSecondary, bounds.W * bounds.H) : 0;
        if (_effect == 0) { return; }
        if ((long)bounds.W * bounds.H > Capacity || !_cache.Ensure(bounds.W * bounds.H, _effect == 2))
        { MaterialResources.Policy.ResourceFallback();_effect = 0;return; }
        _region[0] = new(0, 0, bounds.W, bounds.H, radius, 4, _effect, MaterialPolicy.Opacity(tint, Theme.TextPrimary, Theme.TextSecondary, _effect));
        _cache.Composition.Capture(surface.Pixels, surface.Width, surface.Height, bounds, _region, 1, tint);
    }
    public void Paint(Surface surface, Rect bounds, uint tint)
    {
        _cache.Composition.Paint(surface.Pixels, surface.Width, surface.Height, bounds, _region, 1, tint, tint, tint, tint, false);
    }
    public void Dispose() => _cache.Dispose();
}
