using System;
using System.Linq;
using System.Text;
using System.IO;
using Zenith.Core.Packages;

namespace Zenith.Tests;

public class SystemUpdatePlanTests
{
    private static RepositoryEntry Data(string name, string version, string requirements = "")
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name + version);
        string text = "ZENITH-PACKAGE\t2\nrepository\textra\nname\t" + name + "\nversion\t" + version
            + "\narchitecture\tany\nkind\tdata\nruntime\tbootstrap-1\n" + requirements
            + "file\t/usr/share/doc/" + name + ".txt\t0644\t" + bytes.Length + "\t" + PackagePlanner.Digest(bytes) + "\n";
        return new("extra", "data", name, version, 1024, PackagePlanner.Digest(bytes), text);
    }
    private static RepositoryEntry Kernel(string version) => new("core", "kernel", "zenith-kernel", version, 2048, new string('a', 64), "");
    private static InstalledPackage Installed(RepositoryEntry entry) => new(entry.ManifestText, entry.Digest, new string('b', 64));
    [Fact]
    public void UpdatesAllInstalledDataAndKernelWithoutInstallingUnrequestedPackages()
    {
        var old = Data("app", "1.0"); var newer = Data("app", "2.0");
        var plan = SystemUpdatePlan.Create(new[] { newer, Data("unrequested", "1.0"), Kernel("0.0.9"), Kernel("0.0.10") },
            PackageRepositories.Parse(PackageRepositories.Default), new[] { Installed(old) }, "0.0.7");
        Assert.Equal("app", Assert.Single(plan.Data).Name); Assert.Equal("0.0.10", plan.Kernel!.Version);
    }
    [Fact]
    public void CurrentOrMissingVersionsAndKernelFloorDoNotDowngrade()
    {
        var old = Data("app", "2.0");
        var plan = SystemUpdatePlan.Create(new[] { Data("app", "1.0"), Kernel("0.0.7") },
            PackageRepositories.Parse(PackageRepositories.Default), new[] { Installed(old) }, "0.0.7");
        Assert.True(plan.Empty);
    }
    [Fact]
    public void CompleteInstalledSetCanResolveMoreThanSixteenUpdates()
    {
        var old = Enumerable.Range(0, 20).Select(i => Data("app-" + i, "1.0")).ToArray();
        var newer = old.Select(item => Data(item.Name, "2.0")).ToArray();
        var plan = SystemUpdatePlan.Create(newer, PackageRepositories.Parse(PackageRepositories.Default), old.Select(Installed).ToArray(), "0.0.7");
        Assert.Equal(20, plan.Data.Count);
    }
    [Fact]
    public void UnsatisfiableInstalledReverseDependencyBlocksEntirePlan()
    {
        var library = Data("lib", "1.0"); var app = Data("app", "1.0", "requires\tlib\t>=1.0,<2.0\n");
        Assert.Throws<IOException>(() => SystemUpdatePlan.Create(new[] { Data("lib", "2.0"), Kernel("0.0.8") },
            PackageRepositories.Parse(PackageRepositories.Default), new[] { Installed(library), Installed(app) }, "0.0.7"));
    }
}
