using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Zenith.Core.Configuration;
using Zenith.Core.Processes;
using Zenith.Core.Security;

namespace Zenith.Tests;

public class AppearanceServiceTests
{
    [Fact]
    public void TranslucencyPersistsAndLegacySavesPreserveIt()
    {
        using var root=CredentialContext.Enter(Credentials.Root);
        var store=new Store();var service=new AppearanceService(store);Assert.Null(service.Load());
        byte[] request=new byte[40];request[0]=3;request[8]=1;request[24]=7;request[28]=80;
        Assert.Equal(0,ProcessAppearance.SaveMaterialControl(service,request));Assert.Contains("VERSION=4",store.Text);Assert.Contains("TRANSLUCENCY=80",store.Text);
        var reboot=new AppearanceService(store);Assert.Null(reboot.Load());Assert.Equal(80U,reboot.Saved.Translucency);
        Assert.Equal(0,reboot.Save(1,2,1));Assert.Equal(80U,reboot.Saved.Translucency);
        Assert.Equal(0,reboot.SaveSurfaceMaterials(0,0,3,2));Assert.Equal(80U,reboot.Saved.Translucency);
        byte[] state=ProcessAppearance.MaterialControlSnapshot(reboot);Assert.Equal(3U,BinaryPrimitives.ReadUInt32LittleEndian(state));Assert.Equal(80U,BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(40)));
        Assert.Equal(0UL,BinaryPrimitives.ReadUInt64LittleEndian(ProcessAppearance.MaterialSnapshot(reboot,true).AsSpan(40)));
        request[8]=3;request[28]=101;Assert.Equal(-22,ProcessAppearance.SaveMaterialControl(reboot,request));request[28]=40;request[32]=1;Assert.Equal(-22,ProcessAppearance.SaveMaterialControl(reboot,request));
    }
    private sealed class Store : ISettingsStore
    {
        public string? Text;
        public bool Fail;
        public bool Persistent => true;
        public string? Read(string path) { Assert.Equal(AppearanceService.Path, path); return Text; }
        public void Replace(string path, string text) { if (Fail) { throw new IOException("readonly"); } Text = text; }
    }
    [Fact]
    public void SaveSeparatesPersistedAndActiveStateAndRebootLoadsWithoutWrites()
    {
        using var root = CredentialContext.Enter(Credentials.Root);
        var store = new Store(); var service = new AppearanceService(store); Assert.Null(service.Load());
        Assert.False(service.UsePalette); Assert.Equal(0, service.Save(1, 5, 1));
        Assert.Contains("MODE=light\nACCENT=teal\n", store.Text); Assert.True(service.UsePalette);
        Assert.Equal(1U, service.Saved.Mode); Assert.Equal(0U, service.Active.Mode);
        service.MarkActive(service.Saved); Assert.Equal(service.Saved, service.Active);
        var reboot = new AppearanceService(store); Assert.Null(reboot.Load());
        Assert.Equal(1U, reboot.Active.Mode); Assert.Equal(5U, reboot.Active.Accent);
    }
    [Fact]
    public void FailureAndStaleEditsDoNotApplyOrOverwrite()
    {
        using var root = CredentialContext.Enter(Credentials.Root);
        var store = new Store { Fail = true }; var service = new AppearanceService(store);
        Assert.Equal(-5, service.Save(1, 4, 1)); Assert.Null(store.Text); Assert.Equal(1UL, service.Saved.Revision);
        store.Fail = false; Assert.Equal(0, service.Save(1, 3, 1)); string? saved = store.Text;
        Assert.Equal(-11, service.Save(0, 0, 1)); Assert.Equal(saved, store.Text);
        Assert.Equal(0U, service.Active.Mode);
    }
    [Fact]
    public void IdentityIsRequiredEvenForValidRequests()
    {
        using var user = CredentialContext.Enter(new Credentials(1000, 1000));
        var store = new Store(); var service = new AppearanceService(store);
        Assert.Equal(-13, service.Save(1, 2, 1)); Assert.Null(store.Text);
        byte[] snapshot = ProcessAppearance.Snapshot(service);
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(4)) & 2);
    }
    [Theory]
    [InlineData("VERSION=9\nMODE=dark\nACCENT=blue\n")]
    [InlineData("VERSION=1\nMODE=dark\nMODE=light\nACCENT=blue\n")]
    [InlineData("VERSION=1\nMODE=unknown\nACCENT=blue\n")]
    [InlineData("VERSION=1\nMODE=light\nACCENT=unknown\n")]
    [InlineData("VERSION=1\0\nMODE=light\nACCENT=teal\n")]
    public void CorruptOrFutureConfigurationFallsBackWithoutBeingOverwritten(string text)
    {
        using var root = CredentialContext.Enter(Credentials.Root);
        var store = new Store { Text = text }; var service = new AppearanceService(store);
        Assert.NotNull(service.Load()); Assert.Equal(0U, service.Saved.Mode);
        Assert.Equal(-5, service.Save(1, 4, 1)); Assert.Equal(text, store.Text);
    }
    [Fact]
    public void NativeRequestLayoutAndReservedFieldsAreStrict()
    {
        using var root = CredentialContext.Enter(Credentials.Root);
        var store = new Store(); var service = new AppearanceService(store); byte[] request = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 1); BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), 1); BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(20), 5);
        Assert.Equal(-22, ProcessAppearance.Save(service, request.AsSpan(0, 31)));
        request[24] = 1; Assert.Equal(-22, ProcessAppearance.Save(service, request)); request[24] = 0;
        request[4] = 1; Assert.Equal(-22, ProcessAppearance.Save(service, request)); request[4] = 0;
        request[0] = 2; Assert.Equal(-22, ProcessAppearance.Save(service, request)); request[0] = 1;
        request[20] = 6; Assert.Equal(-22, ProcessAppearance.Save(service, request)); request[20] = 5;
        Assert.Null(store.Text); Assert.Equal(0, ProcessAppearance.Save(service, request));
        byte[] snapshot = ProcessAppearance.Snapshot(service); Assert.Equal(448, snapshot.Length);
        Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(snapshot.AsSpan(8)));
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(16)));
        Assert.Equal(5U, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(20)));
        Assert.Equal(4U, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(4)) & 4);
        Assert.All(snapshot[432..], b => Assert.Equal(0, b));
    }
    [Fact]
    public void SignedResourceFormatUsesCompleteContrastCheckedSemanticPalettes()
    {
        var service = new AppearanceService(new Store());
        service.Resources("VERSION=1\nlight.blue=3157b7\ndark.Surface=202125\n");
        Assert.True(service.Palette.Packaged); Assert.True(service.UsePalette);
        Assert.Equal(0x3157b7U, service.Palette.Get(1, 0, 6));
        Assert.Equal(0x197571U, service.Palette.Get(1, 5, 6));
        byte[] snapshot = ProcessAppearance.Snapshot(service);
        Assert.Equal(0x3157b7U, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(48 + 6 * 32 + 24)));
    }
    [Theory]
    [InlineData("VERSION=2\n")]
    [InlineData("VERSION=1\nVERSION=1\n")]
    [InlineData("VERSION=1\ndark.Text=202125\n")]
    [InlineData("VERSION=1\nlight.blue=ffffff\n")]
    [InlineData("VERSION=1\nlight.Bad=123456\n")]
    [InlineData("VERSION=1\nlight.Surface=ff000000\n")]
    public void InvalidResourcesUseSafeFallback(string text)
    { Assert.Same(AppearancePalette.Default, AppearancePalette.Read(text)); }
    [Fact]
    public async Task ConcurrentServiceEditorsCannotBothCommitOneRevision()
    {
        var store = new Store(); var service = new AppearanceService(store);
        var attempts = Enumerable.Range(0, 12).Select(i => Task.Run(() =>
        {
            using var root = CredentialContext.Enter(Credentials.Root);
            return service.Save((uint)(i % 2), (uint)(i % 6), 1);
        }));
        int[] results = await Task.WhenAll(attempts);
        Assert.Single(results.Where(value => value == 0));
        Assert.Equal(11, results.Count(value => value == -11));
        Assert.Equal(2UL, service.Saved.Revision);
    }
    [Fact]
    public void OwnedDirtyCloseCanBeCancelledButRevocationAlwaysWins()
    {
        var surface = new ProcessSurface(160, 100, "test");
        Assert.Equal(0, surface.SetDirty(true)); Assert.False(surface.RequestClose()); Assert.False(surface.Ended);
        for (int i = 0; i < 100; i++) { surface.Send(new(3, i, 0, 0, 0, 0)); }
        Assert.Equal(9, surface.Poll().Type); // Intent is not evicted by event overflow.
        Assert.Equal(0, surface.SetDirty(false)); Assert.True(surface.RequestClose()); Assert.True(surface.Ended);
        Assert.Equal(1, surface.Poll().Type);
        var revoked = new ProcessSurface(160, 100, "test"); revoked.SetDirty(true); revoked.Dispose();
        Assert.True(revoked.Ended); Assert.Equal(1, revoked.Poll().Type); Assert.Equal(-32, revoked.SetDirty(false));
    }
    [Fact]
    public void MaterialsMigrateAtomicallyPersistAndLegacySavesRetainThem()
    {
        using var root = CredentialContext.Enter(Credentials.Root);
        var store = new Store { Text = "VERSION=1\nMODE=dark\nACCENT=blue\n" };
        var service = new AppearanceService(store);Assert.Null(service.Load());
        Assert.Equal(0, service.SaveFull(1, 4, 3, 1));Assert.Contains("VERSION=2", store.Text);
        Assert.Equal(3U, service.Saved.Materials);Assert.Equal(0U, service.Active.Materials);
        Assert.Equal(-11, service.SaveFull(0, 0, 0, 1));Assert.Equal(3U, service.Saved.Materials);
        Assert.Equal(0, service.Save(0, 1, 2));Assert.Equal(3U, service.Saved.Materials);
        var reboot = new AppearanceService(store);Assert.Null(reboot.Load());Assert.Equal(3U, reboot.Saved.Materials);
        Assert.Equal(0, reboot.SaveFull(0, 1, 2, 1));Assert.Equal(2U, reboot.Saved.Materials); // saved blur can outlive transparency
        store.Fail = true;Assert.Equal(-5, reboot.SaveFull(1, 3, 1, 2));Assert.Equal(2U, reboot.Saved.Materials);
        using var user = CredentialContext.Enter(new Credentials(1000, 1000));Assert.Equal(-13, reboot.SaveFull(0, 0, 0, 2));
    }
    [Fact]
    public void MaterialSettingsWireDoesNotChangeLegacySnapshotsOrTrustClientFlags()
    {
        using var root = CredentialContext.Enter(Credentials.Root);var service = new AppearanceService(new Store());
        byte[] request = new byte[32];request[0] = 1;request[8] = 1;request[24] = 3;
        Assert.Equal(0, ProcessAppearance.SaveMaterials(service, request));
        service.MarkActive(service.Saved);service.ReportMaterials(3, 1, 4);
        var snapshot = ProcessAppearance.MaterialSnapshot(service);Assert.Equal(48, snapshot.Length);
        Assert.Equal(3U, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(16)));
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(20)));
        Assert.All(ProcessAppearance.Snapshot(service)[432..], value => Assert.Equal(0, value));
        request[28] = 1;Assert.Equal(-22, ProcessAppearance.SaveMaterials(service, request));
        Assert.Equal(3U, service.Saved.Materials);
    }
    [Fact]
    public void WholeBackgroundUsesVersionThreeAndSurvivesLegacyAppearanceSave()
    {
        using var root = CredentialContext.Enter(Credentials.Root);
        var store=new Store();var service=new AppearanceService(store);Assert.Null(service.Load());
        Assert.Equal(0,service.SaveFull(0,0,7,1));Assert.Contains("VERSION=3",store.Text);
        var loaded=new AppearanceService(store);Assert.Null(loaded.Load());Assert.Equal(7U,loaded.Saved.Materials);
        Assert.Equal(0,loaded.Save(1,2,1));Assert.Equal(7U,loaded.Saved.Materials);
        Assert.Equal(-22,loaded.SaveFull(0,0,8,2));
        var legacy=ProcessAppearance.MaterialSnapshot(loaded);var extended=ProcessAppearance.MaterialSnapshot(loaded,true);
        Assert.Equal(3U,BinaryPrimitives.ReadUInt32LittleEndian(legacy.AsSpan(16)));
        Assert.Equal(2U,BinaryPrimitives.ReadUInt32LittleEndian(extended));Assert.Equal(7U,BinaryPrimitives.ReadUInt32LittleEndian(extended.AsSpan(16)));
        byte[] request=new byte[32];BinaryPrimitives.WriteUInt32LittleEndian(request,1);BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(8),2);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(24),4);Assert.Equal(-22,ProcessAppearance.SaveMaterials(loaded,request));
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(24),1);Assert.Equal(0,ProcessAppearance.SaveMaterials(loaded,request));Assert.Equal(5U,loaded.Saved.Materials);
        BinaryPrimitives.WriteUInt32LittleEndian(request,2);BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(8),3);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(24),7);Assert.Equal(0,ProcessAppearance.SaveMaterials(loaded,request,true));Assert.Equal(7U,loaded.Saved.Materials);
    }
}
