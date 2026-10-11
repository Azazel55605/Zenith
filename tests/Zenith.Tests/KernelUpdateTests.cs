using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Zenith.Core.Boot;
using Zenith.Core.Packages;

namespace Zenith.Tests;

public sealed class KernelUpdateTests
{
    private sealed class Store : IKernelUpdateStore
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public int Operation, FailAt;
        public ulong FreeEspBytes { get; set; } = 100 * 1024 * 1024;
        private void Tick() { if (++Operation == FailAt) { throw new IOException("injected durable boundary failure"); } }
        public bool Exists(string path) => Files.ContainsKey(path);
        public byte[] Read(string path, int maximum) => Files.TryGetValue(path, out var data) && data.Length <= maximum ? (byte[])data.Clone() : throw new IOException("missing/bounded file");
        public void Write(string path, byte[] data) { Files[path] = (byte[])data.Clone(); Tick(); }
        public void Delete(string path) { Files.Remove(path); Tick(); }
        public void Directory(string path) { Tick(); }
        public void Sync() { Tick(); }
        public void RecoverWrites() { }
    }
    private static byte[] Elf(int seed)
    {
        byte[] elf = new byte[128]; elf[0] = 127; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F'; elf[4] = 2; elf[5] = 1; elf[18] = 62; elf[6] = 1; elf[16] = 2; elf[20] = 1; elf[52] = 64; elf[54] = 56; elf[56] = 1;
        BitConverter.TryWriteBytes(elf.AsSpan(24), 0x1000UL); BitConverter.TryWriteBytes(elf.AsSpan(32), 64UL);
        elf[64] = 1; elf[68] = 1; elf[72] = 120; BitConverter.TryWriteBytes(elf.AsSpan(80), 0x1000UL);
        elf[96] = 8; elf[104] = 8; elf[127] = (byte)seed; return elf;
    }
    private static readonly Guid Root = Guid.Parse("11111111-1111-1111-1111-111111111111"), Esp = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static (Store Store, KernelUpdater Updater, KernelUpdateState State) Fixture()
    {
        var store = new Store(); byte[] elf = Elf(1), efi = new byte[256]; efi[0] = (byte)'M'; efi[1] = (byte)'Z'; efi[60] = 64; efi[64] = (byte)'P'; efi[65] = (byte)'E'; efi[68] = 0x64; efi[69] = 0x86;
        var state = new KernelUpdateState(Root, Esp, BootLayout.Digest(efi), efi.Length, new(BootLayout.Digest(elf), elf.Length, "0.0.0"), null, null, false, "0.0.0");
        store.Files[KernelUpdater.RootInventory] = Encoding.UTF8.GetBytes(state.Inventory()); store.Files[KernelUpdater.EspInventory] = Encoding.UTF8.GetBytes(state.Inventory());
        store.Files[KernelUpdater.MenuPath] = Encoding.UTF8.GetBytes(state.Menu()); store.Files[KernelUpdater.EspPrefix + BootLayout.KernelPath(state.Current.Id)] = elf;
        store.Files[KernelUpdater.EspPrefix + "/EFI/BOOT/BOOTX64.EFI"] = efi;
        return (store, new(store, Root, Esp), state);
    }
    private static KernelPackage Candidate(int seed, string version) { byte[] elf = Elf(seed); return new(version, BootLayout.Digest(elf), elf); }
    [Fact]
    public void UpgradeRequiresRebootThenConfirmationAndKeepsPrevious()
    {
        var (store, updater, before) = Fixture(); var candidate = Candidate(2, "1.0.0");
        updater.Install(candidate, before.Current.Id, () => { }, _ => { }); var pending = updater.Status();
        Assert.True(pending.Pending); Assert.Equal(before.Current, pending.Previous); Assert.Equal(candidate.Id, pending.Current.Id);
        Assert.Equal(pending.Menu(), Encoding.UTF8.GetString(store.Files[KernelUpdater.FallbackMenu]));
        Assert.Throws<IOException>(() => updater.Confirm(before.Current.Id)); Assert.False(store.Exists(KernelUpdater.Journal));
        updater.Confirm(candidate.Id); Assert.False(updater.Status().Pending); Assert.Equal(before.Current, updater.Status().Previous);
        Assert.Equal(updater.Status().Menu(), Encoding.UTF8.GetString(store.Files[KernelUpdater.FallbackMenu]));
    }
    [Fact]
    public void RollbackPreservesHighWaterAndNextUpgradeReclaimsOnlyThirdKernel()
    {
        var (store, updater, before) = Fixture(); var one = Candidate(2, "1.0.0"); updater.Install(one, before.Current.Id, () => { }, _ => { });
        updater.Rollback(); Assert.Equal(before.Current, updater.Status().Current); updater.Confirm(before.Current.Id);
        Assert.Throws<IOException>(() => updater.Install(Candidate(3, "1.0.0"), before.Current.Id, () => { }, _ => { }));
        var two = Candidate(3, "2.0.0"); updater.Install(two, before.Current.Id, () => { }, _ => { }); updater.Confirm(two.Id);
        Assert.False(store.Exists(KernelUpdater.EspPrefix + BootLayout.KernelPath(one.Id)));
        Assert.True(store.Exists(KernelUpdater.EspPrefix + BootLayout.KernelPath(before.Current.Id)));
    }
    [Fact]
    public void TwoConfirmedUpgradesReclaimBaselineAndRetainLastWorkingKernel()
    {
        var (store, updater, baseline) = Fixture(); var one = Candidate(2, "1.0.0"); var two = Candidate(3, "2.0.0");
        updater.Install(one, baseline.Current.Id, () => { }, _ => { }); updater.Confirm(one.Id);
        updater.Install(two, one.Id, () => { }, _ => { });
        Assert.Equal(baseline.Current, updater.Status().Retired);
        Assert.True(store.Exists(KernelUpdater.EspPrefix + BootLayout.KernelPath(baseline.Current.Id)));
        int operations = store.Operation;
        Assert.Throws<IOException>(() => updater.Install(Candidate(4, "3.0.0"), two.Id, () => { }, _ => { }));
        Assert.Equal(operations, store.Operation);
        updater.Confirm(two.Id);
        Assert.False(store.Exists(KernelUpdater.EspPrefix + BootLayout.KernelPath(baseline.Current.Id)));
        Assert.True(store.Exists(KernelUpdater.EspPrefix + BootLayout.KernelPath(one.Id)));
        Assert.True(store.Exists(KernelUpdater.EspPrefix + BootLayout.KernelPath(two.Id)));
        Assert.Equal(one.Id, updater.Status().Previous!.Id); Assert.Null(updater.Status().Retired);
    }
    [Fact]
    public void EveryDurableInstallFailureRecoversOriginalMenuAndAllowsRetry()
    {
        var (probe, engine, original) = Fixture(); engine.Install(Candidate(2, "1.0.0"), original.Current.Id, () => { }, _ => { }); int operations = probe.Operation;
        for (int boundary = 1; boundary <= operations; boundary++)
        {
            var (store, updater, before) = Fixture(); store.FailAt = boundary;
            Assert.Throws<IOException>(() => updater.Install(Candidate(2, "1.0.0"), before.Current.Id, () => { }, _ => { }));
            store.FailAt = 0;
            if (store.Exists(KernelUpdater.Journal))
            {
                updater.Recover(); Assert.Equal(before, updater.Status());
                Assert.Equal(before.Menu(), Encoding.UTF8.GetString(store.Files[KernelUpdater.MenuPath]));
                updater.Install(Candidate(2, "1.0.0"), before.Current.Id, () => { }, _ => { });
            }
            else { Assert.True(updater.Status().Pending); } // commit completed before final flush reported failure
        }
    }
    [Fact]
    public void CancellationDuringStagingLeavesRecoverableUndo()
    {
        var (store, updater, before) = Fixture(); int calls = 0;
        Assert.Throws<OperationCanceledException>(() => updater.Install(Candidate(2, "1.0.0"), before.Current.Id,
            () => { if (++calls == 2) { throw new OperationCanceledException(); } }, _ => { }));
        Assert.True(store.Exists(KernelUpdater.Journal)); updater.Recover(); Assert.Equal(before, updater.Status());
    }
    [Fact]
    public void SpaceIdentityAndRetainedIntegrityFailuresNeverChangeMenu()
    {
        var (store, updater, before) = Fixture(); string menu = before.Menu();
        store.FreeEspBytes = 1; Assert.Throws<IOException>(() => updater.Install(Candidate(2, "1.0.0"), before.Current.Id, () => { }, _ => { }));
        Assert.Throws<IOException>(() => new KernelUpdater(store, Guid.NewGuid(), Esp).Status());
        store.FreeEspBytes = 100 * 1024 * 1024; store.Files[KernelUpdater.EspPrefix + BootLayout.KernelPath(before.Current.Id)][127]++;
        Assert.Throws<IOException>(() => updater.Install(Candidate(2, "1.0.0"), before.Current.Id, () => { }, _ => { }));
        Assert.Equal(menu, Encoding.UTF8.GetString(store.Files[KernelUpdater.MenuPath])); Assert.False(store.Exists(KernelUpdater.Journal));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ConfirmationAndRollbackFailuresRecoverDurablePriorSelection(bool rollback)
    {
        int operations;
        var (probe, engine, initial) = Fixture(); var candidate = Candidate(2, "1.0.0");
        engine.Install(candidate, initial.Current.Id, () => { }, _ => { }); probe.Operation = 0;
        if (rollback) { engine.Rollback(); } else { engine.Confirm(candidate.Id); } operations = probe.Operation;
        for (int boundary = 1; boundary <= operations; boundary++)
        {
            var (store, updater, before) = Fixture(); updater.Install(candidate, before.Current.Id, () => { }, _ => { });
            var pending = updater.Status(); store.Operation = 0; store.FailAt = boundary;
            Assert.Throws<IOException>(() => { if (rollback) { updater.Rollback(); } else { updater.Confirm(candidate.Id); } });
            store.FailAt = 0;
            if (updater.RecoveryRequired) { updater.Recover(); Assert.Equal(pending, updater.Status()); }
            Assert.True(store.Exists(KernelUpdater.EspPrefix + BootLayout.KernelPath(before.Current.Id)));
        }
    }
    [Fact]
    public void OccupiedWorkFileAndMalformedElfFailBeforeAnyMutation()
    {
        var (store, updater, before) = Fixture(); var candidate = Candidate(2, "1.0.0");
        store.Files[KernelUpdater.MenuPath + ".zenith-new"] = new byte[] { 1 };
        Assert.Throws<IOException>(() => updater.Install(candidate, before.Current.Id, () => { }, _ => { }));
        Assert.Equal(0, store.Operation); Assert.Single(store.Files[KernelUpdater.MenuPath + ".zenith-new"]);
        store.Files.Remove(KernelUpdater.MenuPath + ".zenith-new"); candidate.Kernel[56] = 128;
        Assert.Throws<IOException>(() => updater.Install(candidate, before.Current.Id, () => { }, _ => { })); Assert.Equal(0, store.Operation);
    }
    private sealed class Verifier : IPackageSignatureVerifier
    {
        public bool Verify(byte[] key, byte[] hash, byte[] signature)
        { using RSA rsa = RSA.Create(); rsa.ImportFromPem(Encoding.ASCII.GetString(key)); return rsa.VerifyHash(hash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss); }
    }
    [Fact]
    public void SignatureCoversKernelAndMetadataAndCannotEnterDataPackageParser()
    {
        using RSA rsa = RSA.Create(2048); byte[] key = Encoding.ASCII.GetBytes(rsa.ExportSubjectPublicKeyInfoPem() + "\n");
        byte[] content = KernelPackage.Content("1.0.0", Elf(2)); byte[] header = Encoding.ASCII.GetBytes("ZENITH-SIGNED-KERNEL\t1\t" + BootLayout.Digest(key) + "\t" + content.Length + "\t256\n");
        byte[] signed = new byte[header.Length + content.Length]; header.CopyTo(signed, 0); content.CopyTo(signed, header.Length);
        byte[] signature = rsa.SignHash(SHA256.HashData(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pss); byte[] archive = new byte[signed.Length + signature.Length]; signed.CopyTo(archive, 0); signature.CopyTo(archive, signed.Length);
        Assert.Equal("1.0.0", KernelPackage.Read(archive, key, new Verifier()).Version);
        Assert.Throws<IOException>(() => SignedPackage.Read(archive, key, new Verifier()));
        archive[header.Length + 10] ^= 1; Assert.Throws<IOException>(() => KernelPackage.Read(archive, key, new Verifier()));
    }
}
