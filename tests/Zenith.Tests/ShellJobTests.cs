using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Core.Security;

namespace Zenith.Tests;

public class ShellJobTests
{
    [Fact]
    public void BackgroundCopiesEnvironmentAndDirectoryAndWaitReportsExitStatus()
    {
        using var sh=new ShellHarness();
        sh.Run("VALUE=before; mkdir child; sleep 0.05 && echo $VALUE > result & VALUE=after; cd child; wait %1");
        Assert.Equal(0,sh.Shell.LastStatus);
        Assert.Equal("before\n",File.ReadAllText(sh.PathOf("result")));
        Assert.Equal("after",sh.Shell.Get("VALUE"));
        Assert.EndsWith("/child",sh.Shell.WorkingDirectory);
        sh.Run("false & wait %2");Assert.Equal(1,sh.Shell.LastStatus);
        sh.Run("wait %999");Assert.Equal(127,sh.Shell.LastStatus);
    }

    [Fact]
    public void BackgroundHasEofAndNoInteractiveTerminal()
    {
        using var sh=new ShellHarness();
        sh.Shell.ReadInputLine=_=>throw new Exception("background acquired terminal");
        sh.Run("cat > empty & wait");
        Assert.Equal("",File.ReadAllText(sh.PathOf("empty")));
        Assert.False(sh.Shell.HasBackgroundJobs);
    }

    [Fact]
    public void ForegroundCancellationLeavesBackgroundRunningAndSessionCancellationStopsIt()
    {
        using var sh=new ShellHarness();sh.Run("sleep 10 &");
        var run=Task.Run(()=>sh.Run("sleep 10"));Thread.Sleep(100);
        sh.Shell.CancelForeground();Assert.True(run.Wait(TimeSpan.FromSeconds(3)));
        Assert.True(sh.Shell.HasBackgroundJobs);
        sh.Shell.CancelSession();sh.Run("true");
        sh.Run("wait %1");Assert.Equal(130,sh.Shell.LastStatus);
        Assert.False(sh.Shell.HasBackgroundJobs);
    }

    [Fact]
    public void CapacityIsBoundedAndCancellationReleasesWorkers()
    {
        using var sh=new ShellHarness();
        for(int i=0;i<4;i++) { sh.Run("sleep 10 &"); }
        Assert.Contains("maximum 4",sh.Run("sleep 10 &"));
        Assert.Throws<InvalidOperationException>(()=>sh.Shell.SetIdentity(Credentials.Root,"root","/"));
        Assert.Contains("background",sh.Run("exit"));
        sh.Shell.CancelSession();sh.Run("true");sh.Run("wait");
        Assert.False(sh.Shell.HasBackgroundJobs);
        for(int i=0;i<8;i++) { sh.Run("true & wait");Assert.Equal(0,sh.Shell.LastStatus); }
    }
}
