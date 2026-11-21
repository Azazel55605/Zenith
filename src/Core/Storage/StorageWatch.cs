using System;

namespace Zenith.Core.Storage;

/// <summary>Warns once when the system disk is almost full (U14 notifications): below 5 % or 256 MiB free. It warns
/// again only after space recovered above 10 % and 512 MiB, so a disk hovering at the edge does not repeat itself.</summary>
internal static class StorageWatch
{
    public const ulong LowBytes = 256UL << 20, ClearBytes = 512UL << 20;
    public const long IntervalMicros = 60_000_000;
    /// <summary>Free and total bytes of the system disk, or null when unknown; set at boot, replaced by tests.</summary>
    public static Func<(ulong Free, ulong Total)?> Root = () => null;
    private static bool s_warned;
    private static long s_checked;
    private static bool s_ever;

    /// <summary>Called often (the desktop's tick); looks at the disk at most once a minute. True when it warned.</summary>
    public static bool Check(long nowMicros)
    {
        if (s_ever && nowMicros - s_checked < IntervalMicros) { return false; }
        s_checked = nowMicros; s_ever = true;
        if (Root() is not var (free, total) || total == 0) { return false; }
        bool low = free < LowBytes || free * 100 < total * 5;
        bool clear = free >= ClearBytes && free * 100 >= total * 10;
        if (s_warned) { if (clear) { s_warned = false; } return false; }
        if (!low) { return false; }
        s_warned = true;
        string left = free >= 1UL << 30 ? (free >> 30) + " GB" : free >= 1UL << 20 ? (free >> 20) + " MB" : (free >> 10) + " KB";
        Zenith.Core.Notifications.NotificationCenter.FromSystem("Storage", "Disk almost full",
            "Only " + left + " left on the system disk. Remove files or packages you no longer need.", "hard-drive",
            new Zenith.Core.Notifications.NotificationCenter.NoteAction("Open Files", "Horizon", Array.Empty<string>()),
            new Zenith.Core.Notifications.NotificationCenter.NoteAction("Software", "Orbit", new[] { "installed" }));
        return true;
    }
    internal static void Reset() { s_warned = false; s_checked = 0; s_ever = false; }
}
