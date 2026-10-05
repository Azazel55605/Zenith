using Cosmos.Kernel.System.Keyboard;
using Zenith.Gui.Graphics;

namespace Zenith.Gui;

/// <summary>
/// Base class for every application window. The window manager owns position, size, focus and
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
    public Rect Bounds { get; private set; }
    public bool IsFocused { get; internal set; }
    public bool IsMinimized { get; internal set; }
    public bool IsMaximized { get; internal set; }
    public bool IsClosed { get; private set; }

    /// <summary>Where a maximized window goes back to.</summary>
    internal Rect RestoreBounds { get; set; }

    /// <summary>Whether the user may resize and maximize the window. Fixed-layout windows say no.</summary>
    public virtual bool CanResize => true;

    /// <summary>The smallest size the user can resize the window to.</summary>
    public virtual (int Width, int Height) MinimumSize => (300, 180);

    public Rect TitleBar => new(Bounds.X, Bounds.Y, Bounds.W, TitleBarHeight);
    public Rect Content => new(Bounds.X, Bounds.Y + TitleBarHeight, Bounds.W, Bounds.H - TitleBarHeight);
    public Rect CloseButton => new(Bounds.Right - ButtonSize - 6, Bounds.Y + (TitleBarHeight - ButtonSize) / 2, ButtonSize, ButtonSize);
    public Rect MaximizeButton => CanResize ? CloseButton.Offset(-ButtonSize - 2, 0) : new Rect(0, 0, 0, 0);
    public Rect MinimizeButton => CloseButton.Offset(-(ButtonSize + 2) * (CanResize ? 2 : 1), 0);

    public void Close() => IsClosed = true;

    /// <summary>Moves or resizes the window; <see cref="OnResized"/> runs when the size changed.</summary>
    internal void SetBounds(Rect bounds)
    {
        bool resized = bounds.W != Bounds.W || bounds.H != Bounds.H;
        Bounds = bounds;
        if (resized)
        {
            OnResized();
        }
    }

    /// <summary>Called every frame. Return true when the content changed and needs a redraw (clocks, caret blink).</summary>
    public virtual bool Update() => false;

    /// <summary>Paints the content area. The surface is already clipped to <paramref name="content"/>.</summary>
    public abstract void DrawContent(Surface surface, Rect content);

    /// <summary>The window changed size (resize or maximize); recompute layout here.</summary>
    protected virtual void OnResized()
    {
    }

    /// <summary>A left click inside the content area, in content-relative coordinates.</summary>
    public virtual void OnMouseDown(int x, int y)
    {
    }

    /// <summary>The pointer moved with the button held after a click in the content area (content-relative, may be outside it).</summary>
    public virtual void OnMouseDrag(int x, int y)
    {
    }

    /// <summary>The button was released after a click in the content area.</summary>
    public virtual void OnMouseUp(int x, int y)
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
