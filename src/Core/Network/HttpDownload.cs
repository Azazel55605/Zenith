using System;
using System.IO;
using System.Text;

namespace Zenith.Core.Network;

internal sealed record HttpTarget(string Host, int Port, string Path, bool Secure = false)
{
    public static HttpTarget Parse(string url)
    {
        bool secure = url.StartsWith("https://", StringComparison.Ordinal);
        if ((!secure && !url.StartsWith("http://", StringComparison.Ordinal)) || url.Length > 350)
        { throw new IOException("only bounded http:// or https:// URLs supported"); }
        string remainder = url.Substring(secure ? 8 : 7); int slash = remainder.IndexOf('/');
        string authority = slash < 0 ? remainder : remainder.Substring(0, slash);
        string path = slash < 0 ? "/" : remainder.Substring(slash);
        int colon = authority.IndexOf(':'); string host = colon < 0 ? authority : authority.Substring(0, colon);
        int port = colon < 0 ? (secure ? 443 : 80) : int.Parse(authority.Substring(colon + 1)); host = NetworkText.Host(host);
        if (port is < 1 or > 65535 || path.Contains('#') || path.Contains('\\')) { throw new IOException("invalid HTTP URL"); }
        foreach (char c in url) { if (c < 33 || c > 126) { throw new IOException("HTTP URL must be printable ASCII without spaces"); } }
        return new HttpTarget(host, port, path, secure);
    }
    public byte[] Request => Encoding.ASCII.GetBytes("GET " + Path + " HTTP/1.0\r\nHost: " + Host + ":" + Port
        + "\r\nConnection: close\r\nAccept-Encoding: identity\r\n\r\n");
}

/// <summary>Incremental HTTP lab decoder; no body buffer or allocation proportional to payload size.</summary>
internal sealed class HttpDownload
{
    public const int MaximumHeader = 8192;
    public const int MaximumBytes = 8 * 1024 * 1024;
    private readonly byte[] _header = new byte[MaximumHeader];
    private readonly Action<byte[], int, int> _write;
    private readonly Action _cancel;
    private int _headerLength;
    private readonly int _maximum;
    public HttpDownload(Action<byte[], int, int> write, Action cancel, int maximum = MaximumBytes)
    {
        if (maximum < 0 || maximum > MaximumBytes) { throw new ArgumentOutOfRangeException(nameof(maximum)); }
        _write = write; _cancel = cancel; _maximum = maximum;
    }
    public int Length { get; private set; } = -1;
    public int Received { get; private set; }
    public bool Done => Length >= 0 && Received == Length;
    public void Feed(byte[] data, int count)
    {
        if (count < 0 || count > data.Length) { throw new ArgumentOutOfRangeException(nameof(count)); }
        _cancel(); int p = 0;
        while (Length < 0 && p < count)
        {
            if (_headerLength == MaximumHeader) { throw new IOException("HTTP headers exceed 8192 bytes"); }
            byte b = data[p++]; if (b > 127 || b == 0) { throw new IOException("invalid HTTP header byte"); }
            _header[_headerLength++] = b;
            if (_headerLength >= 4 && _header[_headerLength - 4] == 13 && _header[_headerLength - 3] == 10
                && _header[_headerLength - 2] == 13 && _header[_headerLength - 1] == 10) { ParseHeaders(); }
        }
        if (Length < 0) { return; }
        int body = count - p;
        if (body > Length - Received) { throw new IOException("HTTP body exceeds declared Content-Length"); }
        if (body > 0) { _cancel(); _write(data, p, body); Received += body; }
    }
    public void Complete()
    { _cancel(); if (!Done) { throw new IOException("truncated HTTP response"); } }
    private void ParseHeaders()
    {
        string[] lines = Encoding.ASCII.GetString(_header, 0, _headerLength - 4).Split("\r\n");
        if (!(lines[0].StartsWith("HTTP/1.0 200 ", StringComparison.Ordinal) || lines[0].StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal)))
        { throw new IOException("HTTP requires status 200; redirects and error responses are rejected"); }
        int length = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i]; int colon = line.IndexOf(':');
            if (colon < 1 || line[0] == ' ' || line[0] == '\t' || line.Contains('\n') || line.Contains('\r'))
            { throw new IOException("invalid HTTP header"); }
            string key = line.Substring(0, colon).ToLowerInvariant(), value = line.Substring(colon + 1).Trim();
            if (key == "content-length")
            {
                if (length >= 0 || value.Length == 0) { throw new IOException("duplicate or invalid Content-Length"); }
                foreach (char c in value) { if (c < '0' || c > '9') { throw new IOException("invalid Content-Length"); } }
                if (!int.TryParse(value, out length) || length > _maximum) { throw new IOException("HTTP body exceeds download limit"); }
            }
            if (key == "transfer-encoding" || (key == "content-encoding" && !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            { throw new IOException("HTTP transfer/compression encoding unsupported"); }
        }
        if (length < 0) { throw new IOException("HTTP requires Content-Length"); } Length = length;
    }
}
