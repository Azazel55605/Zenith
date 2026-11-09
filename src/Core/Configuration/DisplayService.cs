using System;
using System.Collections.Generic;
using Zenith.Core.Security;

namespace Zenith.Core.Configuration;

/// <summary>A device mode and the logical scale in percent. 100 and 200 are integer scales (each
/// logical pixel is one or four device pixels); 125, 150 and 175 present the logical frame
/// filtered (the stopgap until the shell draws at the device's resolution).</summary>
internal readonly record struct DisplayLayout(int Width, int Height, int Percent)
{
    public static readonly int[] Percents = { 100, 125, 150, 175, 200 };
    public static bool FitsVmware(DisplayLayout mode, uint maxWidth, uint maxHeight, uint vramBytes, uint offset)
        => mode.Usable && mode.Width <= maxWidth && mode.Height <= maxHeight
            && (ulong)mode.Width*(ulong)mode.Height*8+offset <= vramBytes;
    /// <summary>A requested scale: 1 and 2 (the first display ABI) or a percent.</summary>
    public static int PercentOf(uint scale) => scale is 1 or 2 ? (int)scale * 100 : scale <= 400 ? (int)scale : 0;
    /// <summary>The integer scale older clients understand: 2 only at 200%.</summary>
    public int Scale => Percent == 200 ? 2 : 1;
    public bool Integer => Percent is 100 or 200;
    public int LogicalWidth => Percent > 0 ? Width * 100 / Percent : 0;
    public int LogicalHeight => Percent > 0 ? Height * 100 / Percent : 0;
    public bool Usable => Array.IndexOf(Percents, Percent) >= 0 && LogicalWidth >= 800 && LogicalHeight >= 600
        && Width <= 16384 && Height <= 16384 && (long)Width * Height <= 32 * 1024 * 1024
        && (!Integer || (Width % Scale == 0 && Height % Scale == 0));
}
internal readonly record struct DisplayState(DisplayLayout Current, ulong Revision, ulong Token, int Phase,
    int Seconds, int Error, string Driver, DisplayLayout[] Modes, bool Active, bool Persistent, int Owner);

/// <summary>Requests cross to the session owner through Tick. The host, never the client,
/// owns the preview deadline. No disk or hardware changes occur on a request thread.</summary>
internal sealed class DisplayService
{
    public static long Now => (System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000));
    public const string Path = "/etc/zenith/display.conf";
    private readonly ISettingsStore _store;
    private readonly object _gate = new();
    private DisplayLayout _current, _previous, _target;
    private DisplayLayout[] _modes = Array.Empty<DisplayLayout>();
    private string _driver = "No display";
    private Func<DisplayLayout, bool>? _apply;
    private Action<bool>? _finish;
    private ulong _revision = 1, _token;
    private int _owner, _phase, _action, _error;
    private long _deadline;
    private bool _writable = true;
    public DisplayService(ISettingsStore store) { _store = store; }
    public DisplayLayout? Saved()
    {
        try
        {
            string? text = _store.Read(Path);
            if (text is null) { return null; }
            // Schema 1 keeps an integer scale (what earlier kernels read); schema 2 a fractional percent.
            string[] fields = text.Trim().Split(' ');
            if (fields.Length != 5 || fields[0] != "ZENITH-DISPLAY" || (fields[1] != "1" && fields[1] != "2")
                || !int.TryParse(fields[2], out int w) || !int.TryParse(fields[3], out int h)
                || !int.TryParse(fields[4], out int scale) || (fields[1] == "1" ? scale is not (1 or 2) : scale is 100 or 200)
                || !new DisplayLayout(w,h,fields[1] == "1" ? scale * 100 : scale).Usable)
            { _writable = false; return null; }
            return new(w,h,fields[1] == "1" ? scale * 100 : scale);
        }
        catch { _writable = false; return null; }
    }
    private bool _fractional;
    /// <summary>Whether the backend presents 125/150/175% (filtered).</summary>
    public bool Fractional { get { lock (_gate) { return _fractional; } } }
    public void Attach(string driver, DisplayLayout current, DisplayLayout[] modes,
        Func<DisplayLayout, bool>? apply, Action<bool>? finish, bool fractional = false)
    {
        lock (_gate)
        {
            _fractional = fractional;
            if (_phase != 0) { throw new InvalidOperationException("display preview still owned"); }
            _driver = driver; _current = current; _modes = (DisplayLayout[])modes.Clone();
            _apply = apply; _finish = finish; _revision++;
        }
    }
    public DisplayState Snapshot(long now)
    {
        lock (_gate) { return new(_current, _revision, _token, _phase,
            _phase == 2 ? (int)Math.Max(0, (_deadline - now + 999) / 1000) : 0,
            _error, _driver, (DisplayLayout[])_modes.Clone(), _apply is not null && _writable, _store.Persistent, _owner); }
    }
    public long Request(int uid, int owner, uint action, uint mode, uint scale, ulong revision, ulong token)
    {
        lock (_gate)
        {
            if (uid != 0) { return -13; }
            if (_apply is null || !_writable) { return -19; }
            if (action is < 1 or > 3) { return -22; }
            if (action == 1)
            {
                if (_phase != 0) { return -16; }
                if (revision != _revision) { return -11; }
                if (token != 0 || mode >= _modes.Length) { return -22; }
                var target = _modes[mode] with { Percent = DisplayLayout.PercentOf(scale) };
                if (!target.Integer && !_fractional) { return -22; }
                if (!target.Usable) { return -22; }
                if (target == _current) { return -114; }
                _previous = _current; _target = target; _owner = owner;
                _token++; if (_token == 0) { _token++; }
                _phase = 1; _action = 1; _error = 0; _revision++; return 0;
            }
            if (_phase != 2 || owner != _owner || token != _token || revision != _revision) { return -11; }
            if (mode != 0 || scale != 0) { return -22; }
            if (_action != 0) { return -16; }
            _action = (int)action; return 0;
        }
    }
    public void Abandon(int owner) { lock (_gate) { if (_phase != 0 && _owner == owner) { _action = 3; } } }
    // Called only by the UI owner; callbacks never execute from Snapshot/Request.
    public void Tick(long now)
    {
        lock (_gate)
        {
            if (_phase == 0) { return; }
            if (_phase == 1)
            {
                if (_action == 3) { Complete(false, 0); return; }
                // False means the backend left the previous display intact.
                // A failed rollback must propagate to kernel recovery, not be
                // reported as a harmless rejected preview.
                bool ok = _apply!(_target);
                if (!ok) { Complete(false, -5); return; }
                _current = _target; _phase = 2; _action = 0; _deadline = now + 15000; _revision++;
                return;
            }
            if (now >= _deadline || _action == 3) { Restore(0); return; }
            if (_action != 2) { return; }
            var root = CredentialContext.Enter(Credentials.Root);
            try
            {
                _store.Replace(Path, _current.Integer
                    ? "ZENITH-DISPLAY 1 " + _current.Width + " " + _current.Height + " " + _current.Scale + "\n"
                    : "ZENITH-DISPLAY 2 " + _current.Width + " " + _current.Height + " " + _current.Percent + "\n");
                root.Dispose();
            }
            catch
            {
                // The pinned runtime skips unwind-finally on some failures.
                // Restore the UI owner's credentials before rollback callbacks.
                root.Dispose(); Restore(-5); return;
            }
            Complete(true, 0);
        }
    }
    private void Restore(int error)
    {
        // Backend retains the previous frame allocation for the entire trial.
        if (!_apply!(_previous)) { throw new InvalidOperationException("display rollback failed"); }
        _current = _previous; Complete(false, error);
    }
    private void Complete(bool kept, int error)
    {
        _finish?.Invoke(kept); _phase = _action = 0; _owner = 0; _error = error; _revision++;
    }
    public void Detach(long now)
    {
        lock (_gate) { if (_phase != 0) { _action = 3; Tick(now); } _apply = null; _finish = null; _revision++; }
    }
}
