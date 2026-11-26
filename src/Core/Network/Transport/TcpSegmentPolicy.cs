using System.Threading;

namespace Zenith.Core.Network.Transport;

public readonly record struct TcpRejectCounts(long Ahead, long Behind, long Overlap, long Window, long Ambiguous,
                                              long Buffered = 0, long RecoveredBytes = 0)
{
    public TcpRejectCounts Since(TcpRejectCounts before) => new(Ahead - before.Ahead, Behind - before.Behind,
        Overlap - before.Overlap, Window - before.Window, Ambiguous - before.Ambiguous,
        Buffered - before.Buffered, RecoveredBytes - before.RecoveredBytes);

    public string Fields() => "tcp-reject-ahead=" + Ahead + " tcp-reject-behind=" + Behind +
        " tcp-reject-overlap=" + Overlap + " tcp-reject-window=" + Window + " tcp-reject-ambiguous=" + Ambiguous +
        " tcp-buffered-segments=" + Buffered + " tcp-recovered-bytes=" + RecoveredBytes;
}

/// <summary>Admit bounded established data for reassembly. Control segments follow the original contiguous policy.</summary>
public static class TcpSegmentPolicy
{
    private static long s_ahead, s_behind, s_overlap, s_window, s_ambiguous, s_buffered, s_recovered;

    public static TcpRejectCounts Counters() => new(Interlocked.Read(ref s_ahead), Interlocked.Read(ref s_behind),
        Interlocked.Read(ref s_overlap), Interlocked.Read(ref s_window), Interlocked.Read(ref s_ambiguous),
        Interlocked.Read(ref s_buffered), Interlocked.Read(ref s_recovered));

    internal static void Buffered() => Interlocked.Increment(ref s_buffered);
    internal static void Recovered(int bytes) => Interlocked.Add(ref s_recovered, bytes);

    public static bool ReceiveAllowed(uint expected, uint window, uint sequence, uint length, int status, byte flags)
    {
        // ACK or ACK+PSH in ESTABLISHED. SYN, FIN, URG and RST always go through the original checks.
        if (status == 3 && (flags & 0x37) == 0x10 && length > 0 && sequence != expected)
        {
            long start = unchecked((int)(sequence - expected));
            long end = start + length;
            if (start != int.MinValue && end > 0 && end <= window && length <= ushort.MaxValue) { return true; }
        }
        return Accept(expected, window, sequence, length);
    }

    public static bool Accept(uint expected, uint window, uint sequence, uint length)
    {
        if (sequence == expected)
        {
            if (length <= window) { return true; }
            Interlocked.Increment(ref s_window);
        }
        else
        {
            // TCP serial numbers wrap. Exactly half the sequence space has no defined ordering.
            int delta = unchecked((int)(sequence - expected));
            if (delta == int.MinValue) { Interlocked.Increment(ref s_ambiguous); }
            else if (delta > 0) { Interlocked.Increment(ref s_ahead); }
            else if (length > unchecked(expected - sequence)) { Interlocked.Increment(ref s_overlap); }
            else { Interlocked.Increment(ref s_behind); }
        }
        return false;
    }
}
