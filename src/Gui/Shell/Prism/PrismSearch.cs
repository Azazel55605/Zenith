// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Zenith.Gui.Shell.Prism;

/// <summary>What a result does, and the section it is listed under (in this order).</summary>
internal enum PrismKind { Calculator = 0, App = 1, Window = 2, Setting = 3, Action = 4, Command = 5 }

/// <summary>One result. <see cref="Target"/> names what to open: an app name, a settings page id, a
/// window index, an action ("restart", "shutdown", "logout") or a shell command. The title and the
/// keywords (an app's description, a page's category) are searched; the subtitle is only shown.</summary>
internal sealed record PrismItem(PrismKind Kind, string Title, string Subtitle, string Icon, string Target, int Score = 0, string Keywords = "");

/// <summary>
/// Prism's search: ranks candidates against a query, evaluates arithmetic, and offers to run a
/// command. Pure: the shell supplies the candidates (installed apps, open windows, settings pages,
/// power actions) and acts on the chosen item.
/// </summary>
internal static class PrismSearch
{
    public const int MaximumQuery = 128;
    public static readonly string[] SectionTitles = { "Calculator", "Applications", "Open windows", "Settings pages", "Actions", "Commands" };

    /// <summary>The filters Tab cycles through: everything, or one kind.</summary>
    public static readonly (string Label, PrismKind? Kind)[] Filters =
        { ("All", null), ("Apps", PrismKind.App), ("Windows", PrismKind.Window), ("Settings", PrismKind.Setting), ("Actions", PrismKind.Action) };

    /// <summary>How well <paramref name="text"/> matches; 0 when it does not. Whole-text prefix beats a
    /// word's start, which beats a substring, which beats the letters in order.</summary>
    public static int Score(string query, string text)
    {
        if (query.Length == 0) { return 1; }
        if (text.Length == 0) { return 0; }
        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase)) { return 1000 - Math.Min(text.Length, 200); }
        int at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at > 0)
        {
            for (int i = at; i > 0; i = i + 1 < text.Length ? text.IndexOf(query, i + 1, StringComparison.OrdinalIgnoreCase) : -1)
            { if (!char.IsLetterOrDigit(text[i - 1])) { return 700 - Math.Min(i, 200); } }
            return 500 - Math.Min(at, 200);
        }
        int position = 0, gaps = 0;
        foreach (char c in query)
        {
            if (char.IsWhiteSpace(c)) { continue; }
            int found = text.IndexOf(char.ToString(c), position, StringComparison.OrdinalIgnoreCase);
            if (found < 0) { return 0; }
            gaps += found - position; position = found + 1;
        }
        return Math.Max(1, 200 - gaps * 4);
    }

    /// <summary>The ranked results for <paramref name="query"/>: a calculator answer first, then each
    /// kind's best matches (best first), at most <paramref name="maximum"/> in all. A leading "&gt;"
    /// asks only to run the rest as a command.</summary>
    public static List<PrismItem> Search(string query, IReadOnlyList<PrismItem> candidates, PrismKind? filter = null, int maximum = 12)
    {
        query = query.Length > MaximumQuery ? query[..MaximumQuery] : query;
        string trimmed = query.Trim();
        var results = new List<PrismItem>();
        if (trimmed.StartsWith('>'))
        {
            string command = trimmed[1..].Trim();
            if (command.Length > 0) { results.Add(new(PrismKind.Command, "Run \"" + command + "\"", "Command in a terminal", "terminal", command, 1000)); }
            return results;
        }
        if (filter is null && TryEvaluate(trimmed, out double value) && LooksLikeArithmetic(trimmed))
        {
            string answer = Format(value);
            results.Add(new(PrismKind.Calculator, trimmed + " = " + answer, "Calculator · Enter copies the result", "zap", answer, 2000));
        }
        var ranked = new List<PrismItem>();
        foreach (var item in candidates)
        {
            if (filter is PrismKind kind && item.Kind != kind) { continue; }
            int score = Math.Max(Score(trimmed, item.Title), Score(trimmed, item.Keywords) / 2);
            if (score > 0) { ranked.Add(item with { Score = score }); }
        }
        // Group by kind (section order), best first inside a section; stable for equal scores.
        var order = new List<int>(); for (int i = 0; i < ranked.Count; i++) { order.Add(i); }
        order.Sort((a, b) =>
        {
            int k = ((int)ranked[a].Kind).CompareTo((int)ranked[b].Kind);
            if (k != 0) { return k; }
            int s = ranked[b].Score.CompareTo(ranked[a].Score);
            return s != 0 ? s : a.CompareTo(b);
        });
        // With a query, a section shows its best few so every kind stays visible.
        var perKind = new int[6];
        foreach (int i in order)
        {
            if (results.Count >= maximum) { break; }
            var item = ranked[i];
            if (trimmed.Length > 0 && filter is null && perKind[(int)item.Kind] >= (item.Kind == PrismKind.App ? 6 : 4)) { continue; }
            perKind[(int)item.Kind]++; results.Add(item);
        }
        if (trimmed.Length > 0 && filter is null && results.Count < maximum)
        { results.Add(new(PrismKind.Command, "Run \"" + trimmed + "\" in a terminal", "Command", "terminal", trimmed, 0)); }
        return results;
    }

    private static bool LooksLikeArithmetic(string s)
    {
        bool digit = false, op = false;
        foreach (char c in s) { if (char.IsDigit(c)) { digit = true; } else if ("+-*/^%()x".IndexOf(c) >= 0) { op = true; } }
        return digit && op;
    }

    public static string Format(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) { return "undefined"; }
        if (Math.Abs(value - Math.Round(value)) < 1e-9 && Math.Abs(value) < 1e15) { return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture); }
        return value.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    /// <summary>Arithmetic with + - * / % ^, parentheses, unary minus and decimals ("x" also
    /// multiplies). Bounded: at most 128 characters and 32 levels of nesting.</summary>
    public static bool TryEvaluate(string text, out double value)
    {
        value = 0;
        if (text.Length == 0 || text.Length > MaximumQuery) { return false; }
        var parser = new Parser(text);
        try
        {
            value = parser.Expression(0);
            parser.Skip();
            return parser.Done && !double.IsNaN(value) && !double.IsInfinity(value);
        }
        catch (FormatException) { return false; }
    }

    private sealed class Parser
    {
        private readonly string _s; private int _i;
        public Parser(string s) { _s = s; }
        public bool Done => _i >= _s.Length;
        public void Skip() { while (_i < _s.Length && _s[_i] == ' ') { _i++; } }
        private char Peek() { Skip(); return _i < _s.Length ? _s[_i] : '\0'; }
        public double Expression(int depth)
        {
            if (depth > 32) { throw new FormatException(); }
            double v = Term(depth);
            for (char c = Peek(); c is '+' or '-'; c = Peek()) { _i++; double r = Term(depth); v = c == '+' ? v + r : v - r; }
            return v;
        }
        private double Term(int depth)
        {
            double v = Power(depth);
            for (char c = Peek(); c is '*' or '/' or '%' or 'x' or 'X'; c = Peek())
            {
                _i++; double r = Power(depth);
                v = c == '/' ? v / r : c == '%' ? v % r : v * r;
            }
            return v;
        }
        private double Power(int depth)
        {
            double v = Unary(depth);
            if (Peek() == '^') { _i++; v = Math.Pow(v, Power(depth + 1)); }
            return v;
        }
        private double Unary(int depth)
        {
            char c = Peek();
            if (c == '-') { _i++; return -Unary(depth + 1); }
            if (c == '+') { _i++; return Unary(depth + 1); }
            if (c == '(')
            {
                _i++; double v = Expression(depth + 1);
                if (Peek() != ')') { throw new FormatException(); }
                _i++; return v;
            }
            int start = _i; bool dot = false;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || (_s[_i] == '.' && !dot))) { dot |= _s[_i] == '.'; _i++; }
            if (_i == start || (_i == start + 1 && dot)) { throw new FormatException(); }
            return double.Parse(_s.AsSpan(start, _i - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The settings pages as candidates: title, then "Meridian · Category".</summary>
    public static IEnumerable<PrismItem> SettingsPages()
    {
        foreach (var (id, title, category) in MeridianPages.All)
        { yield return new(PrismKind.Setting, title, "Meridian · " + category, "sliders-horizontal", id, Keywords: category); }
    }

    /// <summary>Power and session actions; <paramref name="root"/> decides whether restart and
    /// shut down are offered.</summary>
    public static IEnumerable<PrismItem> Actions(bool root)
    {
        yield return new(PrismKind.Action, "Log out", "Session · change user or session", "rotate-ccw", "logout", Keywords: "session sign out user");
        if (root)
        {
            yield return new(PrismKind.Action, "Restart", "Power", "rotate-ccw", "restart", Keywords: "power reboot");
            yield return new(PrismKind.Action, "Shut down", "Power · power off", "zap", "shutdown", Keywords: "power off halt");
        }
    }
}
