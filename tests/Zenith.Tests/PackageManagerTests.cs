using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using Zenith.Core.Packages;

namespace Zenith.Tests;

public class PackageManagerTests
{
    private sealed class Verifier : IPackageSignatureVerifier
    {
        public bool Verify(byte[] key, byte[] digest, byte[] signature)
        { using var rsa = RSA.Create(); rsa.ImportFromPem(Encoding.ASCII.GetString(key)); return rsa.VerifyHash(digest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss); }
    }
    private sealed class Source(byte[] bytes) : IPackageSource { public byte[] Read(PackageFile file) => bytes; }
    private static string Manifest(string name, string version, string data, string records = "", string? path = null) =>
        "ZENITH-PACKAGE\t2\nrepository\textra\nname\t" + name + "\nversion\t" + version + "\narchitecture\tany\nkind\tdata\nruntime\tbootstrap-1\n" + records
        + "file\t" + (path ?? "/usr/share/doc/" + name + ".txt") + "\t0644\t" + Encoding.ASCII.GetByteCount(data) + "\t" + PackagePlanner.Digest(Encoding.ASCII.GetBytes(data)) + "\n";
    private static (byte[] Archive, byte[] Key) Seal(string text, string data)
    {
        using var rsa = RSA.Create(2048); byte[] key = Encoding.ASCII.GetBytes(rsa.ExportSubjectPublicKeyInfoPem() + "\n");
        byte[] content = SignedPackage.Content(text, new Source(Encoding.ASCII.GetBytes(data)));
        byte[] header = Encoding.ASCII.GetBytes("ZENITH-SIGNED-PACKAGE\t1\t" + PackagePlanner.Digest(key) + "\t" + content.Length + "\t256\n");
        byte[] signed = header.Concat(content).ToArray(); byte[] sig = rsa.SignHash(SHA256.HashData(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return (signed.Concat(sig).ToArray(), key);
    }
    private static SignedPackage Package(string name, string version = "1.0.0", string data = "hello", string records = "", string? path = null)
    { var pair = Seal(Manifest(name, version, data, records, path), data); return SignedPackage.Read(pair.Archive, pair.Key, new Verifier()); }
    [Fact]
    public void ProgressFailureDuringApplyRestoresOriginalTransaction()
    {
        var store = new Store(); var manager = new PackageManager(store);
        manager.Install(new[] { Package("hello-tools", data: "old") }, () => { });
        Assert.Throws<OperationCanceledException>(() => manager.Install(new[] { Package("hello-tools", "2.0.0", "new") }, () => { },
            text => { if (text == "Applying files 1/1") { throw new OperationCanceledException(); } }));
        Assert.Equal("1.0.0", manager.List().Single().Manifest.Version);
        Assert.Empty(manager.Verify());
    }

    private sealed class Store : IPackageStore
    {
        public readonly Dictionary<string, (byte[] Bytes, ushort Mode)> Files = new(StringComparer.Ordinal);
        public readonly HashSet<string> Directories = new(StringComparer.Ordinal) { "/" };
        public int Operations, FailAt = -1; public bool PersistentFailure;
        public ulong FreeBytes { get; set; } = 16777216;
        private void Fault()
        { if (++Operations == FailAt || (PersistentFailure && Operations >= FailAt && FailAt >= 0)) { throw new IOException("injected storage fault"); } }
        public PackagePathState State(string path) => Files.ContainsKey(path) ? PackagePathState.Occupied : Directories.Contains(path) ? PackagePathState.Directory : PackagePathState.Missing;
        public byte[] Read(string path, int maximum)
        { if (!Files.TryGetValue(path, out var file) || file.Bytes.Length > maximum) { throw new IOException("missing/oversized"); } return file.Bytes.ToArray(); }
        public ushort Mode(string path) => Files[path].Mode;
        public void Directory(string path) { Fault(); Directories.Add(path); }
        public void Write(string path, byte[] bytes, ushort mode) { Fault(); Files[path] = (bytes.ToArray(), mode); }
        public void Delete(string path) { Fault(); Files.Remove(path); }
        public void Sync() { Fault(); }
    }
    [Theory]
    [InlineData("1.2", "1.2.0", true)]
    [InlineData("1.3", ">=1.2,<2.0", true)]
    [InlineData("2.0", ">=1.2,<2.0", false)]
    [InlineData("1.0", ">1.0", false)]
    [InlineData("1.0", "<=1.0", true)]
    public void NumericRanges(string version, string constraint, bool matches) => Assert.Equal(matches, PackageVersions.Matches(version, constraint));
    [Fact]
    public void SignedContainerRejectsTamperingWrongKeyTruncationAndSuffix()
    {
        var pair = Seal(Manifest("hello", "1.0", "hello"), "hello"); var verifier = new Verifier();
        Assert.Equal("hello", SignedPackage.Read(pair.Archive, pair.Key, verifier).Manifest.Name);
        foreach (int index in new[] { 0, 60, pair.Archive.Length / 2, pair.Archive.Length - 1 })
        { byte[] changed = pair.Archive.ToArray(); changed[index] ^= 1; Assert.Throws<IOException>(() => SignedPackage.Read(changed, pair.Key, verifier)); }
        Assert.Throws<IOException>(() => SignedPackage.Read(pair.Archive[..^1], pair.Key, verifier));
        Assert.Throws<IOException>(() => SignedPackage.Read(pair.Archive.Concat(new byte[1]).ToArray(), pair.Key, verifier));
        Assert.Throws<IOException>(() => SignedPackage.Read(pair.Archive, Seal(Manifest("hello", "1.0", "hello"), "hello").Key, verifier));
    }
    [Fact]
    public void MultiPackageDependenciesAreAtomicAndRemovalProtectsDependents()
    {
        var store = new Store(); var manager = new PackageManager(store);
        var tool = Package("tool", records: "requires\tlibrary\t>=1.0,<2.0\n"); var library = Package("library");
        Assert.Throws<IOException>(() => manager.Install(new[] { tool }, () => { })); Assert.Empty(manager.List());
        manager.Install(new[] { tool, library }, () => { }); Assert.Equal(2, manager.List().Count); Assert.Empty(manager.Verify());
        Assert.Throws<IOException>(() => manager.Remove("library", () => { })); Assert.Equal(2, manager.List().Count);
        Assert.Throws<IOException>(() => manager.Install(new[] { Package("library", "2.0.0") }, () => { }));
        manager.Remove("tool", () => { }); manager.Remove("library", () => { }); Assert.Empty(manager.List());
    }
    [Fact]
    public void CyclesWithoutHooksCanInstallAsOneSatisfiedSet()
    {
        var manager = new PackageManager(new Store());
        manager.Install(new[] { Package("one", records: "requires\ttwo\t1.0.0\n"), Package("two", records: "requires\tone\t1.0.0\n") }, () => { });
        Assert.Equal(2, manager.List().Count);
        manager.Remove(new[] { "one", "two" }, () => { }); Assert.Empty(manager.List());
    }
    [Fact]
    public void ConflictsOwnershipAndLocalChangesPreserveData()
    {
        var store = new Store(); var manager = new PackageManager(store); manager.Install(new[] { Package("one") }, () => { });
        Assert.Throws<IOException>(() => manager.Install(new[] { Package("two", records: "conflicts\tone\n") }, () => { }));
        Assert.Throws<IOException>(() => manager.Install(new[] { Package("two", path: "/usr/share/doc/one.txt") }, () => { }));
        store.Files["/usr/share/doc/three.txt.pkg-new"] = (Encoding.ASCII.GetBytes("unowned"), 0x1a4);
        Assert.Throws<IOException>(() => manager.Install(new[] { Package("three") }, () => { }));
        Assert.Equal("unowned", Encoding.ASCII.GetString(store.Files["/usr/share/doc/three.txt.pkg-new"].Bytes));
        Assert.False(store.Files.ContainsKey(PackageManager.Journal));
        store.Files["/usr/share/doc/one.txt"] = (Encoding.ASCII.GetBytes("local"), 0x1a4);
        Assert.Single(manager.Verify());
        Assert.Throws<IOException>(() => manager.Remove("one", () => { }));
        Assert.Throws<IOException>(() => manager.Install(new[] { Package("one", "1.1.0") }, () => { }));
        Assert.Equal("local", Encoding.ASCII.GetString(store.Files["/usr/share/doc/one.txt"].Bytes));
    }
    [Fact]
    public void UpgradesPersistModeAndRejectDowngradeOrSpaceFailure()
    {
        var store = new Store(); var manager = new PackageManager(store); manager.Install(new[] { Package("one") }, () => { });
        manager.Install(new[] { Package("one", "1.1.0", "new") }, () => { }); Assert.Empty(manager.Verify());
        Assert.Equal("1.1.0", new PackageManager(store).List()[0].Manifest.Version);
        Assert.Throws<IOException>(() => manager.Install(new[] { Package("one") }, () => { }));
        store.FreeBytes = 0; Assert.Throws<IOException>(() => manager.Remove("one", () => { })); Assert.Single(manager.List());
    }
    [Fact]
    public void EveryMutationFaultEitherCommitsOrRecoversOldGeneration()
    {
        var first = Package("one"); var second = Package("one", "1.1.0", "new data");
        for (int scenario = 0; scenario < 3; scenario++)
        for (int point = 1; point <= 36; point++)
        {
            var store = new Store(); var manager = new PackageManager(store); if (scenario != 0) { manager.Install(new[] { first }, () => { }); }
            store.Operations = 0; store.FailAt = point; store.PersistentFailure = true;
            try { if (scenario == 2) { manager.Remove("one", () => { }); } else { manager.Install(new[] { second }, () => { }); } } catch (IOException) { }
            store.FailAt = -1; store.PersistentFailure = false; new PackageManager(store).Recover();
            Assert.Empty(new PackageManager(store).Verify());
            var installed = new PackageManager(store).List();
            if (installed.Count != 0) { Assert.Contains(installed[0].Manifest.Version, new[] { "1.0.0", "1.1.0" }); } Assert.False(store.Files.ContainsKey(PackageManager.Journal));
        }
    }
    [Fact]
    public void CancellationDuringMutationRollsBackAndReleasesJournal()
    {
        var store = new Store(); var manager = new PackageManager(store); int calls = 0;
        Assert.Throws<OperationCanceledException>(() => manager.Install(new[] { Package("one") }, () => { if (++calls == 3) { throw new OperationCanceledException(); } }));
        Assert.Empty(manager.List()); Assert.False(store.Files.ContainsKey("/usr/share/doc/one.txt"));
    }
    [Fact]
    public void DefaultRepositoriesArePortableAndRejectUnsafeMirrors()
    {
        Assert.Equal(new[] { "core", "extra" }, PackageRepositories.Parse(PackageRepositories.Default).Select(x => x.Name));
        Assert.All(PackageRepositories.Parse(PackageRepositories.Default), x => Assert.Null(x.Mirror));
        Assert.Throws<FormatException>(() => PackageRepositories.Parse(PackageRepositories.Default.Replace("extra", "core")));
        Assert.Throws<FormatException>(() => PackageRepositories.Parse(PackageRepositories.Default.Replace("10\t-", "10\thttp://host")));
        Assert.Throws<FormatException>(() => PackageRepositories.Parse(PackageRepositories.Default.Replace("10\t-", "10\thttps://user:secret@host")));
    }
}
