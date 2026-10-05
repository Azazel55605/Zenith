using Cosmos.Kernel.System.Keyboard;
using Zenith.Gui.Graphics;

namespace Zenith.Gui;

/// <summary>
/// Base class for every application window. The window manager owns position, focus and
/// chrome; subclasses only draw their content area and react to input inside it.
/// </summary>
internal abstract class Window
{
    public const int TitleBarHeight = 38;
    public const int ButtonSize = 28;

    protected Window(string title, int width, int height)
    {
        Title = title;
        Bounds = new Rect(0, 0, width, height);
    }

    public string Title { get; protected set; }
    public Rect Bounds { get; internal set; }
    public bool IsFocused { get; internal set; }
    public bool IsMinimized { get; internal set; }
    public bool IsClosed { get; private set; }

    public Rect TitleBar => new(Bounds.X, Bounds.Y, Bounds.W, TitleBarHeight);
    public Rect Content => new(Bounds.X, Bounds.Y + TitleBarHeight, Bounds.W, Bounds.H - TitleBarHeight);
    public Rect CloseButton => new(Bounds.Right - ButtonSize - 6, Bounds.Y + (TitleBarHeight - ButtonSize) / 2, ButtonSize, ButtonSize);
    public Rect MinimizeButton => CloseButton.Offset(-ButtonSize - 2, 0);

    public void Close() => IsClosed = true;

    /// <summary>Called every frame. Return true when the content changed and needs a redraw (clocks, caret blink).</summary>
    public virtual bool Update() => false;

    /// <summary>Paints the content area. The surface is already clipped to <paramref name="content"/>.</summary>
    public abstract void DrawContent(Surface surface, Rect content);

    /// <summary>A left click inside the content area, in content-relative coordinates.</summary>
    public virtual void OnMouseDown(int x, int y)
    {
    }

    /// <summary>Mouse wheel over the window; positive scrolls back (up).</summary>
    public virtual void OnScroll(int delta)
    {
    }

    /// <summary>A key press while this window has focus.</summary>
    public virtual void OnKey(KeyEvent key)
    {
    }
}
