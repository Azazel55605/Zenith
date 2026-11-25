using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Zenith.Core.Packages;

internal enum PackageRowState { Current = 0, Update = 1, Available = 2, NotOffered = 3, Local = 4 }

/// <summary>One package as Orbit lists it: installed, offered and why not.</summary>
internal sealed record PackageRow(string Name, string Kind, string Repository, string? Installed, string? Candidate,
    string? Latest, PackageRowState State, int Bytes, int PayloadBytes, int Files, bool Programs, string Reason);

internal enum PlanVerb { Install, Upgrade, Remove, Kernel }

internal sealed record PlanAction(PlanVerb Verb, string Name, string? From, string To, string Repository, int Bytes, bool AlsoNeeded);

/// <summary>A visible plan: what changes, what else is needed, sizes, whether a restart is needed, and the exact
/// pkg commands. Blockers say why it cannot run as chosen.</summary>
internal sealed class ChangePlan
{
    public List<PlanAction> Actions { get; } = new();
    public List<string> Blockers { get; } = new();
    public List<RepositoryEntry> Downloads { get; } = new();
    public List<string> Removals { get; } = new();
    public RepositoryEntry? Kernel { get; set; }
    public long DownloadBytes { get; set; }
    public long DiskChange { get; set; }
    public bool Restart => Kernel is not null;
    public string Command { get; set; } = "";
    public bool Empty => Actions.Count == 0;
}

/// <summary>Orbit's view of pkg (kernel 0.0.70): pure, so the host tests cover states, reasons and plans. The same
/// planner (<see cref="RepositoryPlanner"/>) and set rules (<see cref="PackageManager.ValidateSet"/>) as the shell.</summary>
internal static class PackageCatalogView
{
    public static (int Count, string Key) UpdateOffers(IEnumerable<PackageRow> rows)
    {
        var offers = new List<string>();
        foreach (var row in rows)
        {
            if (row.State == PackageRowState.Update) { offers.Add(row.Name + "=" + row.Candidate); }
        }
        offers.Sort(StringComparer.Ordinal);
        return (offers.Count, string.Join("\n", offers));
    }

    private static int Newest(RepositoryEntry a, RepositoryEntry b) => PackageVersions.Compare(b.Version, a.Version);

    public static List<PackageRow> Rows(IReadOnlyList<RepositoryEntry> catalog, IReadOnlyList<PackageRepository> repositories,
        IReadOnlyList<InstalledPackage> installed, string? runningKernel, string kernelFloor, Action? cancel = null)
    {
        var rows = new List<PackageRow>();
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in catalog) { if (entry.Kind == "data") { names.Add(entry.Name); } }
        foreach (var item in installed) { names.Add(item.Manifest.Name); }
        foreach (string name in names)
        {
            cancel?.Invoke();
            var mine = new List<RepositoryEntry>();
            foreach (var entry in catalog) { if (entry.Kind == "data" && entry.Name == name) { mine.Add(entry); } }
            mine.Sort(Newest);
            InstalledPackage? current = null;
            foreach (var item in installed) { if (item.Manifest.Name == name) { current = item; } }
            var latest = mine.Count > 0 ? mine[0] : null;
            if (current is not null && (latest is null || PackageVersions.Compare(latest.Version, current.Manifest.Version) <= 0))
            {
                var m = current.Manifest;
                rows.Add(new(name, m.Kind, m.Repository, m.Version, null, latest?.Version, latest is null ? PackageRowState.Local : PackageRowState.Current,
                    0, m.PayloadBytes, m.Files.Count, Programs(m), latest is null ? "Not offered by any source; installed from a file" : ""));
                continue;
            }
            // A newer version (or any, when not installed) exists: is one installable now?
            RepositoryEntry? candidate = null; string reason = "";
            try
            {
                foreach (var choice in RepositoryPlanner.Plan(catalog, repositories, installed, new[] { name }, cancel))
                { if (choice.Name == name) { candidate = choice; } }
            }
            catch (IOException) { }
            if (candidate is not null && current is not null && PackageVersions.Compare(candidate.Version, current.Manifest.Version) <= 0) { candidate = null; }
            if (candidate is null || candidate.Version != latest!.Version) { reason = Reason(latest!, catalog, installed); }
            var shown = candidate ?? latest!;
            var manifest = shown.Manifest;
            var state = candidate is null ? PackageRowState.NotOffered : current is null ? PackageRowState.Available : PackageRowState.Update;
            rows.Add(new(name, shown.Manifest.Kind, shown.Repository, current?.Manifest.Version, candidate?.Version, latest!.Version, state,
                shown.Bytes, manifest.PayloadBytes, manifest.Files.Count, Programs(manifest), reason));
        }
        if (runningKernel is not null)
        {
            RepositoryEntry? kernel = NewestKernel(catalog, kernelFloor);
            rows.Insert(0, new("zenith-kernel", "kernel", "core", runningKernel, kernel?.Version, kernel?.Version,
                kernel is null ? PackageRowState.Current : PackageRowState.Update, kernel?.Bytes ?? 0, 0, 1, true, ""));
        }
        return rows;
    }

    public static RepositoryEntry? NewestKernel(IReadOnlyList<RepositoryEntry> catalog, string floor)
    {
        RepositoryEntry? best = null;
        foreach (var entry in catalog)
        {
            if (entry.Kind != "kernel" || entry.Repository != "core" || entry.Name != "zenith-kernel" || !PackageVersions.Valid(floor)
                || PackageVersions.Compare(entry.Version, floor) <= 0) { continue; }
            if (best is null || PackageVersions.Compare(entry.Version, best.Version) > 0) { best = entry; }
        }
        return best;
    }

    private static bool Has(IReadOnlyList<string> list, string value) { foreach (string item in list) { if (item == value) { return true; } } return false; }
    public static bool Programs(PackageManifest manifest)
    {
        foreach (var file in manifest.Files) { if (file.Mode == 0x1ed && file.Path.EndsWith(".elf", StringComparison.Ordinal)) { return true; } }
        return false;
    }

    /// <summary>Why this version cannot be installed now, in plain words.</summary>
    public static string Reason(RepositoryEntry entry, IReadOnlyList<RepositoryEntry> catalog, IReadOnlyList<InstalledPackage> installed)
    {
        var manifest = entry.Manifest;
        foreach (var need in manifest.Dependencies)
        {
            BundledComponent? bundled = null;
            foreach (var component in BootstrapComponents.All) { if (component.Name == need.Name) { bundled = component; } }
            if (bundled is not null)
            {
                if (!PackageVersions.Matches(bundled.Version, need.Version))
                { return entry.Version + " needs " + need.Name + " " + need.Version + "; this kernel has " + bundled.Version; }
                continue;
            }
            if (need.Name.EndsWith("-abi", StringComparison.Ordinal) || need.Name.StartsWith("zenith-native-", StringComparison.Ordinal))
            { return entry.Version + " needs a newer kernel than this one (" + need.Name + " " + need.Version + ")"; }
            bool offered = false;
            foreach (var other in catalog) { if (other.Kind == "data" && other.Name == need.Name && PackageVersions.Matches(other.Version, need.Version)) { offered = true; } }
            foreach (var item in installed) { if (item.Manifest.Name == need.Name && PackageVersions.Matches(item.Manifest.Version, need.Version)) { offered = true; } }
            if (!offered) { return entry.Version + " needs " + need.Name + " " + need.Version + ", which no source offers"; }
        }
        foreach (var item in installed)
        {
            var other = item.Manifest;
            if (other.Name == manifest.Name) { continue; }
            if (Has(manifest.Conflicts, other.Name) || Has(other.Conflicts, manifest.Name)) { return entry.Version + " conflicts with " + other.Name + ", which is installed"; }
            foreach (var file in manifest.Files)
            { foreach (var owned in other.Files) { if (owned.Path == file.Path) { return entry.Version + " carries " + file.Path + ", which belongs to " + other.Name; } } }
            foreach (var need in other.Dependencies)
            {
                if (need.Name == manifest.Name && !PackageVersions.Matches(entry.Version, need.Version))
                { return other.Name + " needs " + manifest.Name + " " + need.Version; }
            }
        }
        return entry.Version + " cannot be combined with what is installed";
    }

    /// <summary>The plan for the chosen changes. <paramref name="cached"/> says whether an archive is downloaded already.</summary>
    public static ChangePlan Plan(IReadOnlyList<RepositoryEntry> catalog, IReadOnlyList<PackageRepository> repositories,
        IReadOnlyList<InstalledPackage> installed, IReadOnlyList<string> install, IReadOnlyList<string> remove, bool kernel,
        string? runningKernel, string kernelFloor, Func<string, bool> cached, int availableUpgrades, Action? cancel = null)
    {
        var plan = new ChangePlan();
        var installedNames = new Dictionary<string, InstalledPackage>(StringComparer.Ordinal);
        foreach (var item in installed) { installedNames[item.Manifest.Name] = item; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in install) { if (!PackageManifest.ValidName(name) || !seen.Add(name)) { plan.Blockers.Add("Each package can be chosen once: " + name); } }
        foreach (string name in remove) { if (!PackageManifest.ValidName(name) || !seen.Add(name)) { plan.Blockers.Add("Each package can be chosen once: " + name); } }
        if (install.Count > 32 || remove.Count > 16) { plan.Blockers.Add("At most 32 packages to install and 16 to remove at once"); }
        if (plan.Blockers.Count > 0) { return plan; }
        var remaining = new List<InstalledPackage>();
        foreach (string name in remove)
        {
            if (!installedNames.TryGetValue(name, out var item)) { plan.Blockers.Add(name + " is not installed"); continue; }
            var m = item.Manifest;
            plan.Removals.Add(name);
            plan.Actions.Add(new(PlanVerb.Remove, name, m.Version, m.Version, m.Repository, 0, false));
            plan.DiskChange -= m.PayloadBytes;
        }
        foreach (var item in installed) { if (!plan.Removals.Contains(item.Manifest.Name)) { remaining.Add(item); } }
        // Removing something another installed package needs is refused, with who needs it.
        foreach (string name in plan.Removals)
        {
            var needers = new List<string>();
            foreach (var item in remaining)
            {
                bool replaced = false;
                foreach (string chosen in install) { if (chosen == item.Manifest.Name) { replaced = true; } }
                foreach (var need in item.Manifest.Dependencies) { if (need.Name == name && !replaced) { needers.Add(item.Manifest.Name); } }
            }
            if (needers.Count > 0) { plan.Blockers.Add(name + " is needed by " + string.Join(", ", needers)); }
        }
        if (install.Count > 0)
        {
            IReadOnlyList<RepositoryEntry> chosen = Array.Empty<RepositoryEntry>();
            try { chosen = RepositoryPlanner.Plan(catalog, repositories, remaining, install, cancel); }
            catch (IOException)
            {
                foreach (string name in install)
                {
                    RepositoryEntry? latest = null;
                    foreach (var entry in catalog) { if (entry.Kind == "data" && entry.Name == name && (latest is null || PackageVersions.Compare(entry.Version, latest.Version) > 0)) { latest = entry; } }
                    plan.Blockers.Add(latest is null ? name + " is not offered by any source" : name + ": " + Reason(latest, catalog, remaining));
                }
            }
            foreach (var entry in chosen)
            {
                bool asked = false; foreach (string name in install) { if (name == entry.Name) { asked = true; } }
                installedNames.TryGetValue(entry.Name, out var previous);
                var m = entry.Manifest;
                plan.Actions.Add(new(previous is null ? PlanVerb.Install : PlanVerb.Upgrade, entry.Name, previous?.Manifest.Version, entry.Version, entry.Repository, entry.Bytes, !asked));
                plan.Downloads.Add(entry);
                if (!cached(entry.Digest)) { plan.DownloadBytes += entry.Bytes; }
                plan.DiskChange += m.PayloadBytes - (previous?.Manifest.PayloadBytes ?? 0);
            }
            foreach (string name in install)
            {
                bool found = false; foreach (var entry in chosen) { if (entry.Name == name) { found = true; } }
                if (!found && chosen.Count > 0 && installedNames.ContainsKey(name)) { plan.Blockers.Add(name + " is already at the newest version it can have"); }
            }
            if (chosen.Count == 0 && plan.Blockers.Count == 0) { plan.Blockers.Add("Already installed at the newest versions they can have"); }
        }
        if (kernel)
        {
            var next = NewestKernel(catalog, kernelFloor);
            if (next is null) { plan.Blockers.Add("No newer kernel is offered"); }
            else
            {
                plan.Kernel = next;
                plan.Actions.Add(new(PlanVerb.Kernel, "zenith-kernel", runningKernel, next.Version, "core", next.Bytes, false));
                if (!cached(next.Digest)) { plan.DownloadBytes += next.Bytes; }
            }
        }
        plan.Command = Command(plan, install, availableUpgrades);
        return plan;
    }

    /// <summary>The pkg commands that do the same, so the GUI teaches the CLI.</summary>
    public static string Command(ChangePlan plan, IReadOnlyList<string> install, int availableUpgrades)
    {
        var lines = new List<string>();
        int upgrades = 0; bool onlyUpgrades = install.Count > 0;
        foreach (var action in plan.Actions)
        {
            if (action.Verb == PlanVerb.Upgrade && !action.AlsoNeeded) { upgrades++; }
            if (action.Verb == PlanVerb.Install && !action.AlsoNeeded) { onlyUpgrades = false; }
        }
        if (upgrades != install.Count) { onlyUpgrades = false; }
        bool everything = onlyUpgrades && upgrades == availableUpgrades;
        if (plan.Removals.Count > 0) { lines.Add("pkg remove " + string.Join(" ", plan.Removals)); }
        if (everything && plan.Kernel is not null && plan.Removals.Count == 0) { lines.Add("pkg update"); }
        else
        {
            if (install.Count > 0) { lines.Add(everything ? "pkg upgrade" : "pkg install " + string.Join(" ", install)); }
            if (plan.Kernel is not null) { lines.Add("pkg kernel update"); }
        }
        return string.Join("\n", lines);
    }
}
