using System.Security.Cryptography;
using System.Text;
using Zenith.Core.Boot;
using Zenith.Core.Packages;

internal static class IndexCommands
{
    public static int? Run(string[] args)
    {
        if (args.Length == 4 && args[0] == "index-check")
        {
            var index = RepositoryIndex.Read(SignedCommands.Read(Path.GetFullPath(args[1]), RepositoryIndex.MaximumBytes),
                SignedCommands.Read(Path.GetFullPath(args[2]), 4096), new HostPackageVerifier(), args[3]);
            Console.WriteLine("verified " + index.Repository + " generation=" + index.Generation + " issued=" + index.Issued + " expires=" + index.Expires); return 0;
        }
        if (args.Length != 8 || args[0] != "index-seal") { return null; }
        string repository = args[1], root = Path.GetFullPath(args[5]);
        using RSA rsa = RSA.Create(); rsa.ImportFromPem(Encoding.ASCII.GetString(SignedCommands.Read(Path.GetFullPath(args[6]), 8192)));
        if (rsa.KeySize is < 2048 or > 4096) { throw new IOException("RSA key must be 2048..4096 bits"); }
        byte[] key = Encoding.ASCII.GetBytes(rsa.ExportSubjectPublicKeyInfoPem() + "\n");
        var entries = new List<RepositoryEntry>(); var verifier = new HostPackageVerifier(); int visited = 0;
        void Walk(string directory)
        {
            if (LinuxFileType.Read(directory) != 0x4000 || ++visited > 1024) { throw new IOException("linked or excessive index source tree"); }
            foreach (string file in Directory.EnumerateFileSystemEntries(directory))
            {
                ushort type = LinuxFileType.Read(file);
                if (type == 0x4000) { Walk(file); continue; }
                if (type != 0x8000) { throw new IOException("special/linked index source refused"); }
                if (!file.EndsWith(".zpk", StringComparison.Ordinal)) { continue; }
                byte[] bytes = SignedCommands.Read(file, KernelPackage.MaximumBytes); RepositoryEntry entry;
                if (Encoding.ASCII.GetString(bytes, 0, Math.Min(32, bytes.Length)).StartsWith("ZENITH-SIGNED-KERNEL\t", StringComparison.Ordinal))
                {
                    var package = KernelPackage.Read(bytes, key, verifier);
                    entry = new(repository, "kernel", "zenith-kernel", package.Version, bytes.Length, PackagePlanner.Digest(bytes), "");
                }
                else
                {
                    var package = SignedPackage.Read(bytes, key, verifier);
                    if (package.Manifest.Repository != repository) { throw new IOException("signed package repository mismatch"); }
                    entry = new(repository, "data", package.Manifest.Name, package.Manifest.Version, bytes.Length, package.ArchiveDigest, package.ManifestText);
                }
                if (Path.GetFullPath(file) != Path.Combine(root, entry.ArchivePath)) { throw new IOException("package is not at its immutable identity path"); }
                if (entries.Count == 64) { throw new IOException("index entry limit"); } entries.Add(entry);
            }
        }
        SignedCommands.Parents(root);
        if (LinuxFileType.Read(root) != 0x4000) { throw new IOException("linked index publication root refused"); }
        if (repository is not ("core" or "extra")) { throw new IOException("unknown repository"); }
        string directory = Path.Combine(root, repository); if (Directory.Exists(directory)) { Walk(directory); }
        entries.Sort((a, b) => { int name = string.CompareOrdinal(a.Name, b.Name); return name != 0 ? name : PackageVersions.Compare(b.Version, a.Version); });
        byte[] content = Encoding.UTF8.GetBytes(RepositoryIndex.Content(repository, ulong.Parse(args[2]), long.Parse(args[3]), long.Parse(args[4]), entries));
        byte[] header = Encoding.ASCII.GetBytes("ZENITH-SIGNED-INDEX\t1\t" + PackagePlanner.Digest(key) + "\t" + content.Length + "\t" + rsa.KeySize / 8 + "\n");
        byte[] signed = new byte[header.Length + content.Length]; header.CopyTo(signed, 0); content.CopyTo(signed, header.Length);
        byte[] signature = rsa.SignHash(SHA256.HashData(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        byte[] archive = new byte[signed.Length + signature.Length]; signed.CopyTo(archive, 0); signature.CopyTo(archive, signed.Length);
        _ = RepositoryIndex.Read(archive, key, verifier, repository);
        string output = Path.GetFullPath(args[7]); SignedCommands.Parents(output);
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write); stream.Write(archive); stream.Flush(true);
        Console.WriteLine("sealed " + repository + " generation=" + args[2] + " entries=" + entries.Count); return 0;
    }
}
