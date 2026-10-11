using System.Runtime.InteropServices;

// Linux host tooling only. statx's fixed UAPI layout is defined in linux/stat.h.
// Inspect type without opening: opening a FIFO could otherwise block the builder.
internal static class LinuxFileType
{
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Query(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out Statx stat);
    public static ushort Read(string path)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("P0 package tooling currently requires Linux"); }
        if (Query(-100, path, 0x100, 1, out var stat) != 0 || (stat.Mask & 1) == 0)
        { throw new IOException("cannot stat package input: " + path); }
        return (ushort)(stat.Mode & 0xf000);
    }
}
