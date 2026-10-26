using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Buffers.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Core.Processes;
using Zenith.Core.Shell;

namespace Zenith.Tests;

public class ProcessLifecycleTests
{
    [Fact]
    public void EnvironmentStackHasIndependentTerminatedVectorsAndAlignedUserPointers()
    {
        var env = new Dictionary<string,string> { ["TERM"]="zenith", ["LANG"]="Grüße", ["EMPTY"]="" };
        var stack=ProcessArguments.Create(new[]{"sunbeam","env"},env);
        int at=(int)(stack.StackPointer-(ProcessArguments.Top-ProcessArguments.StackBytes));
        Assert.Equal(0UL,stack.StackPointer%16);
        ulong Word(int offset)=>BinaryPrimitives.ReadUInt64LittleEndian(stack.Bytes.AsSpan(at+offset*8));
        string Text(ulong pointer)
        {
            Assert.InRange(pointer,ProcessArguments.Top-ProcessArguments.StackBytes,ProcessArguments.Top-1);
            int start=(int)(pointer-(ProcessArguments.Top-ProcessArguments.StackBytes));
            return Encoding.UTF8.GetString(stack.Bytes,start,Array.IndexOf(stack.Bytes,(byte)0,start)-start);
        }
        Assert.Equal(2UL,Word(0)); Assert.Equal("sunbeam",Text(Word(1))); Assert.Equal("env",Text(Word(2)));
        Assert.Equal(0UL,Word(3));
        Assert.Equal(new[]{"EMPTY=","LANG=Grüße","TERM=zenith"},Enumerable.Range(4,3).Select(i=>Text(Word(i))));
        Assert.Equal(0UL,Word(7));Assert.Equal(0UL,Word(8));Assert.Equal(0UL,Word(9));
        env["TERM"]="changed";Assert.Equal("TERM=zenith",Text(Word(6)));
    }
    [Theory]
    [InlineData("")] [InlineData("A=B")] [InlineData("9BAD")] [InlineData("BAD\0NAME")]
    public void InvalidEnvironmentNamesAreRefused(string key)
    { Assert.Throws<IOException>(()=>ProcessArguments.Create(new[]{"x"},new Dictionary<string,string>{{key,"v"}})); }
    [Fact]
    public void EnvironmentLimitsUseUtf8BytesAndRefuseEmbeddedNul()
    {
        Assert.Throws<IOException>(()=>ProcessArguments.Create(new[]{"x"},new Dictionary<string,string>{{"A","x\0y"}}));
        Assert.Throws<IOException>(()=>ProcessArguments.Create(new[]{"x"},new Dictionary<string,string>{{"A",new string('ü',4096)}}));
        Assert.Throws<IOException>(()=>ProcessArguments.Create(new[]{"x"},Enumerable.Range(0,65).ToDictionary(i=>"N"+i,i=>"")));
        ProcessArguments.Create(new[]{"x"},new Dictionary<string,string>{{"A",new string('x',8189)}});
    }
    [Fact]
    public void OnlyExplicitExportsAndSafeDefaultsEnterNativeSnapshot()
    {
        var shell=new Shell(new BufferOutput(),login:false);
        shell.Execute("PRIVATE=secret; PUBLIC=hello; export PUBLIC; export SECOND=world");
        var snapshot=shell.ExportedEnvironment;
        Assert.False(snapshot.ContainsKey("PRIVATE"));Assert.Equal("hello",snapshot["PUBLIC"]);
        Assert.Equal("world",snapshot["SECOND"]);Assert.Equal("zenith",snapshot["TERM"]);
        shell.Set("PUBLIC","changed");Assert.Equal("hello",snapshot["PUBLIC"]);
        shell.Execute("unset PUBLIC; PUBLIC=local");Assert.False(shell.ExportedEnvironment.ContainsKey("PUBLIC"));
        shell.Execute("export 'INVALID=NAME=ok' '2BAD=no'");Assert.NotEqual(0,shell.LastStatus);
    }
    [Fact]
    public void CapacityCountsActiveProcessesAndCompletionIsIdempotent()
    {
        var table=new ProcessTable(2,2);int a=table.Start("one",1000,()=>{}),b=table.Start("two",1000,()=>{});
        Assert.Equal(-1,table.Start("three",1000,()=>{}));table.Complete(a,139);table.Complete(a,0);
        Assert.Equal(1,table.ActiveCount);int c=table.Start("three",1000,()=>{});Assert.True(c>b);
        Assert.Equal(139,table.List(1000).Single(p=>p.Pid==a).Status);Assert.Equal(2,table.ActiveCount);
        table.SetState(a,ProcessState.Running);Assert.Equal(ProcessState.Exited,table.List(0).Single(p=>p.Pid==a).State);
    }
    [Fact]
    public void ProcessNamesAndVisibilityCannotExposeAnotherUsersArgumentsOrIdentity()
    {
        var table=new ProcessTable();int a=table.Start("sunbeam",1000,()=>{});table.Start("private-tool",1001,()=>{});
        Assert.Single(table.List(1000));Assert.Equal(a,table.List(1000)[0].Pid);Assert.Equal(2,table.List(0).Count);
        Assert.Equal(-13,table.RequestStop(a,1001));Assert.False(table.List(1000)[0].StopRequested);
        Assert.Throws<ArgumentException>(()=>table.Start("tool\nSECRET",1000,()=>{}));
    }
    [Fact]
    public async Task CancellationRunsOutsideRegistryLockAndIsRequestedOnlyOnce()
    {
        var table=new ProcessTable();int calls=0,pid=0;
        pid=table.Start("sunbeam",1000,()=>
        {
            Task.Run(()=>table.Complete(pid,130)).GetAwaiter().GetResult();Interlocked.Increment(ref calls);
        });
        await Task.Run(()=>Assert.Equal(0,table.RequestStop(pid,0))).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(-3,table.RequestStop(pid,1000));Assert.Equal(1,calls);Assert.Equal(0,table.ActiveCount);
        Assert.Equal(130,table.List(1000)[0].Status);
    }
    [Fact]
    public void LateStopCannotCancelTheNextTerminalCommand()
    {
        int stopped=0;
        var previous=new ProcessCancellation(()=>stopped++);previous.Close();previous.Request();
        Assert.Equal(0,stopped);Assert.False(previous.Requested);
        var current=new ProcessCancellation(()=>stopped++);current.Request();current.Request();current.Close();
        Assert.Equal(1,stopped);Assert.True(current.Requested);
    }
    [Fact]
    public void ExitHistoryIsBoundedAndDetachedFromMutableRegistryState()
    {
        var table=new ProcessTable(1,2);int first=table.Start("one",1000,()=>{});var original=table.List(1000)[0];
        table.SetState(first,ProcessState.Waiting);Assert.Equal(ProcessState.Starting,original.State);
        table.Complete(first,0);
        for(int i=0;i<3;i++){int pid=table.Start("one",1000,()=>{});table.Complete(pid,0);}
        Assert.Equal(2,table.List(1000).Count);Assert.Equal(-3,table.RequestStop(first,0));
    }
}
