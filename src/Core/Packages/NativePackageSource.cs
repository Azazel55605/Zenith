using System;
using System.IO;
using Zenith.Core.Storage;

namespace Zenith.Core.Packages;

internal sealed class NativePackageSource : IPackageSource
{
    private readonly string _directory;
    public NativePackageSource(string directory) { _directory = directory.TrimEnd('/'); }
    public byte[] Read(PackageFile file)
    {
        string path = _directory + "/payload" + file.Path;
        // Refuse leaf and ancestor symlinks; no arbitrary source traversal is needed.
        for (int slash = path.IndexOf('/', 1); slash >= 0; slash = path.IndexOf('/', slash + 1))
        { if (State(path.Substring(0, slash)) != PackagePathState.Directory) { throw new IOException("invalid payload ancestor"); } }
        var metadata = new VfsFileMetadata();
        if (!metadata.TryRead(path, out var stat) || stat.Type != "regular file" || stat.Size != (ulong)file.Length)
        { throw new IOException("invalid payload type or length: " + file.Path); }
        // Fixed manifest bound prevents unbounded reads even if metadata changes.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        byte[] data = new byte[file.Length]; int read = 0;
        while (read < data.Length)
        { int n = stream.Read(data, read, data.Length - read); if (n == 0) { throw new IOException("truncated payload"); } read += n; }
        if (stream.ReadByte() != -1) { throw new IOException("payload grew during verification"); }
        return data;
    }
    public static PackagePathState State(string path)
    {
        if (!new VfsFileMetadata().TryRead(path, out var stat)) { return PackagePathState.Missing; }
        return stat.Type == "directory" ? PackagePathState.Directory : PackagePathState.Occupied;
    }
    public static PackageManifest Load(string path)
    {
        return PackageManifest.Parse(BoundedFile.ReadText(path, PackageManifest.MaximumBytes)
            ?? throw new FileNotFoundException("package manifest missing"));
    }
}
