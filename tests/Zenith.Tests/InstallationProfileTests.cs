using System;
using Zenith.Core.Security;
using Zenith.Core.Storage;

namespace Zenith.Tests;

public sealed class InstallationProfileTests
{
    private sealed class MissingEntropy : IEntropySource
    {
        public bool TryFill(Span<byte> bytes) => false;
    }
    [Theory]
    [InlineData("zenith", "user")]
    [InlineData("desktop-01", "azazel")]
    public void AcceptsInitialIdentities(string host, string user) => InstallationProfile.ValidateNames(host, user);
    [Theory]
    [InlineData("bad\nhost", "user")]
    [InlineData("-host", "user")]
    [InlineData("host", "root")]
    [InlineData("host", "users")]
    [InlineData("host", "../user")]
    public void RejectsMalformedOrReservedIdentities(string host, string user)
        => Assert.Throws<ArgumentException>(() => InstallationProfile.ValidateNames(host, user));
    [Fact]
    public void CredentialsRejectMismatchOrMissingEntropyBeforeReturningInstallProfile()
    {
        Assert.Throws<ArgumentException>(() => InstallationProfile.Create("zenith", "user", "one", "two", "user", "user", new MissingEntropy()));
        Assert.Throws<InvalidOperationException>(() => InstallationProfile.Create("zenith", "user", "one", "one", "user", "user", new MissingEntropy()));
    }
}
