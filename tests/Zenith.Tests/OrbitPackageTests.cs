using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using Zenith.Core.Packages;
using Zenith.Core.Portal;

namespace Zenith.Tests;

/// <summary>Orbit's package service (kernel 0.0.70): history, about files, capabilities, package states with reasons,
/// plans with what is also needed and the exact pkg commands, and one transaction that installs and removes.</summary>
[Collection("PackageHistory")]
public class OrbitPackageTests
{
    [Fact]
    public void UpdateNoticeTracksOfferedVersionsRegardlessOfCatalogOrder()
    {
        PackageRow Row(string name, string candidate) => new(name, "native", "extra", "1.0.0", candidate,
            candidate, PackageRowState.Update, 100, 200, 3, true, "");
        var a = Row("alpha", "1.0.1"); var b = Row("beta", "2.0.0");
        var first = PackageCatalogView.UpdateOffers([a, b]);
        Assert.Equal(2, first.Count);
        Assert.Equal(first, PackageCatalogView.UpdateOffers([b, a]));
        Assert.NotEqual(first.Key, PackageCatalogView.UpdateOffers([a with { Candidate = "1.1.0" }, b]).Key);
        Assert.Equal((0, ""), PackageCatalogView.UpdateOffers([a with { State = PackageRowState.Current }]));
    }

    private sealed class Verifier : IPackageSignatureVerifier
    {
        public bool Verify(byte[] key, byte[] digest, byte[] signature)
        { using var rsa = RSA.Create(); rsa.ImportFromPem(Encoding.ASCII.GetString(key)); return rsa.VerifyHash(digest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss); }
    }
    private sealed class Source(byte[] bytes) : IPackageSource { public byte[] Read(PackageFile file) => bytes; }
    private sealed class Store : IPackageStore
    {
        public readonly Dictionary<string, (byte[] Bytes, ushort Mode)> Files = new(StringComparer.Ordinal);
        public readonly HashSet<string> Directories = new(StringComparer.Ordinal) { "/" };
        public ulong FreeBytes { get; set; } = 16777216;
        public PackagePathState State(string path) => Files.ContainsKey(path) ? PackagePathState.Occupied : Directories.Contains(path) ? PackagePathState.Directory : PackagePathState.Missing;
        public byte[] Read(string path, int maximum) { if (!Files.TryGetValue(path, out var f) || f.Bytes.Length > maximum) { throw new IOException("missing"); } return f.Bytes.ToArray(); }
        public ushort Mode(string path) => Files[path].Mode;
        public void Directory(string path) => Directories.Add(path);
        public void Write(string path, byte[] bytes, ushort mode) => Files[path] = (bytes.ToArray(), mode);
        public void Delete(string path) => Files.Remove(path);
        public void Sync() { }
    }
    private static readonly IReadOnlyList<PackageRepository> Repositories = PackageRepositories.Parse(PackageRepositories.Default);

    private static string Manifest(string name, string version, string records = "", string? path = null, string data = "x", string mode = "0644") =>
        "ZENITH-PACKAGE\t2\nrepository\tcore\nname\t" + name + "\nversion\t" + version + "\narchitecture\tany\nkind\tdata\nruntime\tbootstrap-1\n" + records
        + "file\t" + (path ?? "/usr/share/doc/" + name + ".txt") + "\t" + mode + "\t" + Encoding.ASCII.GetByteCount(data) + "\t" + PackagePlanner.Digest(Encoding.ASCII.GetBytes(data)) + "\n";
    private static RepositoryEntry Entry(string name, string version, string records = "", int bytes = 1000) =>
        new("core", "data", name, version, bytes, PackagePlanner.Digest(Encoding.ASCII.GetBytes(name + version)), Manifest(name, version, records, data: name + version));
    private static InstalledPackage Installed(string name, string version, string records = "") =>
        new(Manifest(name, version, records, data: name + version), PackagePlanner.Digest(Encoding.ASCII.GetBytes("installed" + name + version)), new string('0', 64));
    private static SignedPackage Package(string name, string version, string data, string records = "")
    {
        using var rsa = RSA.Create(2048); byte[] key = Encoding.ASCII.GetBytes(rsa.ExportSubjectPublicKeyInfoPem() + "\n");
        byte[] content = SignedPackage.Content(Manifest(name, version, records, data: data), new Source(Encoding.ASCII.GetBytes(data)));
        byte[] header = Encoding.ASCII.GetBytes("ZENITH-SIGNED-PACKAGE\t1\t" + PackagePlanner.Digest(key) + "\t" + content.Length + "\t256\n");
        byte[] signed = header.Concat(content).ToArray(); byte[] sig = rsa.SignHash(SHA256.HashData(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return SignedPackage.Read(signed.Concat(sig).ToArray(), key, new Verifier());
    }

    [Fact]
    public void HistoryRoundTripsSanitizesAndKeepsTheNewest64()
    {
        var all = new List<PackageTransaction>();
        for (int i = 0; i < 70; i++) { all.Add(new(i, 0, "root\tx", "Orbit", i % 2 == 0, new[] { "install a" + i + " 1.0", "remove b;c 1.0" }, "pkg install a\nb", "ok")); }
        var parsed = PackageHistory.Parse(PackageHistory.Serialize(all));
        Assert.Equal(64, parsed.Count);
        Assert.Equal(6, parsed[0].Time); Assert.Equal(69, parsed[^1].Time);
        Assert.Equal("root x", parsed[0].User); Assert.Equal("pkg install a b", parsed[0].Command);
        Assert.Equal(new[] { "install a6 1.0", "remove b,c 1.0" }, parsed[0].Changes);
        Assert.True(parsed[0].Succeeded); Assert.False(parsed[1].Succeeded);
        Assert.Empty(PackageHistory.Parse("garbage\n1\t2"));
    }

    [Fact]
    public void ChangesAndWordsDescribeATransaction()
    {
        var before = new[] { Installed("a", "1.0.0"), Installed("b", "1.0.0") };
        var after = new[] { Installed("a", "1.1.0"), Installed("c", "0.1.0") };
        var changes = PackageHistory.Changes(before, after);
        Assert.Equal(new[] { "upgrade a 1.0.0 1.1.0", "install c 0.1.0", "remove b 1.0.0" }, changes);
        Assert.Equal("Upgraded a 1.0.0 to 1.1.0", PackageHistory.Words(changes[0]));
        Assert.Equal("Removed b 1.0.0", PackageHistory.Words(changes[2]));
        Assert.Contains("active after a restart", PackageHistory.Words("kernel 0.0.69 0.0.70"));
    }

    [Fact]
    public void EveryCommitIsRecordedWithItsSourceAndOneTransactionInstallsAndRemoves()
    {
        var store = new Store(); var manager = new PackageManager(store);
        string source = PackageHistory.Source, command = PackageHistory.Command; var now = PackageHistory.Now;
        try
        {
            PackageHistory.Source = "shell"; PackageHistory.Command = "pkg install old"; PackageHistory.Now = () => 1000;
            manager.Install(new[] { Package("old", "1.0.0", "o") }, () => { });
            PackageHistory.Source = "Orbit"; PackageHistory.Command = "pkg remove old; pkg install fresh"; PackageHistory.Now = () => 2000;
            manager.Change(new[] { Package("fresh", "0.1.0", "f") }, new[] { "old" }, () => { });
            Assert.Equal(new[] { "fresh" }, manager.List().Select(x => x.Manifest.Name));
            Assert.False(store.Files.ContainsKey("/usr/share/doc/old.txt"));
            var history = PackageHistory.Read(store);
            Assert.Equal(2, history.Count);
            Assert.Equal("shell", history[0].Source); Assert.Equal(new[] { "install old 1.0.0" }, history[0].Changes);
            Assert.Equal("Orbit", history[1].Source); Assert.Equal(2000, history[1].Time);
            Assert.Equal(new[] { "install fresh 0.1.0", "remove old 1.0.0" }, history[1].Changes);
            // Removing what another package needs is refused as a whole: nothing changes.
            manager.Install(new[] { Package("app", "1.0.0", "a", "requires\tfresh\t0.1.0\n") }, () => { });
            Assert.Throws<IOException>(() => manager.Change(Array.Empty<SignedPackage>(), new[] { "fresh" }, () => { }));
            Assert.Equal(2, manager.List().Count);
            Assert.Throws<IOException>(() => manager.Change(Array.Empty<SignedPackage>(), Array.Empty<string>(), () => { }));
        }
        finally { PackageHistory.Source = source; PackageHistory.Command = command; PackageHistory.Now = now; }
    }

    [Fact]
    public void AboutFilesAreReadLenientlyAndBounded()
    {
        var about = PackageAbout.Parse("ZENITH-ABOUT\t1\ntitle\tPolaris\nsummary\tThe native terminal\ndescription\tA terminal.\ndescription\tWith tabs later.\n"
            + "category\tApps\nicon\tterminal\nreplaces\tOpens instead of the built-in Terminal\nchange\t0.1.0\tScrollback\nchange\tbad\tskipped\nplanned\tTabs\nunknown\tline\n");
        Assert.Equal("Polaris", about.Title); Assert.Equal("The native terminal", about.Summary);
        Assert.Equal("A terminal. With tabs later.", about.Description); Assert.Equal("apps", about.Category);
        Assert.Equal(new[] { ("0.1.0", "Scrollback") }, about.Changes); Assert.Equal(new[] { "Tabs" }, about.Planned);
        Assert.Equal("", PackageAbout.Parse("title\tNo header\n").Title);
        Assert.Equal("/usr/share/zenith/about/zenith-polaris.about", PackageAbout.PathOf("zenith-polaris"));
    }

    [Fact]
    public void CapabilitiesComeFromWhatThePackageDeclares()
    {
        var data = PackageAbout.Capabilities(PackageManifest.Parse(Manifest("fonts", "1.0.0")));
        Assert.Equal("Runs nothing", Assert.Single(data).Title);
        string records = "requires\tzenith-native-graphics-abi\t1.0\nrequires\tzenith-native-terminal-session-abi\t1.0\n";
        var program = PackageAbout.Capabilities(PackageManifest.Parse(Manifest("term", "1.0.0", records, "/usr/share/zenith/native/term.elf", mode: "0755")));
        Assert.Contains(program, c => c.Title == "Run a shell as you" && c.Allowed);
        Assert.Contains(program, c => c.Title == "Open windows" && c.Allowed);
        Assert.Contains(program, c => c.Title == "Use the network" && !c.Allowed && c.Detail == "No");
        Assert.Contains(program, c => c.Title == "Run as root" && !c.Allowed);
        var net = PackageAbout.Capabilities(PackageManifest.Parse(Manifest("net", "1.0.0", "requires\tzenith-native-network-abi\t1.0\n", "/usr/share/zenith/native/net.elf", mode: "0755")));
        Assert.Contains(net, c => c.Title == "Use the network" && c.Allowed);
        Assert.Contains(net, c => c.Title == "Open windows" && !c.Allowed);
    }

    [Fact]
    public void RowsSayWhatIsInstalledOfferedAndWhyNot()
    {
        var catalog = new[]
        {
            Entry("current", "1.0.0"), Entry("newer", "1.0.0"), Entry("newer", "1.1.0"), Entry("fresh", "0.1.0"),
            Entry("future", "0.4.0", "requires\tzenith-native-tomorrow-abi\t1.0\n"),
            Entry("kept", "1.0.0"), Entry("kept", "2.0.0", "requires\tzenith-native-tomorrow-abi\t1.0\n"),
            new RepositoryEntry("core", "kernel", "zenith-kernel", "0.0.70", 5000000, new string('1', 64), ""),
        };
        var installed = new[] { Installed("current", "1.0.0"), Installed("newer", "1.0.0"), Installed("local", "1.0.0") };
        var rows = PackageCatalogView.Rows(catalog, Repositories, installed, "0.0.69", "0.0.69").ToDictionary(r => r.Name);
        Assert.Equal(PackageRowState.Current, rows["current"].State);
        Assert.Equal(PackageRowState.Update, rows["newer"].State); Assert.Equal("1.1.0", rows["newer"].Candidate);
        Assert.Equal(PackageRowState.Available, rows["fresh"].State);
        Assert.Equal(PackageRowState.NotOffered, rows["future"].State);
        Assert.Contains("needs a newer kernel", rows["future"].Reason);
        Assert.Equal(PackageRowState.Available, rows["kept"].State); Assert.Equal("1.0.0", rows["kept"].Candidate);
        Assert.Contains("2.0.0 needs a newer kernel", rows["kept"].Reason);
        Assert.Equal(PackageRowState.Local, rows["local"].State);
        Assert.Equal(PackageRowState.Update, rows["zenith-kernel"].State); Assert.Equal("0.0.70", rows["zenith-kernel"].Candidate);
        Assert.Equal(PackageRowState.Current, PackageCatalogView.Rows(catalog, Repositories, installed, "0.0.70", "0.0.70").First().State);
    }

    [Fact]
    public void ReasonsNameConflictsOwnersAndMissingDependencies()
    {
        var installed = new[] { Installed("owner", "1.0.0") };
        var conflict = Entry("rival", "1.0.0", "conflicts\towner\n");
        Assert.Contains("conflicts with owner", PackageCatalogView.Reason(conflict, new[] { conflict }, installed));
        var missing = Entry("app", "1.0.0", "requires\tlib\t2.0\n");
        Assert.Contains("needs lib 2.0, which no source offers", PackageCatalogView.Reason(missing, new[] { missing }, installed));
    }

    [Fact]
    public void PlansShowWhatIsAlsoNeededSizesBlockersAndTheCommand()
    {
        var catalog = new[]
        {
            Entry("app", "1.0.0", "requires\tlib\t1.0.0\n", 3000), Entry("lib", "1.0.0", "", 2000), Entry("tool", "1.0.0"), Entry("tool", "1.1.0"),
            Entry("docs", "1.0.0"), Entry("docs", "2.0.0"),
            new RepositoryEntry("core", "kernel", "zenith-kernel", "0.0.70", 5000000, new string('1', 64), ""),
        };
        var installed = new[] { Installed("tool", "1.0.0"), Installed("docs", "1.0.0"), Installed("user", "1.0.0", "requires\tdocs\t>=1.0\n") };
        var cached = new HashSet<string> { catalog[1].Digest };
        var plan = PackageCatalogView.Plan(catalog, Repositories, installed, new[] { "app" }, Array.Empty<string>(), false, "0.0.69", "0.0.69", cached.Contains, 2);
        Assert.Empty(plan.Blockers);
        Assert.Contains(plan.Actions, a => a.Name == "app" && a.Verb == PlanVerb.Install && !a.AlsoNeeded);
        Assert.Contains(plan.Actions, a => a.Name == "lib" && a.AlsoNeeded);
        Assert.Equal(3000, plan.DownloadBytes);   // lib is downloaded already
        Assert.Equal("pkg install app", plan.Command);
        var all = PackageCatalogView.Plan(catalog, Repositories, installed, new[] { "tool", "docs" }, Array.Empty<string>(), true, "0.0.69", "0.0.69", _ => false, 2);
        Assert.Equal("pkg update", all.Command); Assert.True(all.Restart);
        var upgrades = PackageCatalogView.Plan(catalog, Repositories, installed, new[] { "tool", "docs" }, Array.Empty<string>(), false, "0.0.69", "0.0.69", _ => false, 2);
        Assert.Equal("pkg upgrade", upgrades.Command);
        var removal = PackageCatalogView.Plan(catalog, Repositories, installed, Array.Empty<string>(), new[] { "docs" }, false, "0.0.69", "0.0.69", _ => false, 2);
        Assert.Contains("docs is needed by user", Assert.Single(removal.Blockers));
        var both = PackageCatalogView.Plan(catalog, Repositories, installed, new[] { "app" }, new[] { "tool" }, false, "0.0.69", "0.0.69", _ => false, 2);
        Assert.Empty(both.Blockers); Assert.Equal("pkg remove tool\npkg install app", both.Command);
        Assert.Contains("No newer kernel", Assert.Single(PackageCatalogView.Plan(catalog, Repositories, installed, Array.Empty<string>(), Array.Empty<string>(), true, "0.0.70", "0.0.70", _ => false, 2).Blockers));
        Assert.NotEmpty(PackageCatalogView.Plan(catalog, Repositories, installed, new[] { "app", "app" }, Array.Empty<string>(), false, null, "0.0.0", _ => false, 0).Blockers);
    }

    [Fact]
    public void ChangingPackagesIsItsOwnGrant()
    {
        Assert.Equal(Privilege.Packages, ProcessPrivileges.For(668, 0));
        var portal = new AuthorizationPortal(_ => true, () => false, _ => "root");
        Assert.Equal(Privilege.Packages, portal.Action("org.zenith.packages.manage")!.Privilege);
        Assert.Contains(BootstrapComponents.All, c => c.Name == "zenith-native-package-center-abi" && c.Version == "1.0");
    }
}
