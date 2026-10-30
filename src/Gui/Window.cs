using Cosmos.Kernel.System.Keyboard;
using Zenith.Core.Security;
using Zenith.Gui.Graphics;

namespace Zenith.Gui;

/// <summary>
/// Base class for every application window. The window manager owns position, size, focus and
/// chrome; subclasses only draw their content area and react to input inside it.
/// </summary>
internal abstract class Window
{
    public const int TitleBarHeight = 44;
    public const int ButtonSize = 32;

    protected Window(string title, int width, int height)
    {
        Title = title;
        Bounds = new Rect(0, 0, width, height);
    }

    public string Title { get; protected set; }
    public Credentials Identity { get; protected set; } = CredentialContext.Current;
    public Rect Bounds { get; private set; }
    private bool _isFocused;
    public bool IsFocused
    {
        get => _isFocused;
        internal set { if (_isFocused != value) { _isFocused = value; OnFocusChanged(value); } }
    }
    protected virtual void OnFocusChanged(bool focused) { }

    public bool IsMinimized { get; internal set; }
    public bool IsMaximized { get; internal set; }
    public bool IsClosed { get; private set; }

    /// <summary>Where a maximized window goes back to.</summary>
    internal Rect RestoreBounds { get; set; }

    /// <summary>Whether the user may resize and maximize the window. Fixed-layout windows say no.</summary>
    public virtual bool CanResize => true;
    public virtual (int Width, int Height) MaximumSize => (int.MaxValue, int.MaxValue);
    public virtual bool AllowMaterials => true;
    public virtual bool AllowBackgroundMaterials => false;
    public virtual bool UnifiedChrome => AllowBackgroundMaterials;
    private readonly HostMaterial _contentMaterial = new(1920000);
    internal HostMaterial ChromeMaterial { get; } = new();

    /// <summary>The smallest size the user can resize the window to.</summary>
    public virtual (int Width, int Height) MinimumSize => (300, 180);

    public Rect TitleBar => new(Bounds.X, Bounds.Y, Bounds.W, TitleBarHeight);
    public Rect Content => new(Bounds.X, Bounds.Y + TitleBarHeight, Bounds.W, Bounds.H - TitleBarHeight);
    public Rect CloseButton => new(Bounds.Right - ButtonSize - 6, Bounds.Y + (TitleBarHeight - ButtonSize) / 2, ButtonSize, ButtonSize);
    public Rect MaximizeButton => CanResize ? CloseButton.Offset(-ButtonSize - 4, 0) : new Rect(0, 0, 0, 0);
    public Rect MinimizeButton => CloseButton.Offset(-(ButtonSize + 4) * (CanResize ? 2 : 1), 0);

    public virtual bool HasUnsavedChanges => false;
    public virtual bool CanClose => true;
    public virtual void Close() { if (CanClose) { ChromeMaterial.Dispose(); _contentMaterial.Dispose(); IsClosed = true; } }
    public virtual void EndSession() => Close();

    /// <summary>Moves or resizes the window; <see cref="OnResized"/> runs when the size changed.</summary>
    internal void SetBounds(Rect bounds)
    {
        using var identity = CredentialContext.Enter(Identity);
        bool resized = bounds.W != Bounds.W || bounds.H != Bounds.H;
        Bounds = bounds;
        if (resized)
        {
            OnResized();
        }
    }

    /// <summary>Called every frame. Return true when the content changed and needs a redraw (clocks, caret blink).</summary>
    public virtual bool Update() => false;
    public virtual bool UpdateWhenMinimized => false;

    /// <summary>Paints the content area. The surface is already clipped to <paramref name="content"/>.</summary>
    public virtual void CaptureBackdrop(Surface surface, Rect content)
        => _contentMaterial.Capture(surface, content, 0, Theme.Panel, AllowBackgroundMaterials && AllowMaterials && (MaterialResources.Policy.Effective & 4) != 0);

    protected void PaintBackground(Surface surface, Rect content)
    {
        if (_contentMaterial.Active && AllowMaterials) { _contentMaterial.Paint(surface, content, Theme.Panel); }
        else { surface.FillRect(content, Theme.WindowBackground); }
    }

    /// <summary>Meridian's layout: a sidebar on the base surface beside a rounded detail inset.
    /// Material windows keep their continuous background. Returns the detail rectangle.</summary>
    protected Rect PaintWorkspace(Surface surface, Rect content, int sidebar, int margin = 12)
    {
        var detail = new Rect(content.X + (sidebar > 0 ? sidebar + 2 * margin : margin), content.Y + margin,
            content.W - (sidebar > 0 ? sidebar + 3 * margin : 2 * margin), content.H - 2 * margin);
        if (_contentMaterial.Active && AllowMaterials) { _contentMaterial.Paint(surface, content, Theme.Panel); }
        else
        {
            surface.FillRect(content, Theme.Panel);
            surface.FillRoundRect(detail, Theme.WindowRadius, Theme.WindowBackground | 0xff000000U);
        }
        return detail;
    }

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
    public virtual void OnScrollAt(int delta, int x, int y) => OnScroll(delta);

    /// <summary>Pointer moved over the content with no button held (content-local).</summary>
    public virtual void OnPointerMove(int x, int y) { }
    public virtual void OnPointerLeave() { }

    public virtual void OnScroll(int delta)
    {
    }

    /// <summary>A key press while this window has focus.</summary>
    public virtual void OnKey(KeyEvent key)
    {
    }
}
