using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Zenith.Core.Packages;

internal static class RepositoryPlanner
{
    public static IReadOnlyList<RepositoryEntry> Plan(IReadOnlyList<RepositoryEntry> catalog,
        IReadOnlyList<PackageRepository> repositories, IReadOnlyList<InstalledPackage> installed,
        IReadOnlyList<string> requested, Action? cancel = null)
    {
        if (requested.Count is < 1 or > 32 || requested.Distinct(StringComparer.Ordinal).Count() != requested.Count)
        { throw new IOException("request 1..32 distinct package names"); }
        var roots = requested.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        foreach (string name in roots)
        { if (!PackageManifest.ValidName(name) || name == "zenith-kernel") { throw new IOException("use data package names; kernels use pkg kernel update"); } }
        var priorities = repositories.ToDictionary(x => x.Name, x => x.Priority, StringComparer.Ordinal);
        var old = installed.ToDictionary(x => x.Manifest.Name, StringComparer.Ordinal);
        var choices = catalog.Where(x => x.Kind == "data").OrderBy(x => priorities[x.Repository])
            .ThenBy(x => x.Repository, StringComparer.Ordinal).ThenByDescending(x => x.Version, Comparer<string>.Create(PackageVersions.Compare)).ToArray();
        var selected = new Dictionary<string, RepositoryEntry>(StringComparer.Ordinal); int attempts = 0;
        bool Solve()
        {
            cancel?.Invoke();
            if (++attempts > 4096 || selected.Count > 32) { return false; }
            string? unresolved = roots.FirstOrDefault(x => !selected.ContainsKey(x));
            var needs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var final = installed.Where(x => !roots.Contains(x.Manifest.Name) && !selected.ContainsKey(x.Manifest.Name))
                .Select(x => x.Manifest).Concat(selected.Values.Select(x => x.Manifest)).ToArray();
            foreach (var manifest in final)
            {
                foreach (var dependency in manifest.Dependencies)
                {
                    var bundled = BootstrapComponents.All.FirstOrDefault(x => x.Name == dependency.Name);
                    if (bundled is not null)
                    { if (!PackageVersions.Matches(bundled.Version, dependency.Version)) { return false; } continue; }
                    if (!needs.TryGetValue(dependency.Name, out var terms)) { needs.Add(dependency.Name, terms = new()); }
                    terms.Add(dependency.Version);
                }
            }
            foreach (var need in needs.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (selected.TryGetValue(need.Key, out var candidate))
                { if (!need.Value.All(x => PackageVersions.Matches(candidate.Version, x))) { return false; } }
                else if (!old.TryGetValue(need.Key, out var current) || !need.Value.All(x => PackageVersions.Matches(current.Manifest.Version, x)))
                { unresolved ??= need.Key; }
            }
            if (unresolved is null)
            {
                var result = installed.Where(x => !selected.ContainsKey(x.Manifest.Name)).Concat(selected.Values.Select(x =>
                    new InstalledPackage(x.ManifestText, x.Digest, new string('0', 64)))).ToArray();
                try { PackageManager.ValidateSet(result); return true; } catch (IOException) { return false; }
            }
            foreach (var candidate in choices.Where(x => x.Name == unresolved))
            {
                if (needs.TryGetValue(unresolved, out var terms) && !terms.All(x => PackageVersions.Matches(candidate.Version, x))) { continue; }
                if (old.TryGetValue(unresolved, out var previous))
                {
                    int compare = PackageVersions.Compare(candidate.Version, previous.Manifest.Version);
                    if (compare < 0 || (compare == 0 && candidate.Digest != previous.ArchiveDigest)) { continue; }
                }
                selected[unresolved] = candidate;
                if (Solve()) { return true; }
                selected.Remove(unresolved);
            }
            return false;
        }
        if (!Solve()) { throw new IOException("no compatible package plan: missing dependency, conflict, ownership, platform or version constraint"); }
        return selected.Values.Where(x => !old.TryGetValue(x.Name, out var previous) || previous.ArchiveDigest != x.Digest)
            .OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }
}
