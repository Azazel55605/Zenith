using System;
using Cosmos.Kernel.System.Graphics;
using Zenith.Core.Configuration;
namespace Zenith.Gui.Shell;

/// <summary>Integer logical pixels, including chrome, text and cursor. Hardware input
/// stays physical. This is deliberately not fractional DPI or a new font rasterizer.</summary>
internal sealed class DisplayCanvas : Canvas
{
    private readonly Canvas _physical;
    private readonly int _scale;
    private readonly int[] _pixels;
    public DisplayCanvas(Canvas physical, DisplayLayout layout) : base(new Mode(layout.LogicalWidth,layout.LogicalHeight,ColorDepth.ColorDepth32))
    { _physical=physical; _scale=layout.Scale; _pixels=new int[layout.Width*layout.Height]; }
    public Canvas Physical => _physical;
    public int Scale => _scale;
    public override string Name => _physical.Name;
    public override int RefreshRate => _physical.RefreshRate;
    public override void DrawPoint(int color,int x,int y)
    {
        if((uint)x>=(uint)Width || (uint)y>=(uint)Height) { return; }
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
    public override void Display() { _physical.DrawArray(_pixels,0,0,Width*_scale,Height*_scale); _physical.Display(); }
}
