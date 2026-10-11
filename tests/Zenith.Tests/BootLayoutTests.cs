using System;
using System.IO;
using Zenith.Core.Boot;
using Zenith.Core.Storage;

namespace Zenith.Tests;

public sealed class BootLayoutTests
{
    [Theory]
    [InlineData("root=live", true)]
    [InlineData("root=auto", false)]
    [InlineData("session=headless", false)]
    public void ExplicitLiveAndLegacyAutomaticModes(string text, bool live)
    {
        var selection = BootRootSelection.Parse(text);
        Assert.Equal(live, selection.Live); Assert.Null(selection.PartitionId);
    }
    [Fact]
    public void ExactRootAndGenerationSelectionRejectInvalidOrDuplicateArguments()
    {
        Guid root = Guid.NewGuid(); string id = new('a', 64);
        var selection = BootRootSelection.Parse("root=PARTUUID=" + root + " kernel=" + id + " session=headless");
        Assert.Equal(root, selection.PartitionId); Assert.Equal(id, selection.KernelId);
        foreach (string invalid in new[] { "root=", "root=PARTUUID=bad", "root=live root=auto", "kernel=bad", "kernel=" + id + " kernel=" + id, "root=PARTUUID=" + Guid.Empty })
        { Assert.Throws<IOException>(() => BootRootSelection.Parse(invalid)); }
        Assert.Throws<IOException>(() => BootRootSelection.Parse(new string('x', 4097)));
    }
    [Fact]
    public void VersionedMenuBindsRootAndRetainsPreviousWithoutOverwritingItsPath()
    {
        Guid root = Guid.NewGuid(); string current = new('a', 64), previous = new('b', 64);
        string menu = BootLayout.Configuration(root, current, previous);
        Assert.Contains("path: boot():" + BootLayout.KernelPath(current), menu);
        Assert.Contains("/Zenith previous kernel", menu); Assert.Contains(BootLayout.KernelPath(previous), menu);
        Assert.Contains("root=PARTUUID=" + root, menu); Assert.Contains("root=live session=headless", menu);
        Assert.Throws<IOException>(() => BootLayout.Configuration(root, current, current));
        Assert.Throws<IOException>(() => BootLayout.Configuration(Guid.Empty, current));
        Assert.Throws<IOException>(() => BootLayout.KernelPath(current + "\n"));
    }
    [Fact]
    public void BootPayloadChecksRejectWrongArchitecturesAndMalformedHeaders()
    {
        byte[] kernel = new byte[64]; kernel[0] = 0x7f; kernel[1] = (byte)'E'; kernel[2] = (byte)'L'; kernel[3] = (byte)'F'; kernel[4] = 2; kernel[5] = 1; kernel[18] = 62;
        BootLayout.ValidateKernel(kernel); kernel[18] = 183; Assert.Throws<IOException>(() => BootLayout.ValidateKernel(kernel));
        byte[] efi = new byte[256]; efi[0] = (byte)'M'; efi[1] = (byte)'Z'; efi[60] = 128; efi[128] = (byte)'P'; efi[129] = (byte)'E'; efi[132] = 0x64; efi[133] = 0x86;
        BootLayout.ValidateEfi(efi); efi[60] = 255; Assert.Throws<IOException>(() => BootLayout.ValidateEfi(efi));
        Assert.True(BootLayout.EspBytes > (ulong)(3 * BootLayout.MaximumKernelBytes + BootLayout.MaximumEfiBytes));
    }
    [Fact]
    public void GptIdentityChecksBothChecksumsAndPartitionBounds()
    {
        using var disk = new Ext2FormatterTests.Disk(256UL * 1024 * 1024);
        byte[] header = new byte[512]; BitConverter.TryWriteBytes(header, 0x5452415020494645UL);
        BitConverter.TryWriteBytes(header.AsSpan(8), 0x10000u); BitConverter.TryWriteBytes(header.AsSpan(12), 92u);
        BitConverter.TryWriteBytes(header.AsSpan(24), 1UL); BitConverter.TryWriteBytes(header.AsSpan(72), 2UL);
        BitConverter.TryWriteBytes(header.AsSpan(80), 128u); BitConverter.TryWriteBytes(header.AsSpan(84), 128u);
        byte[] entries = new byte[16384]; Guid esp = Guid.NewGuid(), root = Guid.NewGuid();
        GptIdentity.EspType.TryWriteBytes(entries); esp.TryWriteBytes(entries.AsSpan(16));
        BitConverter.TryWriteBytes(entries.AsSpan(32), 2048UL); BitConverter.TryWriteBytes(entries.AsSpan(40), 264191UL);
        new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4").TryWriteBytes(entries.AsSpan(128)); root.TryWriteBytes(entries.AsSpan(144));
        BitConverter.TryWriteBytes(entries.AsSpan(160), 264192UL); BitConverter.TryWriteBytes(entries.AsSpan(168), 522239UL);
        disk.WriteBlock(1, 1, header); disk.WriteBlock(2, 32, entries); GptChecksums.Complete(disk);
        var identities = GptIdentity.Read(disk); Assert.Equal(2, identities.Count); Assert.Equal(root, identities[1].Id);
        disk.ReadBlock(1, 1, header); header[56] ^= 1; disk.WriteBlock(1, 1, header);
        Assert.Throws<IOException>(() => GptIdentity.Read(disk)); GptChecksums.Complete(disk);
        entries[144] ^= 1; disk.WriteBlock(2, 32, entries); Assert.Throws<IOException>(() => GptIdentity.Read(disk));
        GptChecksums.Complete(disk); Assert.Equal(2, GptIdentity.Read(disk).Count);
        BitConverter.TryWriteBytes(entries.AsSpan(168), disk.BlockCount); disk.WriteBlock(2, 32, entries); GptChecksums.Complete(disk);
        Assert.Throws<IOException>(() => GptIdentity.Read(disk));
    }
}
