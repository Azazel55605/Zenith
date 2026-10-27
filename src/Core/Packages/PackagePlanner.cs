using System;
using System.Collections.Generic;
using System.IO;
using Zenith.Core.Security;

namespace Zenith.Core.Packages;

internal interface IPackageSource
{
    byte[] Read(PackageFile file);
}
internal enum PackagePathState { Missing, Directory, Occupied }
internal sealed record BundledComponent(string Name, string Version, string Activation);
internal static class BootstrapComponents
{
    // Inventory of the current image, not proof of independently installed packages.
    public static readonly BundledComponent[] All =
    {
        new("zenith-kernel", "0.1", "kernel-linked; reboot"),
        new("zenith-base", "0.1", "kernel-linked; reboot"),
        new("base-tools", "0.1", "bundled shell/text tools; reboot"),
        new("net-tools", "0.1", "bundled IPv4 diagnostics; reboot"),
        new("fs-tools", "0.1", "bundled filesystem/account tools; reboot"),
        new("zenith-desktop-test", "0.1", "kernel-linked test DE; reboot"),
        new("zenith-package-meta-abi", "1.0", "dependency-only package format 3"),
        new("zenith-appearance-abi", "1.0", "declarative appearance resources"),
        new("zenith-session-host", "1.0", "trusted login/session host; kernel-linked"),
#if !ARCH_ARM64
        new("zenith-native-fs-abi", "1.0", "read-only native filesystem calls"),
        new("zenith-native-settings-abi", "1.0", "typed system appearance settings with revision checks"),
        new("zenith-native-interactive-abi", "1.0", "owned live-window quota renewal and bounded revocation cleanup"),
        new("zenith-native-system-info-abi", "1.0", "bounded read-only running system metadata"),
        new("zenith-native-window-state-abi", "1.0", "owned unsaved state and cancellable chrome close intent"),
        new("zenith-native-graphics-abi", "1.0", "owned private graphical surfaces and routed events"),
        new("zenith-native-network-abi", "1.0", "owned bounded TCP/verified-TLS clients and atomic download sinks"),
        new("zenith-native-path-abi", "1.0", "owned namespace/metadata operations and private descriptor seeks"),
        new("zenith-native-process-abi", "1.0", "exported environment and owned process lifecycle"),
        new("zenith-native-terminal-abi", "1.0", "foreground raw terminal modes and automatic restoration"),
        new("zenith-native-stream-abi", "1.0", "live standard streams and bounded byte pipelines"),
        new("zenith-native-io-abi", "1.0", "bounded native stdin and regular-file writes"),
        new("zenith-native-abi", "1.0", "x86_64 foreground process ABI; kernel capability"),
#endif
    };
}
internal sealed record PackagePlan(IReadOnlyList<string> Actions, IReadOnlyList<string> Blockers, int PayloadBytes)
{
    public bool Applicable => Blockers.Count == 0;
}

internal static class PackagePlanner
{
    public static string Digest(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[32]; Sha256.Hash(bytes, digest);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
    public static PackagePlan Plan(PackageManifest manifest, IPackageSource source, string architecture,
        IReadOnlyList<BundledComponent> components, Func<string, PackagePathState> state)
    {
        var actions = new List<string>(); var blockers = new List<string>();
        if (manifest.Architecture != "any" && manifest.Architecture != architecture) { blockers.Add("architecture mismatch"); }
        if (manifest.RuntimeId != PackageManifest.Runtime) { blockers.Add("runtime compatibility mismatch"); }
        foreach (var component in components)
        { if (component.Name == manifest.Name) { blockers.Add("reserved bundled component identity: " + manifest.Name); } }
        foreach (var dependency in manifest.Dependencies)
        {
            bool found = false;
            foreach (var component in components)
            { if (component.Name == dependency.Name && PackageVersions.Matches(component.Version, dependency.Version)) { found = true; break; } }
            if (!found) { blockers.Add("missing exact dependency " + dependency.Name + "=" + dependency.Version); }
        }
        foreach (string conflict in manifest.Conflicts)
        { foreach (var component in components) { if (component.Name == conflict) { blockers.Add("declared conflict: " + conflict); } } }
        foreach (var file in manifest.Files)
        {
            byte[] bytes = source.Read(file);
            if (bytes.Length != file.Length || Digest(bytes) != file.Digest) { throw new IOException("payload integrity mismatch: " + file.Path); }
            bool conflict = state(file.Path) != PackagePathState.Missing;
            for (int slash = file.Path.IndexOf('/', 1); slash >= 0; slash = file.Path.IndexOf('/', slash + 1))
            {
                if (state(file.Path.Substring(0, slash)) == PackagePathState.Occupied) { conflict = true; }
            }
            if (conflict) { blockers.Add("unowned path conflict: " + file.Path); }
            else { actions.Add("create " + file.Path + " mode=" + (file.Mode == 0x1a4 ? "0644" : "0755") + " bytes=" + file.Length); }
        }
        return new(actions.AsReadOnly(), blockers.AsReadOnly(), manifest.PayloadBytes);
    }
}
