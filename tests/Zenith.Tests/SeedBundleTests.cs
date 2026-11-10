using System;
using System.IO;
using System.Linq;
using System.Text;
using Zenith.Core.Packages;

namespace Zenith.Tests;

public class SeedBundleTests
{
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----\n");
    private static readonly byte[] Ca = Encoding.ASCII.GetBytes("-----BEGIN CERTIFICATE-----\nBBBB\n-----END CERTIFICATE-----\n");

    private static byte[] Sample() => SeedBundle.Build("https://mirror.example/", new[]
    {
        (SeedKind.PackageKey, "package-key.pem", Key), (SeedKind.TlsCa, "tls-ca.pem", Ca),
        (SeedKind.Package, "zenith-settings.zpk", new byte[] { 1, 2, 3 }), (SeedKind.Package, "zenith-fonts.zpk", new byte[] { 4, 5 }),
    });

    [Fact]
    public void RoundTripsMirrorTrustAndPackagesInOrder()
    {
        byte[] bytes = Sample();
        var seed = SeedBundle.Parse(bytes);
        Assert.Equal("https://mirror.example", seed.Mirror);
        var key = seed.Single(SeedKind.PackageKey)!;
        Assert.Equal(Key, bytes.AsSpan(key.Offset, key.Length).ToArray());
        Assert.Equal(new[] { "zenith-settings.zpk", "zenith-fonts.zpk" }, seed.Packages().Select(p => p.Name));
        var fonts = seed.Packages().Last();
        Assert.Equal(new byte[] { 4, 5 }, bytes.AsSpan(fonts.Offset, fonts.Length).ToArray());
    }

    [Fact]
    public void TrustOnlySeedNeedsNoPackages()
    {
        var seed = SeedBundle.Parse(SeedBundle.Build(null, new[] { (SeedKind.TlsCa, "tls-ca.pem", Ca) }));
        Assert.Null(seed.Mirror); Assert.Empty(seed.Packages()); Assert.Null(seed.Single(SeedKind.PackageKey));
    }

    [Fact]
    public void RejectsCorruptionTruncationAndTrailingBytes()
    {
        byte[] bytes = Sample();
        byte[] flipped = (byte[])bytes.Clone(); flipped[^1] ^= 1;
        Assert.Contains("SHA256 mismatch", Assert.Throws<IOException>(() => SeedBundle.Parse(flipped)).Message);
        Assert.Contains("truncated", Assert.Throws<IOException>(() => SeedBundle.Parse(bytes.AsSpan(0, bytes.Length - 1))).Message);
        Assert.Contains("trailing", Assert.Throws<IOException>(() => SeedBundle.Parse(bytes.Concat(new byte[] { 0 }).ToArray())).Message);
        Assert.Throws<IOException>(() => SeedBundle.Parse(Encoding.ASCII.GetBytes("ZENITH-SEED\t2\nend\n")));
        Assert.Throws<IOException>(() => SeedBundle.Parse(Encoding.ASCII.GetBytes("ZENITH-SEED\t1\n")));
    }

    [Theory]
    [InlineData("mirror\tftp://x\n")]
    [InlineData("mirror\thttp://plain.example\n")]
    [InlineData("mirror\thttps://a\nmirror\thttps://b\n")]
    [InlineData("file\tscript\tx\t1\t" + "00000000000000000000000000000000000000000000000000000000000000ff" + "\n")]
    [InlineData("file\tpackage\t../x\t1\t" + "00000000000000000000000000000000000000000000000000000000000000ff" + "\n")]
    [InlineData("file\tpackage\tx\t-1\t" + "00000000000000000000000000000000000000000000000000000000000000ff" + "\n")]
    [InlineData("other\n")]
    public void RejectsMalformedHeaders(string lines)
        => Assert.Throws<IOException>(() => SeedBundle.Parse(Encoding.ASCII.GetBytes("ZENITH-SEED\t1\n" + lines + "end\n")));

    [Fact]
    public void PackagesRequireTheSeedKeyAndKindsAreSingle()
    {
        Assert.Contains("package key", Assert.Throws<IOException>(() => SeedBundle.Parse(SeedBundle.Build(null,
            new[] { (SeedKind.Package, "a.zpk", new byte[] { 1 }) }))).Message);
        Assert.Throws<IOException>(() => SeedBundle.Parse(SeedBundle.Build(null,
            new[] { (SeedKind.TlsCa, "a.pem", Ca), (SeedKind.TlsCa, "b.pem", Ca) })));
        Assert.Throws<IOException>(() => SeedBundle.Parse(SeedBundle.Build(null,
            new[] { (SeedKind.Package, "a.zpk", new byte[] { 1 }), (SeedKind.PackageKey, "k.pem", Key), (SeedKind.Package, "a.zpk", new byte[] { 2 }) })));
    }
}
