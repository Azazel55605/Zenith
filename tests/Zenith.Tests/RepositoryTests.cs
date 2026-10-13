using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Zenith.Core.Packages;

namespace Zenith.Tests;

public class RepositoryTests
{
    private sealed class Verifier : IPackageSignatureVerifier
    {
        public bool Verify(byte[] key, byte[] hash, byte[] signature)
        {
            using RSA rsa = RSA.Create(); rsa.ImportFromPem(Encoding.ASCII.GetString(key));
            return rsa.VerifyHash(hash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
    }
    private static RepositoryEntry Entry(string name, string version = "1.0.0", string records = "", string repository = "extra", string? path = null)
    {
        byte[] data = Encoding.ASCII.GetBytes(name + version);
        string text = "ZENITH-PACKAGE\t2\nrepository\t" + repository + "\nname\t" + name + "\nversion\t" + version
            + "\narchitecture\tany\nkind\tdata\nruntime\t" + PackageManifest.Runtime + "\n" + records
            + "file\t" + (path ?? "/usr/share/doc/" + name + ".txt") + "\t0644\t" + data.Length + "\t" + PackagePlanner.Digest(data) + "\n";
        return new(repository, "data", name, version, 1024, PackagePlanner.Digest(data), text);
    }
    private static readonly IReadOnlyList<PackageRepository> Repositories = PackageRepositories.Parse(PackageRepositories.Default);
    private static byte[] Seal(RSA rsa, byte[] key, string content)
    {
        byte[] body = Encoding.UTF8.GetBytes(content);
        byte[] header = Encoding.ASCII.GetBytes("ZENITH-SIGNED-INDEX\t1\t" + PackagePlanner.Digest(key) + "\t" + body.Length + "\t256\n");
        byte[] signed = header.Concat(body).ToArray();
        return signed.Concat(rsa.SignHash(SHA256.HashData(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)).ToArray();
    }
    [Fact]
    public void RealSignedIndexRejectsTamperingTruncationSuffixWrongKeyAndRepository()
    {
        using RSA rsa = RSA.Create(2048), other = RSA.Create(2048);
        byte[] key = Encoding.ASCII.GetBytes(rsa.ExportSubjectPublicKeyInfoPem() + "\n");
        byte[] bytes = Seal(rsa, key, RepositoryIndex.Content("extra", 1, 100, 1000, new[] { Entry("hello") }));
        var verifier = new Verifier(); var index = RepositoryIndex.Read(bytes, key, verifier, "extra");
        Assert.Equal("extra/hello/1.0.0/hello.zpk", index.Entries.Single().ArchivePath);
        foreach (int position in new[] { 0, 64, bytes.Length / 2, bytes.Length - 1 })
        { byte[] bad = bytes.ToArray(); bad[position] ^= 1; Assert.Throws<IOException>(() => RepositoryIndex.Read(bad, key, verifier, "extra")); }
        Assert.Throws<IOException>(() => RepositoryIndex.Read(bytes[..^1], key, verifier, "extra"));
        Assert.Throws<IOException>(() => RepositoryIndex.Read(bytes.Concat(new byte[1]).ToArray(), key, verifier, "extra"));
        Assert.Throws<IOException>(() => RepositoryIndex.Read(bytes, Encoding.ASCII.GetBytes(other.ExportSubjectPublicKeyInfoPem() + "\n"), verifier, "extra"));
        Assert.Throws<IOException>(() => RepositoryIndex.Read(bytes, key, verifier, "core"));
    }
    [Fact]
    public void FreshnessAndPersistedGenerationRejectReplayAndChangedSnapshot()
    {
        var index = new RepositoryIndex("extra", 4, 1000, 2000, Array.Empty<RepositoryEntry>());
        index.Fresh(1500); Assert.Throws<IOException>(() => index.Fresh(2000)); Assert.Throws<IOException>(() => index.Fresh(600));
        RepositoryIndex.Advance(index, new byte[] { 1 }, index, new byte[] { 1 });
        Assert.Throws<IOException>(() => RepositoryIndex.Advance(index, new byte[] { 1 }, index with { Generation = 3 }, new byte[] { 1 }));
        Assert.Throws<IOException>(() => RepositoryIndex.Advance(index, new byte[] { 1 }, index, new byte[] { 2 }));
        RepositoryIndex.Advance(index, new byte[] { 1 }, index with { Generation = 5 }, new byte[] { 2 });
    }
    [Fact]
    public void IndexBoundsAndIdentityRejectMalformedSignedMetadata()
    {
        Assert.Throws<IOException>(() => RepositoryIndex.Content("extra", 0, 100, 1000, Array.Empty<RepositoryEntry>()));
        Assert.Throws<IOException>(() => RepositoryIndex.Content("extra", 1, 100, 100 + 31 * 86400, Array.Empty<RepositoryEntry>()));
        Assert.Throws<IOException>(() => RepositoryIndex.Content("extra", 1, 100, 1000, new[] { Entry("hello") with { Name = "../escape" } }));
        Assert.Throws<IOException>(() => RepositoryIndex.Content("core", 1, 100, 1000, new[] { Entry("hello") }));
        Assert.Throws<IOException>(() => RepositoryIndex.Content("extra", 1, 100, 1000, new[] { Entry("hello") with { Version = "2.0.0" } }));
        Assert.Throws<IOException>(() => RepositoryIndex.Content("extra", 1, 100, 1000, new[] { Entry("hello"), Entry("hello") }));
        Assert.Throws<IOException>(() => RepositoryIndex.Content("extra", 1, 100, 1000, Enumerable.Range(0, 65).Select(x => Entry("data-" + x)).ToArray()));
    }
    [Fact]
    public void DependenciesChooseCompatibleVersionAndPriorityWithBacktracking()
    {
        var app = Entry("app", records: "requires\tlib\t>=1.0,<2.0\n");
        var plan = RepositoryPlanner.Plan(new[] { app, Entry("lib", "2.0"), Entry("lib", "1.2") }, Repositories, Array.Empty<InstalledPackage>(), new[] { "app" });
        Assert.Equal("1.2", plan.Single(x => x.Name == "lib").Version);
        var preferred = Entry("app", "1.0", repository: "core");
        Assert.Equal("core", RepositoryPlanner.Plan(new[] { preferred, app }, Repositories, Array.Empty<InstalledPackage>(), new[] { "app" }).Single().Repository);
        var latest = Entry("app", "2.0", "requires\tmissing\t1.0\n");
        Assert.Equal("1.0.0", RepositoryPlanner.Plan(new[] { latest, app, Entry("lib") }, Repositories, Array.Empty<InstalledPackage>(), new[] { "app" }).Single(x => x.Name == "app").Version);
    }
    [Fact]
    public void InstalledDependenciesStayAndUpgradePreservesReverseConstraints()
    {
        var library = Entry("lib"); var app = Entry("app", records: "requires\tlib\t>=1.0,<2.0\n");
        var installed = new[] { new InstalledPackage(library.ManifestText, library.Digest, new string('0', 64)) };
        Assert.Single(RepositoryPlanner.Plan(new[] { app, library }, Repositories, installed, new[] { "app" }));
        var both = installed.Concat(new[] { new InstalledPackage(app.ManifestText, app.Digest, new string('0', 64)) }).ToArray();
        Assert.Throws<IOException>(() => RepositoryPlanner.Plan(new[] { Entry("lib", "2.0") }, Repositories, both, new[] { "lib" }));
        Assert.Empty(RepositoryPlanner.Plan(new[] { library }, Repositories, installed, new[] { "lib" }));
    }
    [Fact]
    public void ConflictsOwnershipDowngradesAndBundledIdentitiesAreBlocked()
    {
        var a = Entry("a"); var b = Entry("b", records: "conflicts\ta\n");
        Assert.Throws<IOException>(() => RepositoryPlanner.Plan(new[] { a, b }, Repositories, Array.Empty<InstalledPackage>(), new[] { "a", "b" }));
        Assert.Throws<IOException>(() => RepositoryPlanner.Plan(new[] { a, Entry("b", path: "/usr/share/doc/a.txt") }, Repositories, Array.Empty<InstalledPackage>(), new[] { "a", "b" }));
        Assert.Throws<IOException>(() => RepositoryPlanner.Plan(new[] { Entry("net-tools") }, Repositories, Array.Empty<InstalledPackage>(), new[] { "net-tools" }));
        var newer = Entry("a", "2.0"); var old = new[] { new InstalledPackage(newer.ManifestText, newer.Digest, new string('0', 64)) };
        Assert.Throws<IOException>(() => RepositoryPlanner.Plan(new[] { a }, Repositories, old, new[] { "a" }));
    }
    [Fact]
    public void DataCyclesResolveAtomicallyAndCancellationStopsPlanning()
    {
        var a = Entry("a", records: "requires\tb\t1.0\n"); var b = Entry("b", records: "requires\ta\t1.0\n");
        Assert.Equal(2, RepositoryPlanner.Plan(new[] { a, b }, Repositories, Array.Empty<InstalledPackage>(), new[] { "a" }).Count);
        Assert.Throws<OperationCanceledException>(() => RepositoryPlanner.Plan(new[] { a, b }, Repositories, Array.Empty<InstalledPackage>(), new[] { "a" }, () => throw new OperationCanceledException()));
    }
}
