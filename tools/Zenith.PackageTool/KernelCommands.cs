using System.Security.Cryptography;
using System.Text;
using Zenith.Core.Boot;

internal static class KernelCommands
{
    public static int? Run(string[] args)
    {
        if (args.Length == 3 && args[0] == "kernel-check")
        {
            var package = KernelPackage.Read(SignedCommands.Read(Path.GetFullPath(args[1]), KernelPackage.MaximumBytes),
                SignedCommands.Read(Path.GetFullPath(args[2]), 4096), new HostPackageVerifier());
            Console.WriteLine("verified zenith-kernel " + package.Version + " sha256=" + package.Id); return 0;
        }
        if (args.Length != 5 || args[0] != "kernel-seal") { return null; }
        byte[] content = KernelPackage.Content(args[1], SignedCommands.Read(Path.GetFullPath(args[2]), BootLayout.MaximumKernelBytes));
        using RSA rsa = RSA.Create(); rsa.ImportFromPem(Encoding.ASCII.GetString(SignedCommands.Read(Path.GetFullPath(args[3]), 8192)));
        if (rsa.KeySize is < 2048 or > 4096) { throw new IOException("RSA key must be 2048..4096 bits"); }
        byte[] key = Encoding.ASCII.GetBytes(rsa.ExportSubjectPublicKeyInfoPem() + "\n");
        byte[] header = Encoding.ASCII.GetBytes("ZENITH-SIGNED-KERNEL\t1\t" + BootLayout.Digest(key) + "\t" + content.Length + "\t" + rsa.KeySize / 8 + "\n");
        byte[] signed = new byte[header.Length + content.Length]; header.CopyTo(signed, 0); content.CopyTo(signed, header.Length);
        byte[] signature = rsa.SignHash(SHA256.HashData(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        byte[] archive = new byte[signed.Length + signature.Length]; signed.CopyTo(archive, 0); signature.CopyTo(archive, signed.Length);
        _ = KernelPackage.Read(archive, key, new HostPackageVerifier());
        string output = Path.GetFullPath(args[4]); SignedCommands.Parents(output);
        string temporary = output + ".staging-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temporary, archive); File.Move(temporary, output, false); }
        catch { if (File.Exists(temporary)) { File.Delete(temporary); } throw; }
        Console.WriteLine("sealed zenith-kernel " + args[1] + " bytes=" + archive.Length + " sha256=" + BootLayout.Digest(archive)); return 0;
    }
}
