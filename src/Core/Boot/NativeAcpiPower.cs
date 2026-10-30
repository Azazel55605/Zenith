#if !ARCH_ARM64
using System;
using System.Runtime.InteropServices;

namespace Zenith.Core.Boot;

/// <summary>
/// Powers off through the firmware's own registers. Cosmos discards LAI's result and
/// then tries only emulator ports before halting, which VMware reports as a halted CPU.
/// </summary>
internal static unsafe class NativeAcpiPower
{
    private const int SpinReads = 2_000_000, MaximumTable = 4 * 1024 * 1024;
    [DllImport("*", EntryPoint = "cosmos_acpi_scan_table")]
    private static extern byte* ScanTable(byte* signature, nuint index);
    [DllImport("*", EntryPoint = "cosmos_acpi_shutdown")]
    private static extern int LaiShutdown();
    [DllImport("*", EntryPoint = "zenith_port_inw")]
    [SuppressGCTransition]
    private static extern ushort InWord(ushort port);
    [DllImport("*", EntryPoint = "zenith_port_outw")]
    [SuppressGCTransition]
    private static extern void OutWord(ushort port, ushort value);
    [DllImport("*", EntryPoint = "zenith_port_outb")]
    [SuppressGCTransition]
    private static extern void OutByte(ushort port, byte value);
    [DllImport("*", EntryPoint = "zenith_interrupts_off")]
    [SuppressGCTransition]
    private static extern void InterruptsOff();

    private static ReadOnlySpan<byte> Table(ReadOnlySpan<byte> signature)
    {
        fixed (byte* name = signature)
        {
            byte* table = ScanTable(name, 0);
            if (table == null) { return default; }
            uint length = *(uint*)(table + 4);
            return length is < 36 or > MaximumTable ? default : new ReadOnlySpan<byte>(table, (int)length);
        }
    }
    private static bool TryPlan(out AcpiPowerPlan plan)
    {
        plan = default;
        try { return AcpiPowerPlan.TryCreate(Table("FACP\0"u8), Table("DSDT\0"u8), out plan); }
        catch (Exception) { return false; }
    }
    /// <summary>Read-only summary, logged while the boot log can still persist it.</summary>
    public static string Describe()
    {
        if (!TryPlan(out var p)) { return "acpi: no usable FADT/_S5; LAI only"; }
        return "acpi: pm1a=" + p.Pm1a.ToString("x") + " pm1b=" + p.Pm1b.ToString("x") + " smi=" + p.SmiCommand.ToString("x")
            + " enable=" + p.AcpiEnable.ToString("x") + " s5=" + p.TypeA + "/" + p.TypeB + " sci=" + (AcpiPowerPlan.Enabled(InWord((ushort)p.Pm1a)) ? 1 : 0);
    }
    /// <summary>Returns only when every firmware path failed; the caller then parks the CPU.</summary>
    public static void PowerOff()
    {
        InterruptsOff();
        if (TryPlan(out var p))
        {
            ushort pm1a = (ushort)p.Pm1a;
            if (AcpiPowerPlan.NeedsEnable(InWord(pm1a), p))
            {
                OutByte((ushort)p.SmiCommand, (byte)p.AcpiEnable);
                for (int i = 0; i < SpinReads && !AcpiPowerPlan.Enabled(InWord(pm1a)); i++) { }
            }
            Log.Write("power", "acpi s5 write: sci=" + (AcpiPowerPlan.Enabled(InWord(pm1a)) ? 1 : 0));
            if (p.Pm1b != 0) { OutWord((ushort)p.Pm1b, AcpiPowerPlan.SleepValue(InWord((ushort)p.Pm1b), p.TypeB)); }
            OutWord(pm1a, AcpiPowerPlan.SleepValue(InWord(pm1a), p.TypeA));
            for (int i = 0; i < SpinReads; i++) { InWord(pm1a); }
            Log.Write("power", "acpi s5 write did not power off");
        }
        Log.Write("power", "lai s5 result: " + LaiShutdown());
    }
}
#endif
