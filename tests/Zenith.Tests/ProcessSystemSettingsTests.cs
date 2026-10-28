using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Zenith.Core.Configuration;
using Zenith.Core.Processes;
using Zenith.Core.Security;
namespace Zenith.Tests;
public class ProcessSystemSettingsTests
{
    private sealed class Store : ISettingsStore
    {
        public bool Fail; public bool Persistent => true;
        public readonly Dictionary<string,string> Files = new();
        public string? Read(string path) => Files.GetValueOrDefault(path);
        public void Replace(string path,string text) { if(Fail)throw new IOException(); Files[path]=text; }
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
