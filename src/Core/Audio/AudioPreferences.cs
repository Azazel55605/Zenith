using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Zenith.Core.Audio;

// Store endpoint identities, never the enumeration order, stream ID or microphone authorization.
internal sealed class AudioPreferences
{
    public const string Path = "/etc/zenith/audio-devices.conf";
    private const int Limit = 64;
    internal readonly record struct Level(int Volume, bool Muted);
    private readonly Dictionary<string, Level> _levels = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private readonly string[] _selected = ["", ""];

    internal static string Identity(uint slot, uint controller, uint codecModel, uint codec, uint pin, uint direction, uint config)
    {
        if (controller is 0 or uint.MaxValue || codecModel is 0 or uint.MaxValue || codec >= 15 || pin > 255 || direction > 1)
        { return ""; }
        return $"{slot:x8}{controller:x8}{codecModel:x8}{codec:x2}{pin:x2}{direction:x1}{config:x8}";
    }

    private static bool Valid(string key)
    {
        if (key.Length != 37) { return false; }
        foreach (char c in key) { if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) { return false; } }
        return true;
    }

    public bool Selected(string key, int direction) => key.Length != 0 && key == _selected[direction];
    public bool TryLevel(string key, out Level value) => _levels.TryGetValue(key, out value);
    public void Remember(string key, int direction, int volume, bool muted, bool select)
    {
        if (!Valid(key) || direction is < 0 or > 1 || volume is < 0 or > 100) { return; }
        if (select) { _selected[direction] = key; }
        _order.Remove(key);
        if (_order.Count >= Limit)
        {
            int at = _order.FindIndex(k => k != _selected[0] && k != _selected[1]);
            _levels.Remove(_order[at]); _order.RemoveAt(at);
        }
        _order.Add(key); _levels[key] = new(volume, muted);
    }

    public string Encode()
    {
        var text = new StringBuilder("version=1\n");
        for (int d = 0; d < 2; d++) { text.Append(_selected[d]).Append('\n'); }
        foreach (var key in _order)
        {
            var level = _levels[key];
            text.Append(key).Append(' ').Append(level.Volume.ToString(CultureInfo.InvariantCulture))
                .Append(level.Muted ? " 1\n" : " 0\n");
        }
        return text.ToString();
    }

    public static AudioPreferences Decode(string? text)
    {
        var result = new AudioPreferences();
        if (string.IsNullOrEmpty(text) || text.Length > 4096) { return result; }
        string[] lines = text.Split('\n');
        if (lines.Length < 3 || lines[0] != "version=1") { return result; }
        for (int d = 0; d < 2; d++)
        {
            if (Valid(lines[d + 1]) && lines[d + 1][28] == '0' + d) { result._selected[d] = lines[d + 1]; }
        }
        for (int i = 3; i < lines.Length; i++)
        {
            string[] fields = lines[i].Split(' ');
            if (fields.Length != 3 || !Valid(fields[0]) || fields[2] is not ("0" or "1") ||
                !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int volume)) { continue; }
            result.Remember(fields[0], fields[0][28] - '0', volume, fields[2] == "1", false);
        }
        return result;
    }
}
