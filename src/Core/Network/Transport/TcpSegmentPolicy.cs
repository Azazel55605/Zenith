namespace Zenith.Core.Network.Transport;

/// <summary>No reorder queue: accept only contiguous bytes and acknowledge every payload, irrespective of PSH.</summary>
public static class TcpSegmentPolicy
{
    public static bool Accept(uint expected, uint window, uint sequence, uint length)
        => sequence == expected && length <= window;
}
