using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using Zenith.Core.Configuration;
using Zenith.Core.Processes;
using Zenith.Core.Security;
namespace Zenith.Tests;
public class PersonalAppearanceTests
{
    private sealed class Defaults : ISettingsStore
    { public string? Text;public bool Persistent=>true;public string? Read(string path)=>Text;public void Replace(string path,string text){Text=text;} }
    private sealed class Store : IPersonalAppearanceStore
    {
        public readonly Dictionary<uint,string> Files=new();public bool Fail;public int AvailabilityReads, FileReads;public bool Persistent=>true;
        public bool Available(uint uid){AvailabilityReads++;return uid is 0 or 1000 or 1001;}
        public string? Read(uint uid){FileReads++;return Files.GetValueOrDefault(uid);}
        public void Replace(uint uid,string text){if(Fail)throw new InvalidOperationException();Files[uid]=text;}
    }
    [Fact]
    public void DisplayPollingUsesLoadedProfilesWithoutAccountOrFilesystemReads()
    {
        var store = new Store();
        var defaults = new AppearanceService(new Defaults());
        defaults.Load();
        var service = new PersonalAppearanceService(defaults, store);
        service.ForDisplay(1000);
        int accounts = store.AvailabilityReads, reads = store.FileReads;
        using (CredentialContext.Enter(Credentials.Root)) { Assert.Equal(0, defaults.Save(1, 2, 1)); }
        for (int i = 0; i < 200; i++) { Assert.Equal(1U, service.ForDisplay(1000).Saved.Mode); }
        Assert.Equal(accounts, store.AvailabilityReads);
        Assert.Equal(reads, store.FileReads);
        Assert.Equal(0, service.Save(1000, 0, 1, 0, 40, service.Snapshot(1000).Saved.Revision));
        Assert.True(store.AvailabilityReads > accounts);
    }
    [Fact]
    public void MalformedDisplayFallbackDoesNotRetryDiskOnEveryFrame()
    {
        var store = new Store();
        store.Files[1000] = "VERSION=999\n";
        var defaults = new AppearanceService(new Defaults());
        defaults.Load();
        var service = new PersonalAppearanceService(defaults, store);
        Assert.True(service.ForDisplay(1000).Inherited);
        int reads = store.FileReads;
        for (int i = 0; i < 200; i++) { Assert.True(service.ForDisplay(1000).Inherited); }
        Assert.Equal(reads, store.FileReads);
        Assert.Equal(-5, service.Save(1000, 0, 0, 0, 40, 1));
        Assert.Equal("VERSION=999\n", store.Files[1000]);
    }
    [Fact]
    public void SavedPersonalProfileIsPendingUntilThatIdentityOwnsTheDisplay()
    {
        var store = new Store();
        store.Files[1000] = "VERSION=2\nMODE=light\nACCENT=rose\nMATERIALS=0\n";
        var defaults = new AppearanceService(new Defaults());
        defaults.Load();
        var service = new PersonalAppearanceService(defaults, store);
        var saved = service.Snapshot(1000);
        Assert.NotEqual(saved.Saved.Revision, saved.Active.Revision);
        service.MarkActive(1000, saved.Saved);
        Assert.Equal(saved.Saved, service.Snapshot(1000).Active);
        Assert.Equal(0UL, service.Snapshot(1001).Active.Revision);
    }
    [Fact]
    public void TwoUsersInheritOverrideResetAndPersistIndependently()
    {
        var store=new Store();var defaults=new AppearanceService(new Defaults());defaults.Load();var service=new PersonalAppearanceService(defaults,store);
        var first=service.Snapshot(1000);Assert.True(first.Inherited);
        using(CredentialContext.Enter(Credentials.Root)){Assert.Equal(0,defaults.SaveFull(1,2,7,1,80));}
        Assert.Equal(1U,service.Snapshot(1000).Saved.Mode);Assert.Equal(-11,service.Save(1000,0,1,3,50,first.Saved.Revision));
        Assert.Equal(0,service.Save(1000,0,1,3,50,service.Snapshot(1000).Saved.Revision));
        Assert.Equal(0U,service.Snapshot(1000).Saved.Mode);Assert.Equal(1U,service.Snapshot(1001).Saved.Mode);
        using(CredentialContext.Enter(Credentials.Root)){Assert.Equal(0,defaults.Save(0,3,2));}
        Assert.Equal(1U,service.Snapshot(1000).Saved.Accent);Assert.Equal(3U,service.Snapshot(1001).Saved.Accent);
        var reboot=new PersonalAppearanceService(defaults,store);Assert.False(reboot.Snapshot(1000).Inherited);Assert.Equal(50U,reboot.Snapshot(1000).Saved.Translucency);
        Assert.Equal(0,reboot.Save(1000,0,0,0,40,reboot.Snapshot(1000).Saved.Revision,true));Assert.True(reboot.Snapshot(1000).Inherited);
        Assert.Equal(3U,reboot.Snapshot(1000).Saved.Accent);Assert.Equal(-13,reboot.Save(65534,0,0,0,40,1));
    }
    [Fact]
    public void ScopedNativeWritesUseCapturedUidAndFailClosedOnFutureData()
    {
        var store=new Store();var defaults=new AppearanceService(new Defaults());defaults.Load();var service=new PersonalAppearanceService(defaults,store);
        using var user=CredentialContext.Enter(new Credentials(1000,1000));
        var snapshot=ProcessScopedAppearance.Snapshot(defaults,service,1);Assert.Equal(1000U,BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(504)));
        byte[] request=new byte[48];request[0]=1;request[4]=1;request[8]=1;request[20]=5;request[28]=60;
        Assert.Equal(0,ProcessScopedAppearance.Save(defaults,service,request));Assert.True(store.Files.ContainsKey(1000));Assert.False(store.Files.ContainsKey(1001));
        request[4]=0;Assert.Equal(-13,ProcessScopedAppearance.Save(defaults,service,request));request[4]=1;request[8]=2;store.Fail=true;
        Assert.Equal(-5,ProcessScopedAppearance.Save(defaults,service,request));Assert.Equal(2UL,service.Snapshot(1000).Saved.Revision);
        store.Files[1001]="VERSION=999\n";Assert.Throws<FormatException>(()=>service.Snapshot(1001));Assert.True(service.ForDisplay(1001).Inherited);
        Assert.Equal(-5,service.Save(1001,0,0,0,40,1));Assert.Equal("VERSION=999\n",store.Files[1001]);
        request[40]=1;Assert.Equal(-22,ProcessScopedAppearance.Save(defaults,service,request));
    }
}
