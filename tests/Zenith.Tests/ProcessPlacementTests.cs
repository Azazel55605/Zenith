using System;
using System.Collections.Generic;
using Zenith.Core.Processes;

namespace Zenith.Tests;

public class ProcessPlacementTests
{
    [Fact]
    public void MissingAffinityAllowsFallbackButExplicitAffinityIsStrict()
    {
        Assert.Equal(255U,ProcessPlacement.Parse(null));
        foreach (var pair in new[] { ("1",1U), ("0x0e",14U), ("255",255U) })
            Assert.Equal(pair.Item2,ProcessPlacement.Parse(new Dictionary<string,string>{{"ZENITH_CPU_MASK",pair.Item1}}));
        foreach (string value in new[] { "", "0", "256", "-1", " 2", "2 ", "0x100", "banana", null! })
            Assert.Throws<ArgumentException>(()=>ProcessPlacement.Parse(new Dictionary<string,string>{{"ZENITH_CPU_MASK",value}}));
    }

    [Fact]
    public void CpuMetadataKeepsAffinityIdentityAndExactMigrationCount()
    {
        var table=new ProcessTable();int pid=table.Start("worker",1000,()=>{});
        table.SetExecution(pid,1,14);table.SetExecution(pid,1,14);table.SetExecution(pid,3,14);
        var entry=Assert.Single(table.List(1000));Assert.Equal(3,entry.Cpu);Assert.Equal(1,entry.Migrations);
        Assert.Equal(14U,entry.Affinity);Assert.Empty(table.List(1001));
        Assert.Throws<ArgumentException>(()=>table.SetExecution(pid,0,14));
        table.Complete(pid,0);table.SetExecution(pid,2,14);
        Assert.Equal(3,Assert.Single(table.List(1000)).Cpu);
    }
}
