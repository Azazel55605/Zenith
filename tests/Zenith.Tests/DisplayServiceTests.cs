using System;
using System.Collections.Generic;
using System.IO;
using Zenith.Core.Configuration;
using Zenith.Core.Processes;
using Zenith.Core.Security;
namespace Zenith.Tests;
public class DisplayServiceTests
{
    private sealed class Store : ISettingsStore
    {
        public string? Text; public int Writes; public bool Fail; public bool Persistent=>true; public uint Writer;
        public string? Read(string path)=>Text;
        public void Replace(string path,string text) { Writer=CredentialContext.Current.Uid; if(Fail) { throw new IOException(); } Writes++;Text=text; }
    }
    private static readonly DisplayLayout Boot=new(1280,800,1), Large=new(1920,1200,1);
    private static DisplayService Service(Store store,List<DisplayLayout> changes)
    {
        var s=new DisplayService(store);s.Attach("test",Boot,new[]{Boot,Large},m=>{changes.Add(m);return true;},_=>{});return s;
    }
    private static void Preview(DisplayService s,long now=100)
    { var state=s.Snapshot(now);Assert.Equal(0,s.Request(0,42,1,1,2,state.Revision,0));s.Tick(now); }
    [Fact] public void SvgaCatalogueRequiresFrontAndBackBuffersAndActualLimits()
    {
        Assert.True(DisplayLayout.FitsVmware(new(1600,1200,1),3840,2400,16*1024*1024,0));
        Assert.False(DisplayLayout.FitsVmware(new(1920,1200,1),3840,2400,16*1024*1024,0));
        Assert.False(DisplayLayout.FitsVmware(new(1600,1200,1),1280,1024,64*1024*1024,0));
        Assert.True(DisplayLayout.FitsVmware(new(3840,2400,1),3840,2400,128*1024*1024,0));
        Assert.False(DisplayLayout.FitsVmware(new(3840,2160,1),3840,2400,32*1024*1024,0));
    }
    [Fact] public void PreviewDoesNotSaveAndTimeoutRestoresWithoutClient()
    {
        var store=new Store();var changes=new List<DisplayLayout>();var s=Service(store,changes);Preview(s);
        Assert.Equal(Large with {Scale=2},s.Snapshot(100).Current);Assert.Equal(15,s.Snapshot(100).Seconds);Assert.Equal(0,store.Writes);
        s.Tick(15099);Assert.Equal(2,s.Snapshot(15099).Phase);s.Tick(15100);Assert.Equal(Boot,s.Snapshot(15100).Current);Assert.Equal(0,store.Writes);
        Assert.Equal(new[]{Large with{Scale=2},Boot},changes);
    }
    [Fact] public void OnlyOwnerWithCurrentTokenCanKeepAndPersistenceLoads()
    {
        var store=new Store();var changes=new List<DisplayLayout>();var s=Service(store,changes);Preview(s);var a=s.Snapshot(100);
        Assert.Equal(-13,s.Request(1000,42,2,0,0,a.Revision,a.Token));Assert.Equal(-11,s.Request(0,43,2,0,0,a.Revision,a.Token));
        Assert.Equal(-11,s.Request(0,42,2,0,0,a.Revision,a.Token+1));Assert.Equal(0,s.Request(0,42,2,0,0,a.Revision,a.Token));
        Assert.Equal(0,store.Writes);s.Tick(101);Assert.Equal(1,store.Writes);Assert.Equal(Large with{Scale=2},new DisplayService(store).Saved());
        Assert.Equal(-11,s.Request(0,42,2,0,0,a.Revision,a.Token));
    }
    [Fact] public void ClientExitCancelsQueuedOrActiveTrial()
    {
        foreach(bool started in new[]{false,true})
        {
            var store=new Store();var changes=new List<DisplayLayout>();var s=Service(store,changes);var a=s.Snapshot(0);
            Assert.Equal(0,s.Request(0,42,1,1,1,a.Revision,0));if(started)s.Tick(0);s.Abandon(41);s.Abandon(42);s.Tick(1);
            Assert.Equal(Boot,s.Snapshot(1).Current);Assert.Equal(0,s.Snapshot(1).Phase);Assert.Equal(0,store.Writes);
        }
    }
    [Fact] public void FailedSaveRestoresAndReportsFailure()
    {
        var store=new Store{Fail=true};var changes=new List<DisplayLayout>();var s=Service(store,changes);Preview(s);var a=s.Snapshot(100);
        Assert.Equal(0,s.Request(0,42,2,0,0,a.Revision,a.Token));s.Tick(101);Assert.Equal(Boot,s.Snapshot(101).Current);Assert.Equal(-5,s.Snapshot(101).Error);
    }
    [Fact] public void DeferredSaveRestoresUiOwnerCredentialsOnFailure()
    {
        using var user=CredentialContext.Enter(new Credentials(1000,1000));
        var store=new Store{Fail=true};var s=Service(store,new());Preview(s);var a=s.Snapshot(100);
        Assert.Equal(0,s.Request(0,42,2,0,0,a.Revision,a.Token));s.Tick(101);
        Assert.Equal(0U,store.Writer);Assert.Equal(1000U,CredentialContext.Current.Uid);Assert.Equal(Boot,s.Snapshot(101).Current);
    }
    [Fact] public void UnrecoverableBackendFailureIsNotSilentlyPublished()
    {
        var store=new Store();var s=new DisplayService(store);
        s.Attach("broken",Boot,new[]{Boot,Large},_=>throw new IOException("rollback failed"),_=>{});
        Assert.Equal(0,s.Request(0,42,1,1,1,s.Snapshot(0).Revision,0));
        Assert.Throws<IOException>(()=>s.Tick(0));Assert.Equal(0,store.Writes);Assert.Equal(Boot,s.Snapshot(0).Current);
    }
    [Fact] public void UnsupportedSchemaIsNotOverwritten()
    {
        var store=new Store{Text="ZENITH-DISPLAY 2 1920 1200 1"};var s=Service(store,new());Assert.Null(s.Saved());
        Assert.Equal(-19,s.Request(0,42,1,1,1,s.Snapshot(0).Revision,0));Assert.Equal(0,store.Writes);
    }
    [Theory] [InlineData(0U,2U)] [InlineData(1U,0U)] [InlineData(1U,3U)] [InlineData(24U,1U)]
    public void InvalidAndUnusableViewportRefused(uint mode,uint scale)
    {var s=Service(new(),new());Assert.Equal(-22,s.Request(0,42,1,mode,scale,s.Snapshot(0).Revision,0));}
    [Fact] public void ConcurrentAndStaleRequestsRefused()
    {
        var s=Service(new(),new());var a=s.Snapshot(0);Assert.Equal(-11,s.Request(0,42,1,1,1,a.Revision+1,0));
        Assert.Equal(0,s.Request(0,42,1,1,1,a.Revision,0));Assert.Equal(-16,s.Request(0,43,1,1,1,a.Revision,0));
        s.Tick(100);s.Detach(101);Assert.Equal(Boot,s.Snapshot(101).Current);Assert.False(s.Snapshot(101).Active);
    }
    [Fact] public void FailedApplyIsNotPublishedOrPersisted()
    {
        var store=new Store();var s=new DisplayService(store);s.Attach("fixed",Boot,new[]{Boot,Large},_=>false,_=>{});
        Assert.Equal(0,s.Request(0,42,1,1,1,s.Snapshot(0).Revision,0));s.Tick(0);Assert.Equal(Boot,s.Snapshot(0).Current);Assert.Equal(-5,s.Snapshot(0).Error);Assert.Equal(0,store.Writes);
    }
    [Fact] public void WireContractHasBoundedNamesAndReservedBytes()
    {
        var s=Service(new(),new());var b=ProcessDisplaySettings.Snapshot(s,0);Assert.Equal(512,b.Length);Assert.Equal(1,b[0]);
        Assert.Equal(-22,ProcessDisplaySettings.Request(s,new byte[39],0,42));var request=new byte[40];request[0]=1;request[32]=1;
        Assert.Equal(-22,ProcessDisplaySettings.Request(s,request,0,42));
    }
}
