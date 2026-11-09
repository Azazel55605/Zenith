using System;
using Cosmos.Kernel.System.Graphics;
using Zenith.Core.Configuration;
namespace Zenith.Gui.Shell;

/// <summary>A logical screen over the device: 200% doubles each logical pixel; 125/150/175% are
/// presented filtered by <see cref="FramePresenter"/>. Hardware input stays physical. The shell
/// still draws logical frames: sharp fractional scaling needs it to draw at device resolution.</summary>
internal sealed class DisplayCanvas : Canvas
{
    private readonly Canvas _physical;
    private readonly int _scale;
    private readonly int[] _pixels;
    public DisplayCanvas(Canvas physical, DisplayLayout layout) : base(new Mode(layout.LogicalWidth,layout.LogicalHeight,ColorDepth.ColorDepth32))
    { _physical=physical; _scale=layout.Integer?layout.Scale:1; Percent=layout.Percent; _pixels=layout.Integer?new int[layout.Width*layout.Height]:Array.Empty<int>(); }
    public Canvas Physical => _physical;
    /// <summary>The integer scale of the legacy canvas methods (1 under a fractional scale).</summary>
    public int Scale => _scale;
    public int Percent { get; }
    public override string Name => _physical.Name;
    public override int RefreshRate => _physical.RefreshRate;
    public override void DrawPoint(int color,int x,int y)
    {
        if((uint)x>=(uint)Width || (uint)y>=(uint)Height || _pixels.Length==0) { return; }
        for(int dy=0;dy<_scale;dy++) { int at=(y*_scale+dy)*(Width*_scale)+x*_scale;
            for(int dx=0;dx<_scale;dx++) { _pixels[at+dx]=color; } }
    }
    public override void Clear(int color) => Array.Fill(_pixels,color);
    public override void DrawArray(int[] pixels,int x,int y,int width,int height) => DrawArray(pixels,x,y,width,height,0);
    public override void DrawArray(int[] pixels,int x,int y,int width,int height,int startIndex)
    {
        if(x==0 && y==0 && width==Width && height==Height && _scale==1) { Array.Copy(pixels,startIndex,_pixels,0,_pixels.Length); return; }
        for(int row=0;row<height;row++) { for(int col=0;col<width;col++) { DrawPoint(pixels[startIndex+row*width+col],x+col,y+row); } }
    }
    public override void Display() { if(_pixels.Length!=0) { _physical.DrawArray(_pixels,0,0,Width*_scale,Height*_scale); } _physical.Display(); }
}
