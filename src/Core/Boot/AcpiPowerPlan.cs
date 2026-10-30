using System;
using System.Buffers.Binary;

namespace Zenith.Core.Boot;

/// <summary>Registers and sleep types for ACPI S5, read from bounded firmware tables.</summary>
public readonly record struct AcpiPowerPlan(int Pm1a, int Pm1b, int SmiCommand, int AcpiEnable, int TypeA, int TypeB)
{
    private const int FadtSmiCommand = 48, FadtAcpiEnable = 52, FadtPm1a = 64, FadtPm1b = 68, FadtMinimum = 72, TableHeader = 36;
    private const ushort SciEnable = 1, SleepEnable = 1 << 13, SleepTypeMask = 7 << 10;

    public static bool TryCreate(ReadOnlySpan<byte> fadt, ReadOnlySpan<byte> dsdt, out AcpiPowerPlan plan)
    {
        plan = default;
        if (fadt.Length < FadtMinimum || !TryReadS5(dsdt, out int a, out int b)) { return false; }
        uint pm1a = BinaryPrimitives.ReadUInt32LittleEndian(fadt[FadtPm1a..]), pm1b = BinaryPrimitives.ReadUInt32LittleEndian(fadt[FadtPm1b..]);
        uint smi = BinaryPrimitives.ReadUInt32LittleEndian(fadt[FadtSmiCommand..]);
        if (pm1a is 0 or > 0xffff || pm1b > 0xffff || smi > 0xffff) { return false; }
        plan = new((int)pm1a, (int)pm1b, (int)smi, fadt[FadtAcpiEnable], a, b); return true;
    }

    /// <summary>Firmware may leave legacy mode active; it then needs an explicit SMI request.</summary>
    public static bool NeedsEnable(ushort pm1Control, AcpiPowerPlan plan)
        => (pm1Control & SciEnable) == 0 && plan.SmiCommand != 0 && plan.AcpiEnable != 0;
    public static bool Enabled(ushort pm1Control) => (pm1Control & SciEnable) != 0;
    public static ushort SleepValue(ushort pm1Control, int type)
        => (ushort)((pm1Control & ~SleepTypeMask & ~SleepEnable) | ((type & 7) << 10) | SleepEnable);

    /// <summary>Finds a Name(_S5_, Package) definition without an AML interpreter.</summary>
    public static bool TryReadS5(ReadOnlySpan<byte> dsdt, out int typeA, out int typeB)
    {
        typeA = typeB = 0;
        if (dsdt.Length <= TableHeader) { return false; }
        for (int i = TableHeader; i + 4 < dsdt.Length; i++)
        {
            if (dsdt[i] != '_' || dsdt[i + 1] != 'S' || dsdt[i + 2] != '5' || dsdt[i + 3] != '_') { continue; }
            bool named = dsdt[i - 1] == 0x08 || (dsdt[i - 1] == 0x5C && dsdt[i - 2] == 0x08);
            if (named && TryReadPackage(dsdt[(i + 4)..], out typeA, out typeB)) { return true; }
        }
        typeA = typeB = 0; return false;
    }

    private static bool TryReadPackage(ReadOnlySpan<byte> p, out int a, out int b)
    {
        a = b = 0;
        if (p.Length < 2 || p[0] != 0x12) { return false; }
        int at = 2 + (p[1] >> 6);      // PkgLength: the top bits count its following bytes.
        if (at >= p.Length) { return false; }
        at++;                          // NumElements
        return TryReadByte(p, ref at, out a) && TryReadByte(p, ref at, out b);
    }

    private static bool TryReadByte(ReadOnlySpan<byte> p, ref int at, out int value)
    {
        value = 0;
        if (at >= p.Length) { return false; }
        byte op = p[at++];
        if (op == 0x0A) { if (at >= p.Length) { return false; } value = p[at++]; }
        else if (op is 0x00 or 0x01) { value = op; }
        else { return false; }
        return value <= 7;
    }
}
