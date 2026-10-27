using System;
using System.Collections.Generic;
using Cosmos.Kernel.System.Keyboard;
using Zenith.Core.Processes;
using Zenith.Core.Security;
using Zenith.Gui.Graphics;

namespace Zenith.Gui;

// Process workers only touch owned buffers/queues. WindowManager is mutated
// exclusively by the desktop tick. A host is permanently bound to one login.
internal sealed class NativeClientHost : IProcessGraphicsHost
{
    private readonly object _gate = new();
    private readonly List<ProcessSurface> _pending = new();
    private readonly List<ProcessSurface> _surfaces = new();
    private readonly Credentials _identity;
    private readonly int _generation;
    private bool _stopped;
    public NativeClientHost(Credentials identity) { _identity = identity; _generation = Accounts.Generation; }
    public IProcessGraphicsHost? ForUser(uint uid) => uid == _identity.Uid ? this : null;
    public ProcessSurface? Open(int width, int height, string title)
    {
        lock (_gate)
        {
            _surfaces.RemoveAll(s => s.Ended);
            _pending.RemoveAll(s => s.Ended);
            if (_stopped || Accounts.Generation != _generation || _surfaces.Count >= 4) { return null; }
            var surface = new ProcessSurface(width, height, title);
            _surfaces.Add(surface); _pending.Add(surface); return surface;
        }
    }
    public void Tick(WindowManager windows)
    {
        lock (_gate)
        {
            if (_stopped) { return; }
            if (Accounts.Generation != _generation) { StopCore(); return; }
            foreach (var surface in _pending)
            { if (!surface.Ended) { windows.Open(new ClientWindow(surface, _identity)); Zenith.Core.Log.Write("graphics", "native surface opened " + surface.Width + "x" + surface.Height); } }
            _pending.Clear();
            _surfaces.RemoveAll(s => s.Ended);
        }
    }
    private void StopCore()
    { _stopped = true; foreach (var s in _surfaces) { s.Dispose(); } _surfaces.Clear(); _pending.Clear(); }
    public void Stop() { lock (_gate) { StopCore(); } }

    private sealed class ClientWindow : Window
    {
        private readonly ProcessSurface _surface;
        private readonly int[] _frame;
        public ClientWindow(ProcessSurface surface, Credentials identity) : base("Native · " + surface.Title, surface.Width, surface.Height + TitleBarHeight)
        { _surface = surface; Identity = identity; _frame = new int[surface.Width * surface.Height]; }
        public override bool CanResize => false;
        public override bool UpdateWhenMinimized => true;
        public override bool HasUnsavedChanges => _surface.Unsaved;
        public override void Close() { if (_surface.RequestClose()) { base.Close(); } }
        public override void EndSession() { _surface.Dispose(); base.Close(); }
        public override bool Update()
        {
            if (_surface.Ended) { base.Close(); Zenith.Core.Log.Write("graphics", "native surface retired"); return true; }
            return !IsMinimized && _surface.CopyFrame(_frame, false);
        }
        public override void DrawContent(Surface surface, Rect content)
        {
            Rect clipped = content.Intersect(surface.Clip);
            for (int y = clipped.Y; y < clipped.Bottom; y++)
            {
                int start = (y - content.Y) * _surface.Width + clipped.X - content.X;
                Array.Copy(_frame, start, surface.Pixels, y * surface.Width + clipped.X, clipped.W);
            }
        }
        protected override void OnFocusChanged(bool focused) => _surface.Send(new(2, focused ? 1 : 0, 0, 0, 0, 0));
        public override void OnMouseDown(int x, int y) => Pointer(4, x, y);
        public override void OnMouseUp(int x, int y) => Pointer(5, x, y);
        public override void OnMouseDrag(int x, int y) => Pointer(6, x, y);
        private void Pointer(int type, int x, int y) => _surface.Send(new(type, 1, Math.Clamp(x, 0, _surface.Width - 1), Math.Clamp(y, 0, _surface.Height - 1), 0, 0));
        public override void OnScroll(int delta) => _surface.Send(new(8, Math.Clamp(delta, -120, 120), 0, 0, 0, 0));
        public override void OnKey(KeyEvent key)
        {
            int special = key.Key switch
            {
                ConsoleKeyEx.Escape => 1, ConsoleKeyEx.Enter => 2, ConsoleKeyEx.Backspace => 3,
                ConsoleKeyEx.Tab => 4, ConsoleKeyEx.LeftArrow => 5, ConsoleKeyEx.RightArrow => 6,
                ConsoleKeyEx.UpArrow => 7, ConsoleKeyEx.DownArrow => 8, ConsoleKeyEx.Delete => 9,
                ConsoleKeyEx.Home => 10, ConsoleKeyEx.End => 11, _ => 0
            };
            int modifiers = ((key.Modifiers & ConsoleModifiers.Shift) != 0 ? 1 : 0)
                | ((key.Modifiers & ConsoleModifiers.Control) != 0 ? 2 : 0) | ((key.Modifiers & ConsoleModifiers.Alt) != 0 ? 4 : 0);
            _surface.Send(new(3, ProcessKeyEncoding.Text(key.KeyChar, key.Key.ToString(), modifiers), 0, 0, modifiers, special));
        }
    }
}
