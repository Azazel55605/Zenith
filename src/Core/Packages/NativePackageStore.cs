using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Vfs;
using Zenith.Core.Storage;

namespace Zenith.Core.Packages;

internal sealed class NativePackageStore : IPackageStore
{
    private readonly string _root;
    /// <param name="root">Where the package tree lives: empty for the running system, or the mount
    /// point of another root (Sunrise installs into the target before it is booted).</param>
    public NativePackageStore(string root) { _root = root.TrimEnd('/'); }
    public NativePackageStore() : this("") { }
    private string Map(string path) => _root + path;
    public PackagePathState State(string path) => NativePackageSource.State(Map(path));
    private static int Regular(string path, int maximum)
    {
        for (int slash = path.IndexOf('/', 1); slash >= 0; slash = path.IndexOf('/', slash + 1))
        { if (NativePackageSource.State(path.Substring(0, slash)) != PackagePathState.Directory) { throw new IOException("linked/missing package ancestor"); } }
        if (!new VfsFileMetadata().TryRead(path, out var stat) || stat.Type != "regular file" || stat.Size > (ulong)maximum)
        { throw new IOException("expected bounded regular package file"); }
        return (int)stat.Size;
    }
    public byte[] Read(string path, int maximum)
    {
        path = Map(path);
        int length = Regular(path, maximum);
        using var stream = File.OpenRead(path); byte[] bytes = new byte[length]; int offset = 0;
        try
        {
            while (offset < bytes.Length)
            {
                int n = stream.Read(bytes, offset, bytes.Length - offset);
                if (n == 0) { throw new IOException("truncated package file"); }
                offset += n;
            }
            if (stream.ReadByte() != -1) { throw new IOException("package file grew"); }
            return bytes;
        }
        catch { stream.Dispose(); throw; }
    }
    public ushort Mode(string path)
    {
        path = Map(path);
        if (!new VfsFileMetadata().TryRead(path, out var stat) || !stat.UnixAttributes) { throw new IOException("package mutation requires Unix metadata/ext2"); }
        return (ushort)(stat.Mode & 0xfff);
    }
    private static bool Under(string path, string mount)
        => mount == "/" || path == mount || path.StartsWith(mount.TrimEnd('/') + "/", StringComparison.Ordinal);
    /// <summary>The real path of a package mutation; refuses one that would land on another mount
    /// below this store's root.</summary>
    private string Mutation(string path)
    {
        path = Map(path);
        string own = _root.Length == 0 ? "/" : _root;
        foreach (var mount in VfsManager.Mounts)
        {
            if (mount.MountPoint != own && Under(mount.MountPoint, own) && Under(path, mount.MountPoint))
            { throw new IOException("package mutation cannot cross a mount"); }
        }
        return path;
    }
    public void Directory(string path)
    {
        path = Mutation(path);
        if (!VfsManager.TryCreateDirectory(path, (VfsMode)0x1ed)) { throw new IOException("cannot create package directory"); }
    }
    public void Write(string path, byte[] bytes, ushort mode)
    {
        path = Mutation(path);
        string temporary = path + ".pkg-new";
        if (NativePackageSource.State(temporary) != PackagePathState.Missing) { throw new IOException("occupied package staging path"); }
        if (!VfsManager.TryCreateFile(temporary, (VfsMode)0x180) || !VfsManager.TryOpenFile(temporary, out var file)) { throw new IOException("cannot stage package file"); }
        try
        {
            if (file.Write(bytes) != bytes.Length || !file.TryFlush()) { throw new IOException("package write failed"); }
            file.Dispose();
            if (!new VfsFileMetadata().SetMode(temporary, mode) || !VfsManager.TryRename(temporary, path)) { throw new IOException("package rename/mode failed"); }
        }
        catch { file.Dispose(); if (NativePackageSource.State(temporary) != PackagePathState.Missing) { File.Delete(temporary); } throw; }
    }
    public void Delete(string path)
    {
        path = Mutation(path);
        if (NativePackageSource.State(path) != PackagePathState.Missing) { Regular(path, SignedPackage.MaximumBytes); File.Delete(path); }
    }
    /// <summary>Removes a cache file of any size (kernel archives exceed data packages): same unlinked-
    /// ancestor, regular-file and mount checks as every other package mutation.</summary>
    public void DeleteCached(string path)
    { path = Mutation(path); Regular(path, int.MaxValue); File.Delete(path); }
    public void Sync() => SystemMounts.SyncAll();
    public ulong FreeBytes
    {
        get
        {
            if (!VfsManager.TryStatFs(_root.Length == 0 ? "/" : _root, out var stat)) { throw new IOException("cannot determine root free space"); }
            return checked(stat.Bavail * stat.BlockSize);
        }
    }
}

internal sealed unsafe class NativePackageVerifier : IPackageSignatureVerifier
{
    public static bool ValidKey(byte[] publicKey)
    {
        byte[] pem = new byte[publicKey.Length + 1]; publicKey.CopyTo(pem, 0); byte[] hash = new byte[32];
        fixed (byte* p = pem, h = hash) { return VerifyNative(p, (nuint)pem.Length, h, null, 0) == 0; }
    }
    public bool Verify(byte[] publicKey, byte[] digest, byte[] signature)
    {
        byte[] pem = new byte[publicKey.Length + 1]; publicKey.CopyTo(pem, 0);
        fixed (byte* p = pem, hash = digest, sig = signature)
        { return VerifyNative(p, (nuint)pem.Length, hash, sig, (nuint)signature.Length) == 0; }
    }
    [DllImport("*", EntryPoint = "zenith_pkg_verify")]
    private static extern int VerifyNative(byte* pem, nuint length, byte* hash, byte* signature, nuint signatureLength);
}
