using System.Collections.Generic;
using Cosmos.Kernel.System.Keyboard;
using Cosmos.Kernel.System.Mouse;

namespace Zenith.Gui;

/// <summary>
/// One frame's worth of input: the pointer state, button edges since the previous frame,
/// and every key pressed since then. Read once per frame so all consumers see the same state.
/// </summary>
internal sealed class Input
{
    private bool _previousLeft;
    private bool _previousRight;

    public int X { get; private set; }
    public int Y { get; private set; }
    public bool Left { get; private set; }
    public bool LeftPressed { get; private set; }
    public bool LeftReleased { get; private set; }
    public bool RightPressed { get; private set; }
    public int Scroll { get; private set; }
    public bool Moved { get; private set; }
    public List<KeyEvent> Keys { get; } = new();

    /// <summary>Whether anything happened this frame that could change what is on screen.</summary>
    public bool HasActivity => Moved || LeftPressed || LeftReleased || RightPressed || Scroll != 0 || Keys.Count > 0;

    /// <summary>The pointer in device pixels, as last polled (the hardware pointer is placed there).</summary>
    public static int PhysicalX { get; private set; }
    public static int PhysicalY { get; private set; }

    public void Poll()
    {
        Zenith.Core.Input.MousePolling.Drain();
        int px = MouseManager.X, py = MouseManager.Y;
        bool absolute = Zenith.Core.Input.AbsolutePointer.Active;
        if (absolute && Zenith.Core.Input.AbsolutePointer.Poll(MouseManager.ScreenWidth, MouseManager.ScreenHeight, ref px, ref py))
        {
            // Relative input (if the device ever falls back) continues from here.
            MouseManager.SetPosition(px, py);
        }
        PhysicalX = px; PhysicalY = py;
        int x = Zenith.Gui.Shell.SessionDisplay.ToLogical(px), y = Zenith.Gui.Shell.SessionDisplay.ToLogical(py);
        Moved = x != X || y != Y;
        X = x;
        Y = y;

        Left = absolute ? Zenith.Core.Input.AbsolutePointer.Left : MouseManager.LeftButton;
        bool right = absolute ? Zenith.Core.Input.AbsolutePointer.Right : MouseManager.RightButton;
        LeftPressed = Left && !_previousLeft;
        LeftReleased = !Left && _previousLeft;
        RightPressed = right && !_previousRight;
        _previousLeft = Left;
        _previousRight = right;

        Scroll = MouseManager.ScrollDelta + Zenith.Core.Input.AbsolutePointer.TakeWheel();
        if (MouseManager.ScrollDelta != 0)
        {
            MouseManager.ResetScrollDelta();
        }

        Keys.Clear();
        while (KeyboardManager.TryReadKey(out KeyEvent? key))
        {
            Keys.Add(key);
        }
    }
}
