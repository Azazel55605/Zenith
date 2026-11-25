using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Zenith.Core.Packages;

namespace Zenith.Tests;

public class PackageTests
{
    private const string Target = "/usr/share/doc/example.txt";
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("hello\n");
    private static string Text(string target = Target) => "ZENITH-PACKAGE\t1\nname\thello-docs\nversion\t1.0.0\narchitecture\tany\nkind\tdata\nruntime\tbootstrap-1\nrequires\tzenith-base\t0.1\nfile\t" + target + "\t0644\t6\t" + PackagePlanner.Digest(Data) + "\n";
    private sealed class Source(byte[] bytes) : IPackageSource { public byte[] Read(PackageFile file) => bytes; }
    private static PackagePlan Plan(string text, Func<string, PackagePathState>? state = null, byte[]? data = null) =>
        PackagePlanner.Plan(PackageManifest.Parse(text), new Source(data ?? Data), "x86_64", BootstrapComponents.All, state ?? (_ => PackagePathState.Missing));

    [Fact]
    public void X5PackageUsesTheNewCapabilityAndBoundaries()
    {
        var payload = new byte[PackageManifest.LegacyMaximumFileBytes + 1];
        string digest = PackagePlanner.Digest(payload);
        string manifest = Text().Replace("\t0644\t6\t" + PackagePlanner.Digest(Data),
            $"\t0644\t{payload.Length}\t{digest}");
        Assert.Throws<FormatException>(() => PackageManifest.Parse(manifest));
        manifest += $"requires\t{PackageManifest.LargeNativeCapability}\t1.0\n";
        var parsed = PackageManifest.Parse(manifest);
        var old = BootstrapComponents.All.Where(c => c.Name != PackageManifest.LargeNativeCapability).ToArray();
        var plan = PackagePlanner.Plan(parsed, new Source(payload), "x86_64", old, _ => PackagePathState.Missing);
        Assert.Contains(PackageManifest.LargeNativeCapability, Assert.Single(plan.Blockers));
        Assert.True(Plan(manifest, data: payload).Applicable);

        string files = manifest[..manifest.IndexOf("file\t", StringComparison.Ordinal)] + $"requires\t{PackageManifest.LargeNativeCapability}\t1.0\n";
        for (int i = 0; i < 8; ++i) { files += $"file\t/usr/share/doc/x5-{i}\t0644\t{PackageManifest.MaximumFileBytes}\t{digest}\n"; }
        Assert.Equal(PackageManifest.MaximumPayloadBytes, PackageManifest.Parse(files).PayloadBytes);
        Assert.Throws<FormatException>(() => PackageManifest.Parse(files + $"file\t/usr/share/doc/x5-over\t0644\t1\t{digest}\n"));
        PackageManager.CheckTransactionBytes(64 * 1024 * 1024);
        Assert.Throws<IOException>(() => PackageManager.CheckTransactionBytes(64 * 1024 * 1024 + 1));
    }
    [Fact]
    public void FilesystemCapabilityBlocksInstallationOnOldKernels()
    {
        string text = Text().Replace("requires\tzenith-base\t0.1\n", "requires\tzenith-base\t0.1\nrequires\tzenith-native-fs-abi\t1.0\n");
        var old = BootstrapComponents.All.Where(c => c.Name != "zenith-native-fs-abi").ToArray();
        var plan = PackagePlanner.Plan(PackageManifest.Parse(text), new Source(Data), "x86_64", old, _ => PackagePathState.Missing);
        Assert.False(plan.Applicable); Assert.Contains("zenith-native-fs-abi", Assert.Single(plan.Blockers));
        Assert.True(Plan(text).Applicable);
    }
    [Theory]
    [InlineData("zenith-net-tools", "zenith-native-network-abi", "zenith-native-web-transport-abi")]
    [InlineData("zenith-settings", "zenith-native-settings-abi", BootstrapComponents.LockPreferences)]
    [InlineData("zenith-orbit", "zenith-native-package-center-abi", BootstrapComponents.ScheduledUpdates)]
    public void AddedServiceRequirementBlocksOnlyTheUpdatedClientOnAnOlderKernel(string client, string previous, string capability)
    {
        string original = Text().Replace("hello-docs", client) + $"requires\t{previous}\t1.0\n";
        string updated = original + $"requires\t{capability}\t1.0\n";
        var old = BootstrapComponents.All.Where(c => c.Name != capability).ToArray();
        PackagePlan OldPlan(string manifest) => PackagePlanner.Plan(PackageManifest.Parse(manifest),
            new Source(Data), "x86_64", old, _ => PackagePathState.Missing);

        Assert.True(OldPlan(original).Applicable);
        Assert.Equal("missing exact dependency " + capability + "=1.0", Assert.Single(OldPlan(updated).Blockers));
        Assert.True(Plan(original).Applicable);
        Assert.True(Plan(updated).Applicable);
    }
    [Fact]
    public void RoundTripAndPlanVerifyPayloadWithoutMutating()
    {
        var manifest = PackageManifest.Parse(Text());
        Assert.Equal("hello-docs", manifest.Name); Assert.Equal(6, manifest.PayloadBytes); Assert.Single(manifest.Files);
        var plan = Plan(Text()); Assert.True(plan.Applicable); Assert.Single(plan.Actions); Assert.Empty(plan.Blockers);
        Assert.Equal("5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03", PackagePlanner.Digest(Data));
    }
    [Theory]
    [InlineData("/etc/shadow")]
    [InlineData("/home/user/example.txt")]
    [InlineData("/boot/kernel.elf")]
    [InlineData("/usr/share/doc/../../root/secret")]
    [InlineData("/usr/share/doc//example.txt")]
    [InlineData("/usr/share/doc/./example.txt")]
    [InlineData("/usr/share/doc/example\\evil")]
    [InlineData("/usr/share/doc/example/")]
    [InlineData("/usr/share/zenith/components.tsv")]
    [InlineData("/usr/share/zenith/components.tsv/child")]
    public void UnsafeOrMutablePathsAreRefused(string path) => Assert.Throws<FormatException>(() => PackageManifest.Parse(Text(path)));
    [Theory]
    [InlineData("ZENITH-PACKAGE\t1", "ZENITH-PACKAGE\t2")]
    [InlineData("kind\tdata", "kind\tsystem")]
    [InlineData("version\t1.0.0", "version\t01.0.0")]
    [InlineData("\t0644\t6", "\t4755\t6")]
    [InlineData("\t0644\t6", "\t0644\t06")]
    [InlineData("\t0644\t6", "\t0644\t262145")]
    [InlineData("architecture\tany", "architecture\tx86")]
    public void UnknownFormatsAndInvalidRecordsAreRefused(string old, string replacement) =>
        Assert.Throws<FormatException>(() => PackageManifest.Parse(Text().Replace(old, replacement)));
    [Fact]
    public void DuplicateFieldsFilesDependenciesAndParentFilesAreRefused()
    {
        Assert.Throws<FormatException>(() => PackageManifest.Parse(Text() + "name\tx\n"));
        Assert.Throws<FormatException>(() => PackageManifest.Parse(Text() + Text().Split('\n')[7] + "\n"));
        Assert.Throws<FormatException>(() => PackageManifest.Parse(Text() + "requires\tzenith-base\t0.1\n"));
        Assert.Throws<FormatException>(() => PackageManifest.Parse(Text() + "file\t" + Target + "/child\t0644\t6\t" + PackagePlanner.Digest(Data) + "\n"));
    }
    [Fact]
    public void IntegrityMismatchIsFatalRatherThanAnApplicablePlan()
    {
        Assert.Throws<IOException>(() => Plan(Text(), data: Encoding.UTF8.GetBytes("HELLO\n")));
        Assert.Throws<IOException>(() => Plan(Text(), data: Array.Empty<byte>()));
    }
    [Theory]
    [InlineData("architecture\tany", "architecture\tarm64", "architecture mismatch")]
    [InlineData("runtime\tbootstrap-1", "runtime\tunknown", "runtime compatibility mismatch")]
    [InlineData("requires\tzenith-base\t0.1", "requires\tzenith-base\t0.2", "missing exact dependency")]
    [InlineData("name\thello-docs", "name\tzenith-base", "reserved bundled")]
    public void CompatibilityAndDependenciesBlockPlans(string old, string replacement, string expected)
    {
        var plan = Plan(Text().Replace(old, replacement)); Assert.False(plan.Applicable); Assert.Contains(expected, Assert.Single(plan.Blockers));
    }
    [Theory]
    [InlineData(Target, (int)PackagePathState.Occupied)]
    [InlineData(Target, (int)PackagePathState.Directory)]
    [InlineData("/usr/share/doc", (int)PackagePathState.Occupied)]
    public void UnownedFilesDirectoriesAndAncestorLinksBlock(string path, int state)
    {
        var plan = Plan(Text(), value => value == path ? (PackagePathState)state : PackagePathState.Missing);
        Assert.False(plan.Applicable); Assert.Empty(plan.Actions); Assert.Contains("unowned", Assert.Single(plan.Blockers));
    }
    [Fact]
    public void ExistingParentDirectoriesAreUsableAndManifestIsBounded()
    {
        Assert.True(Plan(Text(), path => path == Target ? PackagePathState.Missing : PackagePathState.Directory).Applicable);
        Assert.Throws<FormatException>(() => PackageManifest.Parse(new string('x', PackageManifest.MaximumBytes + 1)));
        Assert.Throws<FormatException>(() => PackageManifest.Parse(Text().Replace("hello-docs", "hello\0docs")));
    }
}
