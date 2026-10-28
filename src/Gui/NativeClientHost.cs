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
    public ProcessSurface? Open(int width, int height, string title, int format = 1)
    {
        lock (_gate)
        {
            _surfaces.RemoveAll(s => s.Ended);
            _pending.RemoveAll(s => s.Ended);
            if (_stopped || Accounts.Generation != _generation || _surfaces.Count >= 4) { return null; }
            var surface = new ProcessSurface(width, height, title, format);
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
        private int[] _frame;
        private int _frameWidth, _frameHeight;
        private bool _wasResizable;
        private readonly ProcessMaterialRegion[] _materials = new ProcessMaterialRegion[ProcessMaterialRegion.MaximumRegions];
        private readonly MaterialCache? _composition;
        private readonly ProcessMaterialRegion[] _effective = new ProcessMaterialRegion[ProcessMaterialRegion.MaximumRegions];
        private int _materialCount;
        public ClientWindow(ProcessSurface surface, Credentials identity) : base("Native · " + surface.Title, surface.Width, surface.Height + TitleBarHeight)
        { _surface = surface; Identity = identity; _frameWidth = surface.Width; _frameHeight = surface.Height; _frame = new int[surface.Width * surface.Height]; if (surface.Format == 2) { _composition = new(1920000); } }
        public override bool UnifiedChrome => _surface.Resizable;
        public override bool CanResize => _surface.Resizable;
        public override (int Width, int Height) MinimumSize => (600, 480 + TitleBarHeight);
        public override (int Width, int Height) MaximumSize => (ProcessSurface.MaximumWidth, ProcessSurface.MaximumHeight + TitleBarHeight);
        protected override void OnResized() => _surface.RequestResize(Content.W, Content.H);
        public override bool UpdateWhenMinimized => true;
        public override bool HasUnsavedChanges => _surface.Unsaved;
        public override void Close() { if (_surface.RequestClose()) { _composition?.Dispose();base.Close(); } }
        public override void EndSession() { _composition?.Dispose();_surface.Dispose(); base.Close(); }
        public override bool Update()
        {
            if (_surface.Ended) { _composition?.Dispose();base.Close(); Zenith.Core.Log.Write("graphics", "native surface retired"); return true; }
            bool changed = _wasResizable != _surface.Resizable; _wasResizable = _surface.Resizable;
            var size = _surface.FrameSize;
            if (size.Width != _frameWidth || size.Height != _frameHeight)
            {
                _frame = new int[size.Width * size.Height]; Array.Fill(_frame, (int)(Theme.WindowBackground | 0xff000000U)); _frameWidth = size.Width; _frameHeight = size.Height; _materialCount = 0; changed = true;
            }
            if (IsMinimized || !_surface.CopyFrame(_frame, false, _materials, out int count)) { return changed; }
            _materialCount = count; return true;
        }
        public override void CaptureBackdrop(Surface surface, Rect content)
        {
            if (_composition is null || content.W != _frameWidth || content.H != _frameHeight) { return; }
            int area = 0; bool blur = false, any = false;
            int scale = _surface.Resizable && (MaterialResources.Policy.Effective & 2) != 0 && _frameWidth * _frameHeight > 131072 ? 4 : 1;
            for (int i = 0; i < _materialCount; i++)
            {
                var r = _materials[i]; uint tint = r.Role is 1 or 3 ? Theme.Panel : Theme.Raised;
                int effect = r.Role == 1 && (MaterialResources.Policy.Effective & 4) == 0 ? 0
                    : MaterialResources.Policy.Effect(r.Effect, tint, Theme.TextPrimary, Theme.TextSecondary, MaterialComposition.SampleLength(r.Width, scale) * MaterialComposition.SampleLength(r.Height, scale));
                area += MaterialComposition.SampleLength(r.Width, scale) * MaterialComposition.SampleLength(r.Height, scale); blur |= effect == 2; any |= effect != 0;
                _effective[i] = r with { Effect = effect, Opacity = MaterialPolicy.Opacity(tint, Theme.TextPrimary, Theme.TextSecondary, effect) };
            }
            if (any && !_composition.Ensure(area, blur, scale))
            {
                bool tintOnly = blur && _composition.Ensure(area, false, scale);
                for (int i = 0; i < _materialCount; i++)
                {
                    var r = _effective[i]; uint tint = r.Role is 1 or 3 ? Theme.Panel : Theme.Raised;
                    int effect = tintOnly && r.Effect != 0 ? 1 : 0;
                    _effective[i] = r with { Effect = effect, Opacity = MaterialPolicy.Opacity(tint, Theme.TextPrimary, Theme.TextSecondary, effect) };
                }
            }
            _composition.Composition.Capture(surface.Pixels, surface.Width, surface.Height, content, _effective, _materialCount, Theme.WindowBackground);
        }
        public override void DrawContent(Surface surface, Rect content)
        {
            if (content.W != _frameWidth || content.H != _frameHeight)
            { surface.FillRect(content, Theme.WindowBackground); return; }
            if (_composition is not null)
            {
                _composition.Composition.Compose(_frame, surface.Pixels, surface.Width, surface.Height, content, _effective, _materialCount,
                    Theme.Panel, Theme.Raised, Theme.Panel, Theme.Raised);
                return;
            }
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
        public override void OnScrollAt(int delta, int x, int y) => _surface.Send(new(8, Math.Clamp(delta, -120, 120), Math.Clamp(x, 0, Content.W-1), Math.Clamp(y, 0, Content.H-1), 0, 0));
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
