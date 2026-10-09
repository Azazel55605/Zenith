namespace Zenith.Core.Memory;

/// <summary>Collect before the managed heap consumes pages needed by native allocations.</summary>
internal sealed class MemoryPressurePolicy
{
    private long _nextCheck;

    public bool ShouldCollect(ulong totalPages, ulong freePages, long timestamp, long frequency)
    {
        if (totalPages == 0 || frequency <= 0 || timestamp < _nextCheck)
        {
            return false;
        }

        // Keep one eighth of physical memory available for stacks and DMA. Polling
        // itself does not allocate. Limit attempts to one per second if live data
        // prevents reclamation; a collection cannot guarantee enough free memory.
        if (freePages > totalPages / 8)
        {
            return false;
        }

        _nextCheck = timestamp + frequency;
        return true;
    }
}
