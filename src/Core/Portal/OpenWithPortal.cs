using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Zenith.Core.Portal;

/// <summary>What the Open with dialog needs: the file, its extension (lower case, "" for none), whether the
/// program asked to choose, and the app the person chose before for this extension (null when none).</summary>
internal sealed record OpenWithPrompt(string Path, string Extension, bool Choose, string? Remembered);

/// <summary>Where the person's "always use this app" choices live, one record per account.</summary>
internal interface IOpenWithStore
{
    string? Read(uint uid);
    void Replace(uint uid, string text);
}

/// <summary>Host tests and live sessions: choices kept in memory.</summary>
internal sealed class MemoryOpenWithStore : IOpenWithStore
{
    private readonly Dictionary<uint, string> _records = new();
    public string? Read(uint uid) => _records.TryGetValue(uid, out var text) ? text : null;
    public void Replace(uint uid, string text) => _records[uid] = text;
}

/// <summary>org.zenith.portal.OpenWith 1: a program asks the desktop to open a file with an app, as
/// xdg-open does. The desktop picks the app (the person's remembered choice, else the app that declares the
/// file's type) or asks in its own dialog; the program never names or starts the app itself.
/// <para>Methods: <c>Open</c> opens with the usual app and asks only when there is none; <c>Choose</c> always
/// asks (the Open with... menu). Payload: the absolute path, UTF-8, 1..1024 bytes (an extended payload,
/// request flag 1, because paths do not fit the 144 inline bytes). Response: u32 opened (1/0), then the app's
/// name, NUL-padded to 64 bytes.</para></summary>
internal sealed class OpenWithPortal : IPortalInterface
{
    public const string InterfaceName = "org.zenith.portal.OpenWith";
    public const int MaximumChoices = 64;
    public string Name => InterfaceName;
    public uint Version => 1;
    private readonly IOpenWithStore _store;
    private readonly object _gate = new();

    public OpenWithPortal(IOpenWithStore store) { _store = store; }

    public int Open(PortalRequest request)
    {
        if (request.Method is not ("Open" or "Choose")) { return -38; }
        string? path = PathOf(request.Payload);
        if (path is null) { return -22; }
        string extension = ExtensionOf(path);
        request.Prompt = new OpenWithPrompt(path, extension, request.Method == "Choose", extension.Length > 0 ? Remembered(request.Caller.Uid, extension) : null);
        return 0;   // the desktop's dialog launches (at once when it knows the app) or asks
    }

    /// <summary>The desktop opened the file with <paramref name="app"/>; <paramref name="always"/> remembers it
    /// for this kind of file.</summary>
    public bool Opened(PortalRequest request, string app, bool always)
    {
        if (request.Prompt is OpenWithPrompt prompt && always && prompt.Extension.Length > 0) { Remember(request.Caller.Uid, prompt.Extension, app); }
        byte[] response = new byte[68];
        BinaryPrimitives.WriteUInt32LittleEndian(response, 1);
        byte[] name = Encoding.ASCII.GetBytes(app.Length > 63 ? app[..63] : app);
        name.CopyTo(response, 4);
        return PortalBroker.Finish(request, PortalState.Approved, "opened with " + app, response);
    }
    public bool Declined(PortalRequest request, string why) => PortalBroker.Finish(request, PortalState.Denied, why, new byte[68]);
    public bool Failed(PortalRequest request, string why) => PortalBroker.Finish(request, PortalState.Failed, why, new byte[68]);

    /// <summary>An absolute path without NUL or control characters; null otherwise.</summary>
    public static string? PathOf(byte[] payload)
    {
        if (payload.Length is < 2 or > 1024) { return null; }
        int end = Array.IndexOf(payload, (byte)0); int length = end < 0 ? payload.Length : end;
        for (int i = length; i < payload.Length; i++) { if (payload[i] != 0) { return null; } }
        string path;
        try { path = new UTF8Encoding(false, true).GetString(payload, 0, length); }
        catch (ArgumentException) { return null; }
        if (path.Length < 2 || path[0] != '/' || path.EndsWith('/')) { return null; }
        foreach (char c in path) { if (c < ' ' || c == '\u007f') { return null; } }
        return path;
    }
    /// <summary>"Notes.MD" → "md"; names without a dot, or starting with one, have none.</summary>
    public static string ExtensionOf(string path)
    {
        int slash = path.LastIndexOf('/'); string name = path[(slash + 1)..];
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) { return ""; }
        string extension = name[(dot + 1)..].ToLowerInvariant();
        foreach (char c in extension) { if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '+')) { return ""; } }
        return extension.Length <= 16 ? extension : "";
    }

    // Records: "extension=App" lines, at most 64.
    public string? Remembered(uint uid, string extension)
    {
        lock (_gate) { return Parse(Read(uid)).TryGetValue(extension, out var app) ? app : null; }
    }
    public void Remember(uint uid, string extension, string app)
    {
        if (!ValidApp(app) || extension.Length == 0) { return; }
        lock (_gate)
        {
            var choices = Parse(Read(uid));
            choices[extension] = app;
            if (choices.Count > MaximumChoices) { return; }
            var text = new StringBuilder();
            var keys = new List<string>(choices.Keys); keys.Sort(StringComparer.Ordinal);
            foreach (string key in keys) { text.Append(key).Append('=').Append(choices[key]).Append('\n'); }
            try { _store.Replace(uid, text.ToString()); }
            catch (Exception e) { Log.Write("portal", "open-with choice not saved: " + e.Message); }
        }
    }
    private string? Read(uint uid) { try { return _store.Read(uid); } catch (Exception e) { Log.Write("portal", "open-with choices unreadable: " + e.Message); return null; } }
    private static Dictionary<string, string> Parse(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (text is null) { return result; }
        foreach (string line in text.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0 || result.Count >= MaximumChoices) { continue; }
            string key = line[..eq], value = line[(eq + 1)..].Trim();
            if (ExtensionOf("/x." + key) == key && ValidApp(value)) { result[key] = value; }
        }
        return result;
    }
    public static bool ValidApp(string app)
    {
        if (app.Length is 0 or > 63) { return false; }
        foreach (char c in app) { if (c is < ' ' or > '~' or '=') { return false; } }
        return true;
    }
}
