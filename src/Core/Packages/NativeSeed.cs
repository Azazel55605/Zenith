// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Zenith.Core.Boot;
using Zenith.Core.Network;
using Zenith.Core.Shell.Commands;
using Zenith.Core.Storage;

namespace Zenith.Core.Packages;

/// <summary>
/// The installer seed (<see cref="SeedBundle"/>) on a live ISO boot. At boot it pins the seed's
/// package key, TLS CA and mirror in the live <c>/etc</c>, which Sunrise copies into the installed
/// root; Sunrise then installs the seed's signed packages into the target. Nothing happens on an
/// installed root or a plain ISO.
/// </summary>
internal static class NativeSeed
{
    private static SeedBundle? s_bundle;
    private static bool s_loaded;
    public static string? Error { get; private set; }

    /// <summary>The parsed seed of this boot, or null (none, or invalid: see <see cref="Error"/>).</summary>
    public static SeedBundle? Bundle
    {
        get
        {
            if (s_loaded) { return s_bundle; }
            s_loaded = true;
            try
            {
                var bytes = NativeBootAssets.Seed();
                if (!bytes.IsEmpty) { s_bundle = SeedBundle.Parse(bytes); }
            }
            catch (Exception e) { Error = e.Message; Log.Write("seed", "installer seed ignored: " + e.Message); }
            return s_bundle;
        }
    }

    public static byte[] Read(SeedFile file) => NativeBootAssets.Seed().Slice(file.Offset, file.Length).ToArray();

    /// <summary>On a live boot, after the network service is up: trust what the ISO carries.</summary>
    public static void ApplyLive()
    {
        if (SystemMounts.Mode != BootMode.Live) { return; }
        var seed = Bundle;
        if (seed is null) { return; }
        // Each part on its own: a CA the TLS stack refuses must not also cost the package key.
        var parts = new List<string>();
        void Part(string label, Action apply)
        {
            try { apply(); parts.Add(label); }
            catch (Exception e) { Error = label + ": " + e.Message; Log.Write("seed", "installer seed " + label + " not applied: " + e.Message); }
        }
        if (seed.Single(SeedKind.PackageKey) is { } key) { Part("package key " + key.Sha256[..16], () => PackageCommands.PinKey(Read(key))); }
        if (seed.Single(SeedKind.TlsCa) is { } ca) { TrustCaWhenIdle(ca); }
        if (seed.Mirror is { } mirror) { Part("mirror " + mirror, () => NativeRepositories.Mirror(mirror)); }
        int packages = 0; foreach (var _ in seed.Packages()) { packages++; }
        Log.Write("seed", "installer seed applied: " + string.Join(", ", parts) + "; packages=" + packages);
    }

    /// <summary>The TLS stack is one shared native context: importing the CA waits for the boot's own
    /// network work (DHCP, NTP) to finish, on its own thread so boot does not wait.</summary>
    private static void TrustCaWhenIdle(SeedFile ca)
    {
        byte[] pem = Read(ca);
        var worker = new System.Threading.Thread(() =>
        {
            using var root = Security.CredentialContext.Enter(Security.Credentials.Root);
            string staged = "/tmp/seed-tls-ca.pem";
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.WriteAllBytes(staged, pem);
                    NetworkService.Trust(staged, ca.Sha256, () => { });
                    File.Delete(staged);
                    Log.Write("seed", "installer seed TLS CA " + ca.Sha256[..16] + " applied");
                    return;
                }
                catch (Exception e)
                {
                    try { if (File.Exists(staged)) { File.Delete(staged); } } catch { }
                    if (e.Message.StartsWith("network busy", StringComparison.Ordinal) && attempt < 240)
                    { System.Threading.Thread.Sleep(250); continue; }
                    Error = "TLS CA: " + e.Message;
                    Log.Write("seed", "installer seed TLS CA " + ca.Sha256[..16] + " not applied: " + e.Message);
                    return;
                }
            }
        });
        worker.Start();
    }

    /// <summary>Sunrise's network check: fetch the core index from the seeded mirror over verified
    /// HTTPS and check its signature with the seeded key. Returns a line for the review; throws on failure.</summary>
    public static string CheckMirror(Action cancel)
    {
        var seed = Bundle;
        if (seed?.Mirror is not { } mirror || seed.Single(SeedKind.PackageKey) is not { } keyFile) { return "no mirror on this ISO"; }
        string staged = "/tmp/sunrise-index.zri"; byte[] bytes;
        try
        {
            try { NetworkService.Fetch(mirror + "/core/index-v3.zri", staged, true, cancel, _ => { }, RepositoryIndex.MaximumBytes); }
            catch (Exception error) when (error.Message == "HTTP status 404")
            { NetworkService.Fetch(mirror + "/core/index.zri", staged, true, cancel, _ => { }, RepositoryIndex.MaximumBytes); }
            bytes = File.ReadAllBytes(staged);
        }
        finally { if (File.Exists(staged)) { File.Delete(staged); } }
        var index = RepositoryIndex.Read(bytes, Read(keyFile), new NativePackageVerifier(), "core");
        index.Fresh((Time.SystemClock.UtcNow.Ticks - new DateTime(1970, 1, 1).Ticks) / TimeSpan.TicksPerSecond);
        return "mirror reachable, signed core index verified";
    }

    /// <summary>The seed's packages, each verified against the seed's key. Throws on the first defect.</summary>
    public static List<SignedPackage> Packages()
    {
        var seed = Bundle; var packages = new List<SignedPackage>();
        if (seed is null || seed.Single(SeedKind.PackageKey) is not { } keyFile) { return packages; }
        byte[] key = Read(keyFile); var verifier = new NativePackageVerifier();
        foreach (var file in seed.Packages())
        {
            try { packages.Add(SignedPackage.Read(Read(file), key, verifier)); }
            catch (Exception e) { throw new IOException("seed package " + file.Name + ": " + e.Message); }
        }
        return packages;
    }

    /// <summary>A line per package ("name version") for the review, after checking the set installs
    /// on an empty root (signatures, dependencies, file conflicts).</summary>
    public static List<string> Check()
    {
        var packages = Packages(); var lines = new List<string>(); var set = new List<InstalledPackage>();
        foreach (var package in packages)
        {
            lines.Add(package.Manifest.Name + " " + package.Manifest.Version);
            set.Add(new InstalledPackage(package.ManifestText, package.ArchiveDigest, package.KeyId));
        }
        if (set.Count > 0) { PackageManager.ValidateSet(set); }
        return lines;
    }

    /// <summary>Installs the seed's packages into the root mounted at <paramref name="root"/>, as one transaction.</summary>
    public static int InstallInto(string root, Action<string> progress)
    {
        var packages = Packages();
        if (packages.Count == 0) { return 0; }
        var manager = new PackageManager(new NativePackageStore(root));
        manager.Install(packages, () => { }, step => progress("Installing apps: " + step));
        return packages.Count;
    }
}
