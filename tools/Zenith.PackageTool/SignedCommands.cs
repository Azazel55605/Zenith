using System.Security.Cryptography;
using System.Text;
using Zenith.Core.Packages;

internal sealed class HostPackageVerifier : IPackageSignatureVerifier
{
    public bool Verify(byte[] publicKey, byte[] digest, byte[] signature)
    {
        using RSA rsa = RSA.Create(); rsa.ImportFromPem(Encoding.ASCII.GetString(publicKey));
        return rsa.KeySize is >= 2048 and <= 4096 && signature.Length == rsa.KeySize / 8
            && rsa.VerifyHash(digest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }
}
internal static class SignedCommands
{
    public static int? Run(string[] args)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("package tooling currently requires Linux"); }
        int? index = IndexCommands.Run(args); if (index.HasValue) { return index; }
        int? kernel = KernelCommands.Run(args); if (kernel.HasValue) { return kernel; }
        if (args.Length == 2 && args[0] == "keygen")
        {
            string output = Path.GetFullPath(args[1]);
            if (Directory.Exists(output) || File.Exists(output)) { throw new IOException("key output exists"); }
            Parents(output); string temporary = output + ".staging-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(temporary); File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                using RSA rsa = RSA.Create(3072);
                using (var file = new FileStream(temporary + "/private.pem", new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
                { file.Write(Encoding.ASCII.GetBytes(rsa.ExportPkcs8PrivateKeyPem() + "\n")); }
                byte[] key = Encoding.ASCII.GetBytes(rsa.ExportSubjectPublicKeyInfoPem() + "\n");
                File.WriteAllBytes(temporary + "/public.pem", key); Directory.Move(temporary, output);
                Console.WriteLine("public PEM SHA256=" + PackagePlanner.Digest(key));
                Console.WriteLine("keep private.pem on the signing machine; publish only public.pem");
            }
            catch { if (Directory.Exists(temporary)) { Directory.Delete(temporary, true); } throw; }
            return 0;
        }
        if (args.Length == 4 && args[0] == "seal")
        {
            string bundle = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[3]); Parents(output);
            string text = new UTF8Encoding(false, true).GetString(Read(bundle + "/manifest.tsv", PackageManifest.MaximumBytes));
            byte[] content = SignedPackage.Content(text, new DirectorySource(bundle));
            using RSA rsa = RSA.Create(); rsa.ImportFromPem(Encoding.ASCII.GetString(Read(Path.GetFullPath(args[2]), 8192)));
            if (rsa.KeySize is < 2048 or > 4096) { throw new IOException("RSA key must be 2048..4096 bits"); }
            byte[] key = Encoding.ASCII.GetBytes(rsa.ExportSubjectPublicKeyInfoPem() + "\n");
            byte[] header = Encoding.ASCII.GetBytes("ZENITH-SIGNED-PACKAGE\t1\t" + PackagePlanner.Digest(key) + "\t" + content.Length + "\t" + rsa.KeySize / 8 + "\n");
            byte[] signed = new byte[header.Length + content.Length]; header.CopyTo(signed, 0); content.CopyTo(signed, header.Length);
            byte[] signature = rsa.SignHash(SHA256.HashData(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            byte[] archive = new byte[signed.Length + signature.Length]; signed.CopyTo(archive, 0); signature.CopyTo(archive, signed.Length);
            _ = SignedPackage.Read(archive, key, new HostPackageVerifier());
            string temporary = output + ".staging-" + Guid.NewGuid().ToString("N");
            try { File.WriteAllBytes(temporary, archive); File.Move(temporary, output, false); }
            catch { if (File.Exists(temporary)) { File.Delete(temporary); } throw; }
            Console.WriteLine("sealed " + archive.Length + " bytes SHA256=" + PackagePlanner.Digest(archive)); return 0;
        }
        if (args.Length == 3 && args[0] == "check")
        {
            var package = SignedPackage.Read(Read(Path.GetFullPath(args[1]), SignedPackage.MaximumBytes), Read(Path.GetFullPath(args[2]), 4096), new HostPackageVerifier());
            Console.WriteLine("verified " + package.Manifest.Name + " " + package.Manifest.Version + " repo=" + package.Manifest.Repository); return 0;
        }
        return null;
    }
    internal static void Parents(string path)
    {
        for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
        { if (Directory.Exists(parent) && LinuxFileType.Read(parent) != 0x4000) { throw new IOException("linked parent refused"); } }
    }
    internal static byte[] Read(string path, int maximum)
    {
        Parents(path); if (LinuxFileType.Read(path) != 0x8000) { throw new IOException("expected regular source/key"); }
        using var file = File.OpenRead(path); if (file.Length > maximum) { throw new IOException("source/key exceeds bound"); }
        byte[] bytes = new byte[(int)file.Length]; file.ReadExactly(bytes); if (file.ReadByte() != -1) { throw new IOException("source grew"); } return bytes;
    }
}
