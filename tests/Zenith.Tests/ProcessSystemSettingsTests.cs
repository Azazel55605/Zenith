using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Zenith.Core.Configuration;
using Zenith.Core.Processes;
using Zenith.Core.Security;
using Zenith.Core.Sessions;
namespace Zenith.Tests;
public class ProcessSystemSettingsTests
{
    internal sealed class Store : ISettingsStore
    {
        public bool Fail; public bool Persistent => true;
        public readonly Dictionary<string,string> Files = new();
        public string? Read(string path) => Files.GetValueOrDefault(path);
        public void Replace(string path,string text) { if(Fail)throw new IOException(); Files[path]=text; }
    }
    [Fact]
    public void TaskbarChoiceIsPublishedSavedAndSurvivesReboot()
    {
        var store=new Store();var service=new SettingsService(store,_=>{},_=>{},_=>true);
        using var root=CredentialContext.Enter(Credentials.Root);
        var data=ProcessSystemSettings.Snapshot(service,true);
        Assert.Equal(5U,BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(3616)));Assert.Equal(0U,BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(3620)));
        Assert.Equal((byte)'D',data[3624]);Assert.Equal((byte)'A',data[3624+4*24]);
        byte[] request=new byte[32];request[0]=2;request[4]=5;request[8]=1;request[16]=2;
        Assert.Equal(0,ProcessSystemSettings.Save(service,request,true));Assert.Equal(2U,service.Current.Taskbar);
        request[8]=2;request[16]=5;Assert.Equal(-22,ProcessSystemSettings.Save(service,request,true));
        var reboot=new SettingsService(store,_=>{},_=>{},_=>true);Assert.Empty(reboot.Load());Assert.Equal(2U,reboot.Current.Taskbar);
        store.Replace(SettingsService.TaskbarPath,"TASKBAR=always\n");
        var earlier=new SettingsService(store,_=>{},_=>{},_=>true);Assert.Empty(earlier.Load());Assert.Equal(1U,earlier.Current.Taskbar);
        store.Replace(SettingsService.TaskbarPath,"TASKBAR=sideways\n");
        var broken=new SettingsService(store,_=>{},_=>{},_=>true);Assert.Single(broken.Load());Assert.Equal(0U,broken.Current.Taskbar);
    }
    [Fact]
    public void TheStyleCatalogueListsEveryWallpaperAndASecondSetChoiceSaves()
    {
        var store=new Store();var service=new SettingsService(store,_=>{},_=>{},_=>true);
        using var root=CredentialContext.Enter(Credentials.Root);
        byte[] list=Zenith.Core.Configuration.StyleCatalog.List(service,6,0x7ECAC7)!;
        Assert.Equal(16U,BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(8)));
        Assert.Equal("Orbits",Encoding.UTF8.GetString(list,32+12*64+24,6));
        Assert.Equal(0U,BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(32+12*64+56)));          // dynamic
        Assert.Equal(1U,BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(32+4*64+56)));           // Graphite: solid
        Assert.Equal(0x7ECAC7U,BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(32+11*64+60)));   // Accent color
        Assert.Null(Zenith.Core.Configuration.StyleCatalog.List(service,1,0));
        byte[] request=new byte[32];request[0]=2;request[4]=6;request[8]=1;request[16]=13;         // Tides
        Assert.Equal(0,ProcessSystemSettings.Save(service,request,true));Assert.Equal(13U,service.Current.Wallpaper);
        var data=ProcessSystemSettings.Snapshot(service,true);
        Assert.Equal(13U,BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(3748)));                // beyond the 12 names
        var reboot=new SettingsService(store,_=>{},_=>{},_=>true);Assert.Empty(reboot.Load());Assert.Equal(13U,reboot.Current.Wallpaper);
    }
    [Fact]
    public void WallpaperAndLauncherChoicesArePublishedSavedAndSurviveReboot()
    {
        var store=new Store();var service=new SettingsService(store,_=>{},_=>{},_=>true);
        using var root=CredentialContext.Enter(Credentials.Root);
        var data=ProcessSystemSettings.Snapshot(service,true);
        Assert.Equal(12U,BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(3744)));   // the legacy snapshot names 12
        Assert.Equal(0U,BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(3748)));
        Assert.Equal("Zenith (dynamic)",Encoding.UTF8.GetString(data,3752,16));
        Assert.Equal(2U,BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4040)));Assert.Equal((byte)'S',data[4048+24]);
        byte[] request=new byte[32];request[0]=2;request[4]=6;request[8]=1;request[16]=2;    // wallpaper: Aurora
        Assert.Equal("aurora",SettingsService.WallpaperNames[2]);Assert.Equal(16,SettingsService.WallpaperNames.Length);
        Assert.Equal(0,ProcessSystemSettings.Save(service,request,true));Assert.Equal(2U,service.Current.Wallpaper);
        request[4]=7;request[8]=2;request[16]=1;                                             // launcher: palette
        Assert.Equal(0,ProcessSystemSettings.Save(service,request,true));Assert.Equal(1U,service.Current.Launcher);
        request[8]=3;request[16]=2;Assert.Equal(-22,ProcessSystemSettings.Save(service,request,true));
        request[4]=99;request[16]=0;Assert.Equal(-22,ProcessSystemSettings.Save(service,request,true));
        data=ProcessSystemSettings.Snapshot(service,true);
        Assert.Equal(2U,BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(3748)));Assert.Equal(1U,BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4044)));
        Assert.Equal(2U,BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(112)));Assert.Equal(0U,BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(116)));
        request[4]=8;request[8]=3;request[16]=1;                                             // cursor set: Dark
        Assert.Equal(0,ProcessSystemSettings.Save(service,request,true));Assert.Equal(1U,service.Current.Cursor);
        Assert.Equal(1U,BinaryPrimitives.ReadUInt32LittleEndian(ProcessSystemSettings.Snapshot(service,true).AsSpan(116)));
        var reboot=new SettingsService(store,_=>{},_=>{},_=>true);Assert.Empty(reboot.Load());
        Assert.Equal(2U,reboot.Current.Wallpaper);Assert.Equal(1U,reboot.Current.Launcher);Assert.Equal(1U,reboot.Current.Cursor);
        store.Replace(SettingsService.WallpaperPath,"WALLPAPER=lava\n");
        var broken=new SettingsService(store,_=>{},_=>{},_=>true);Assert.Single(broken.Load());Assert.Equal(0U,broken.Current.Wallpaper);
        using var user=CredentialContext.Enter(new Credentials(1000,1000));
        request[4]=6;request[8]=4;request[16]=1;Assert.Equal(-13,ProcessSystemSettings.Save(service,request,true));
    }
    [Fact]
    public void AnimationChoiceIsPublishedSavedAndSurvivesReboot()
    {
        var store=new Store();var service=new SettingsService(store,_=>{},_=>{},_=>true);
        using var root=CredentialContext.Enter(Credentials.Root);
        Assert.Equal(100,service.AnimationPercent);
        var data=ProcessSystemSettings.Snapshot(service,true);
        Assert.Equal(4U,BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(3512)));Assert.Equal(0U,BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(3516)));
        Assert.Equal((byte)'N',data[3520]);Assert.Equal((byte)'O',data[3520+3*24]);
        byte[] request=new byte[32];request[0]=2;request[4]=4;request[8]=1;request[16]=3;
        Assert.Equal(0,ProcessSystemSettings.Save(service,request,true));Assert.Equal(0,service.AnimationPercent);
        request[8]=2;request[16]=4;Assert.Equal(-22,ProcessSystemSettings.Save(service,request,true));
        request[0]=1;request[16]=1;Assert.Equal(-22,ProcessSystemSettings.Save(service,request));
        var reboot=new SettingsService(store,_=>{},_=>{},_=>true);Assert.Empty(reboot.Load());Assert.Equal(3U,reboot.Current.Animation);
        store.Replace(SettingsService.MotionPath,"ANIMATION=sideways\n");
        var broken=new SettingsService(store,_=>{},_=>{},_=>true);Assert.Single(broken.Load());Assert.Equal(100,broken.AnimationPercent);
    }
    [Fact]
    public void TilingDefaultCanBeSavedAndBootedWithDisplayOrRecoveredHeadless()
    {
        var store = new Store();
        var settings = new SettingsService(store, _ => { }, _ => { }, _ => true);
        var data = ProcessSystemSettings.Snapshot(settings, true);
        Assert.Equal(3U, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(3456)));
        Assert.Equal("wm", Encoding.UTF8.GetString(data, 3464 + 2 * 16, 2));
        byte[] request = new byte[32];
        request[0] = 2; request[4] = 3; request[8] = 1; request[16] = 2;
        using (CredentialContext.Enter(new Credentials(1000, 1000)))
        { Assert.Equal(-13, ProcessSystemSettings.Save(settings, request, true)); }
        using (CredentialContext.Enter(Credentials.Root))
        { Assert.Equal(0, ProcessSystemSettings.Save(settings, request, true)); }

        var restored = new SettingsService(store, _ => { }, _ => { }, _ => true);
        Assert.Empty(restored.Load());
        Assert.Equal(SessionMode.WindowManager, restored.Current.DefaultSession);
        Assert.Equal(SessionMode.WindowManager, SessionModes.Select(restored.Current.DefaultSession, "", _ => true).Mode);
        var recovery = SessionModes.Select(restored.Current.DefaultSession, "", mode => mode == SessionMode.Headless);
        Assert.Equal(SessionMode.Headless, recovery.Mode);
        Assert.Equal("capability fallback", recovery.Source);
        Assert.Equal(SessionMode.Headless, SessionModes.Select(restored.Current.DefaultSession, "session=headless", _ => true).Mode);
    }
    [Fact]
    public void NewSessionCatalogueIsVersionedAndOldRequestsCannotSaveIt()
    {
        var service=new SettingsService(new Store(),_=>{},_=>{},mode=>mode!=Zenith.Core.Sessions.SessionMode.WindowManager);
        using var root=CredentialContext.Enter(Credentials.Root);var data=ProcessSystemSettings.Snapshot(service,true);
        Assert.Equal(2U,BinaryPrimitives.ReadUInt32LittleEndian(data));Assert.Equal(2U,BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(3456)));
        byte[] request=new byte[32];request[0]=2;request[4]=3;request[8]=1;request[16]=1;
        Assert.Equal(0,ProcessSystemSettings.Save(service,request,true));Assert.Equal(Zenith.Core.Sessions.SessionMode.Headless,service.Current.DefaultSession);
        request[0]=1;Assert.Equal(-22,ProcessSystemSettings.Save(service,request));
        var info=ProcessSettingsInformation.Encode(2,new string('é',5000));int length=(int)BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(8));
        Assert.Equal(1U,BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(12)));Assert.Equal(length,System.Text.Encoding.UTF8.GetByteCount(System.Text.Encoding.UTF8.GetString(info,16,length)));
    }
    [Fact]
    public void ChoicesAreRealSharedDefaultsWithRevisionAndPermissionChecks()
    {
        var store = new Store();var service = new SettingsService(store,_=>{},_=>{},_=>true);
        using var root = CredentialContext.Enter(Credentials.Root);
        byte[] snapshot = ProcessSystemSettings.Snapshot(service);
        Assert.Equal(4096,snapshot.Length);Assert.Equal(7U,BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(16)));
        Assert.Equal("us",Encoding.UTF8.GetString(snapshot,128,2));
        byte[] request = new byte[32]; request[0]=1;request[4]=1;request[8]=1;request[16]=1;
        Assert.Equal(0,ProcessSystemSettings.Save(service,request));Assert.Equal("de",service.Current.Keyboard);
        Assert.Equal(-11,ProcessSystemSettings.Save(service,request));
        service.SetTimeZone("UTC+2");Assert.Equal(3UL,service.Snapshot().Revision);
        snapshot=ProcessSystemSettings.Snapshot(service);Assert.Equal(uint.MaxValue,BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(28)));
        request[8]=3;request[20]=1;Assert.Equal(-22,ProcessSystemSettings.Save(service,request));request[20]=0;
        using(CredentialContext.Enter(new Credentials(1000,1000)))
        { Assert.Equal(-13,ProcessSystemSettings.Save(service,request));Assert.Equal(1U,BinaryPrimitives.ReadUInt32LittleEndian(ProcessSystemSettings.Snapshot(service).AsSpan(4))); }
        store.Fail=true;Assert.Equal(-5,ProcessSystemSettings.Save(service,request));Assert.Equal(3UL,service.Snapshot().Revision);
        request[16]=255;Assert.Equal(-22,ProcessSystemSettings.Save(service,request));
        Assert.Equal(-22,ProcessSystemSettings.Save(service,new byte[31]));
        var reboot=new SettingsService(store,_=>{},_=>{},_=>true);Assert.Empty(reboot.Load());Assert.Equal("de",reboot.Current.Keyboard);Assert.Equal("UTC+2",reboot.Current.TimeZone);
    }
}
