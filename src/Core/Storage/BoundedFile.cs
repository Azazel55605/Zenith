using System;
using System.IO;
using System.Text;

namespace Zenith.Core.Storage;

internal static class BoundedFile
{
    public static string? ReadText(string path, int maximum)
    {
        if (!new VfsFileMetadata().TryRead(path, out var stat)) { return null; }
        if (stat.Type != "regular file" || stat.Size > (ulong)maximum)
        { throw new IOException("expected bounded regular file: " + path); }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        byte[] bytes = new byte[(int)stat.Size]; int read = 0;
        while (read < bytes.Length)
        { int n = stream.Read(bytes, read, bytes.Length - read); if (n == 0) { throw new IOException("file shortened during read"); } read += n; }
        if (stream.ReadByte() != -1) { throw new IOException("file grew during read"); }
        return new UTF8Encoding(false, true).GetString(bytes);
    }
}
