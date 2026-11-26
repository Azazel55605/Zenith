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
    public const string LockPreferences = "zenith-native-lock-preferences-abi";
    public const string ScheduledUpdates = "zenith-native-update-schedule-abi";

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
        new("zenith-native-terminal-session-abi", "1.0", "kernel-owned shell sessions for terminal clients"),
        new(PackageManifest.LargeNativeCapability, "1.0", "large native images, packages and bounded file reads"),
        new("zenith-native-memory-abi", "1.0", "program images up to 32 MiB of address space"),
        new("zenith-native-framebuffer-abi", "1.0", "on-demand private framebuffer and resource-safe resize refusal"),
        new("zenith-native-display-abi", "1.0", "host-owned primary display preview, rollback and integer scale"),
        new("zenith-native-settings-abi", "1.0", "typed system appearance settings with revision checks"),
        new("zenith-native-interactive-abi", "1.0", "owned live-window quota renewal and bounded revocation cleanup"),
        new("zenith-native-system-info-abi", "1.0", "bounded read-only running system metadata"),
        new("zenith-native-personal-appearance-abi", "1.0", "captured-UID personal overrides and system inheritance"),
        new("zenith-native-shared-settings-abi", "1.0", "session defaults and bounded public network/account information"),
        new("zenith-native-material-control-abi", "1.0", "revisioned system-wide translucency strength"),
        new("zenith-native-system-settings-abi", "1.0", "typed administrator keyboard and time-zone preferences"),
        new("zenith-native-background-abi", "1.0", "versioned whole-background preferences for opted-in clients"),
        new("zenith-native-monitor-abi", "1.0", "CPU, memory and frame samples, the caller's processes and owner-authorized stop"),
        new("zenith-native-disk-abi", "1.0", "disks, partitions and free space; root-only background partition edits"),
        new("zenith-native-portal-abi", "1.0", "Desktop Portal requests (650-653): org.zenith.portal.Authorization grants until exit"),
        new("zenith-native-files-abi", "1.0", "File listing with times, free space and copy/move/delete jobs (654-658), waiting on a job from kernel 0.0.59; right-click and F-key input (627 bits 2/4)"),
        new("zenith-native-archive-abi", "1.0", "archive jobs (656 operations 4 extract and 5 compress: zip, tar, tar.gz, gz) and image thumbnails (660) (kernel 0.0.65)"),
        new("zenith-native-open-with-abi", "1.0", "portal extended payloads, org.zenith.portal.OpenWith and the org.zenith.files.manage action (kernel 0.0.65)"),
        new("zenith-native-network-center-abi", "1.0", "network status, traffic and event history, diagnostics, ping, safe apply and connect/disconnect (667) (kernel 0.0.69)"),
        new("zenith-native-package-center-abi", "1.0", "Orbit's package service: overview, packages with reasons, details from the signed package, plans, apply and refresh jobs, history and sources (668) (kernel 0.0.70)"),
        new(ScheduledUpdates, "1.0", "Read the scheduled package check preference and change it using its observed revision (668)"),
        new("zenith-native-desktop-preferences-abi", "1.0", "Revisioned panel, window, task switcher and workspace preferences (672)"),
        new(LockPreferences, "1.0", "Revisioned lock screen preferences and per-user recent Settings pages (672)"),
        new("zenith-native-notifications-abi", "1.1", "Post desktop notifications; buttons (event 13), per-sender switches, Do Not Disturb and popup time (call 670)"),
        new("zenith-native-notification-preferences-abi", "1.0", "Revisioned notification visibility, corner, hover and sound preferences (call 670, v2 settings packets)"),
        new("zenith-native-style-catalog-abi", "1.0", "Meridian's pickers: every choice of a setting and previews drawn by the desktop, system sound level, switches and preview (669) (kernel 0.0.72)"),
        new("zenith-native-clipboard-abi", "1.0", "window clipboard: 639 copies from the focused window, 659 pastes once per Ctrl+V (kernel 0.0.64)"),
        new("zenith-native-viewport-abi", "1.0", "owned bounded resize/maximize with versioned acknowledgement"),
        new("zenith-native-window-state-abi", "1.0", "owned unsaved state and cancellable chrome close intent"),
        new("zenith-native-graphics-abi", "1.0", "owned private graphical surfaces and routed events"),
        new("zenith-native-key-edges-abi", "1.0", "opt-in focused physical keyboard press and release events (671)"),
        new("zenith-native-transfer-abi", "1.0", "known-size download space checks, bounded bulk sends, entropy and private credential-file reads (500)"),
        new("zenith-native-network-abi", "1.0", "owned bounded TCP/verified-TLS clients and atomic download sinks"),
        new("zenith-native-web-transport-abi", "1.0", "public and configured TLS roots and up to six sequential client connections"),
        new("zenith-native-path-abi", "1.0", "owned namespace/metadata operations and private descriptor seeks"),
        new("zenith-native-process-abi", "1.0", "exported environment and owned process lifecycle"),
        new("zenith-native-audio-abi", "1.0", "HDA playback, permissioned capture and session mixer"),
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
