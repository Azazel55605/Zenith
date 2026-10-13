namespace Zenith.Core.Input;

/// <summary>Preserves dedicated navigation identity before layout translation.</summary>
public static class KeyboardScanPolicy
{
    // 0x60 is Cosmos RightAlt; 0x67..0x73 are unused synthetic navigation codes.
    public static byte Ps2(byte scan, bool extended)
    {
        if (!extended) { return scan; }
        if (scan == 0x38) { return 0x60; }
        return IsNavigation(scan) ? (byte)(scan + 0x20) : scan;
    }

    public static byte Virtio(byte scan, ushort code)
    {
        return code switch
        {
            86 => 0x56, 102 => 0x67, 103 => 0x68, 104 => 0x69,
            105 => 0x6b, 106 => 0x6d, 107 => 0x6f, 108 => 0x70,
            109 => 0x71, 110 => 0x72, 111 => 0x73,
            _ => scan
        };
    }

    public static byte LayoutScan(byte scan) => IsNavigation((byte)(scan - 0x20))
        ? (byte)(scan - 0x20) : scan;

    public static bool UseNumLock(byte scan, bool enabled) => enabled
        && scan >= 0x47 && scan <= 0x53;

    private static bool IsNavigation(byte scan) => scan is
        0x47 or 0x48 or 0x49 or 0x4b or 0x4d or 0x4f or 0x50 or 0x51 or 0x52 or 0x53;
}
