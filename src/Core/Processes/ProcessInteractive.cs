namespace Zenith.Core.Processes;

/// <summary>Opt-in GUI quotas renew per minute only while the owned surface lives.
/// Legacy processes retain lifetime quotas. Closing a surface gives a bounded
/// cleanup interval; revocation never turns into an immortal headless process.</summary>
internal sealed class ProcessInteractive
{
    public const long WindowMilliseconds = 60000, CleanupMilliseconds = 2000;
    private bool _enabled;
    private long _window, _ended = -1;
    public bool Enabled => _enabled;
    public int Enable(bool ownedLiveSurface, long now)
    {
        if (!ownedLiveSurface) { return -9; }
        if (_enabled) { return -16; }
        _enabled = true; _window = now; return 0;
    }
    public bool Advance(bool live, long now, out bool renew)
    {
        renew = false;
        if (!_enabled) { return true; }
        if (!live)
        {
            if (_ended < 0) { _ended = now; }
            return now - _ended < CleanupMilliseconds;
        }
        if (_ended >= 0) { return false; }
        if (now - _window >= WindowMilliseconds)
        { _window = now; renew = true; }
        return true;
    }
}
