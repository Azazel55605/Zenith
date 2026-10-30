using System;
using Zenith.Core.Boot;

namespace Zenith.Tests;

public sealed class AcpiPowerPlanTests
{
    private static byte[] Dsdt(params byte[] body)
    {
        var d = new byte[36 + body.Length + 8];
        "DSDT"u8.CopyTo(d); BitConverter.TryWriteBytes(d.AsSpan(4), d.Length);
        body.CopyTo(d.AsSpan(36)); return d;
    }
    private static readonly byte[] S5 = { 0x08, (byte)'_', (byte)'S', (byte)'5', (byte)'_', 0x12, 0x08, 0x04, 0x0A, 0x05, 0x0A, 0x06, 0x00, 0x00 };
    private static byte[] Fadt(uint pm1a = 0x1004, uint pm1b = 0, uint smi = 0xB2, byte enable = 0xF0)
    {
        var f = new byte[244]; "FACP"u8.CopyTo(f); BitConverter.TryWriteBytes(f.AsSpan(4), f.Length);
        BitConverter.TryWriteBytes(f.AsSpan(48), smi); f[52] = enable;
        BitConverter.TryWriteBytes(f.AsSpan(64), pm1a); BitConverter.TryWriteBytes(f.AsSpan(68), pm1b);
        return f;
    }
    [Fact]
    public void ReadsS5PackageFromCommonAmlEncodings()
    {
        Assert.True(AcpiPowerPlan.TryReadS5(Dsdt(S5), out int a, out int b)); Assert.Equal((5, 6), (a, b));
        // Root-prefixed name, zero/one opcodes and a two-byte package length.
        byte[] rooted = { 0x08, 0x5C, (byte)'_', (byte)'S', (byte)'5', (byte)'_', 0x12, 0x46, 0x00, 0x04, 0x00, 0x01, 0x00, 0x00 };
        Assert.True(AcpiPowerPlan.TryReadS5(Dsdt(rooted), out a, out b)); Assert.Equal((0, 1), (a, b));
    }
    [Fact]
    public void RefusesMalformedOrMissingS5()
    {
        Assert.False(AcpiPowerPlan.TryReadS5(Dsdt(1, 2, 3), out _, out _));
        Assert.False(AcpiPowerPlan.TryReadS5(new byte[4], out _, out _));
        // A bare string in a comment-like position is not a Name definition.
        byte[] notName = { 0x00, (byte)'_', (byte)'S', (byte)'5', (byte)'_', 0x12, 0x08, 0x04, 0x0A, 0x05, 0x0A, 0x06 };
        Assert.False(AcpiPowerPlan.TryReadS5(Dsdt(notName), out _, out _));
        // Truncated right after the package header, and out-of-range sleep types.
        var truncated = Dsdt(S5[..7]); Array.Resize(ref truncated, 36 + 7);
        BitConverter.TryWriteBytes(truncated.AsSpan(4), truncated.Length);
        Assert.False(AcpiPowerPlan.TryReadS5(truncated, out _, out _));
        byte[] large = (byte[])S5.Clone(); large[9] = 8;
        Assert.False(AcpiPowerPlan.TryReadS5(Dsdt(large), out _, out _));
        // Declared length beyond the buffer never reads past it.
        var lying = Dsdt(S5); BitConverter.TryWriteBytes(lying.AsSpan(4), 1 << 20);
        Assert.True(AcpiPowerPlan.TryReadS5(lying, out _, out _));
    }
    [Fact]
    public void PlanCarriesRegistersAndComputesTheSleepWrite()
    {
        Assert.True(AcpiPowerPlan.TryCreate(Fadt(), Dsdt(S5), out var p));
        Assert.Equal(new AcpiPowerPlan(0x1004, 0, 0xB2, 0xF0, 5, 6), p);
        // Preserve unrelated bits, replace SLP_TYP and set SLP_EN.
        Assert.Equal((ushort)(0x0001 | (5 << 10) | (1 << 13)), AcpiPowerPlan.SleepValue(0x1C01, 5));
        Assert.True(AcpiPowerPlan.NeedsEnable(0, p)); Assert.False(AcpiPowerPlan.NeedsEnable(1, p));
        Assert.False(AcpiPowerPlan.TryCreate(Fadt(pm1a: 0), Dsdt(S5), out _));
        Assert.False(AcpiPowerPlan.TryCreate(Fadt(pm1a: 0x10000), Dsdt(S5), out _));
        Assert.False(AcpiPowerPlan.TryCreate(new byte[40], Dsdt(S5), out _));
        Assert.True(AcpiPowerPlan.TryCreate(Fadt(smi: 0, enable: 0), Dsdt(S5), out var hardwareOnly));
        Assert.False(AcpiPowerPlan.NeedsEnable(0, hardwareOnly));    // no SMI path: nothing to request
    }
}
