using System;
using System.Collections.Generic;
using System.Linq;

namespace Zenith.Core.Packages;

internal sealed record SystemUpdatePlan(IReadOnlyList<RepositoryEntry> Data, RepositoryEntry? Kernel)
{
    public bool Empty => Data.Count == 0 && Kernel is null;
    public static SystemUpdatePlan Create(IReadOnlyList<RepositoryEntry> catalog,
        IReadOnlyList<PackageRepository> repositories, IReadOnlyList<InstalledPackage> installed,
        string kernelFloor, Action? cancel = null)
    {
        var names = installed.Where(item => catalog.Any(candidate => candidate.Kind == "data"
            && candidate.Name == item.Manifest.Name && PackageVersions.Compare(candidate.Version, item.Manifest.Version) > 0))
            .Select(item => item.Manifest.Name).ToArray();
        var data = names.Length == 0 ? Array.Empty<RepositoryEntry>()
            : RepositoryPlanner.Plan(catalog, repositories, installed, names, cancel);
        var kernel = catalog.Where(candidate => candidate.Kind == "kernel" && candidate.Repository == "core"
                && candidate.Name == "zenith-kernel" && PackageVersions.Compare(candidate.Version, kernelFloor) > 0)
            .OrderByDescending(candidate => candidate.Version, Comparer<string>.Create(PackageVersions.Compare)).FirstOrDefault();
        return new(data, kernel);
    }
}
