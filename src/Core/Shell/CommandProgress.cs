using System;
using System.Diagnostics;
using System.Globalization;

namespace Zenith.Core.Shell;

/// <summary>One terminal status line; progress is omitted from redirected stdout.</summary>
internal sealed class CommandProgress
{
    private readonly IOutput _output;
    private readonly bool _interactive;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _last = -1000;
    private bool _active;
    public CommandProgress(IOutput output, bool interactive) { _output = output; _interactive = interactive; }
    public void Report(string message)
    {
        if (!_interactive) { return; }
        const string prefix = "fetch progress ";
        if (message.StartsWith(prefix, StringComparison.Ordinal))
        {
            string[] values = message.Substring(prefix.Length).Split('/');
            if (values.Length == 2 && int.TryParse(values[0], out int done) && int.TryParse(values[1], out int total) && total > 0)
            {
                if (_active && done < total && _clock.ElapsedMilliseconds - _last < 200) { return; }
                message = Format("fetch progress", done, total) + " " + (done / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture)
                    + "/" + (total / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";
            }
        }
        const string applying = "Applying files ";
        if (message.StartsWith(applying, StringComparison.Ordinal))
        {
            string[] values = message.Substring(applying.Length).Split('/');
            if (values.Length == 2 && int.TryParse(values[0], out int done) && int.TryParse(values[1], out int total))
            { message = Format("Applying files", done, total); }
        }
        _last = _clock.ElapsedMilliseconds;
        message = message.Replace('\r', ' ').Replace('\n', ' ');
        if (message.Length > 76) { message = message.Substring(0, 73) + "..."; }
        _output.Write("\r\u001b[2K" + message); _active = true;
    }
    public static string Format(string label, int done, int total)
    {
        int percent = total <= 0 ? 0 : (int)Math.Clamp((long)done * 100 / total, 0, 100);
        int filled = percent * 16 / 100;
        return label + " [" + new string('#', filled) + new string('-', 16 - filled) + "] " + percent + "%";
    }
    public void Finish()
    {
        if (!_active) { return; }
        _active = false;
        // Closing a status line must not replace an original failure/cancellation.
        try { _output.Write("\n"); } catch { }
    }
}
