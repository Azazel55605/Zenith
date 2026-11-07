namespace Zenith.Core.Memory;

/// <summary>Collect before the managed heap consumes pages needed by native allocations.</summary>
internal sealed class MemoryPressurePolicy
{
    private long _nextCheck;
    private ulong _allocated;
    private int _collections = -1;
    public const ulong AllocationBudget = 32UL * 1024 * 1024;

    public bool ShouldCollect(ulong totalPages, ulong freePages, long timestamp, long frequency,
        ulong allocatedBytes = 0, int collections = 0)
    {
        // Debt is cumulative allocation, not live heap size. External collections
        // reset it too; refused collections retain debt for a bounded retry.
        if (_collections != collections || allocatedBytes < _allocated)
        { _collections = collections; _allocated = allocatedBytes; }
        if (totalPages == 0 || frequency <= 0 || timestamp < _nextCheck)
        {
            return false;
        }

        // Keep one eighth of physical memory available for stacks and DMA, but
        // do not postpone collecting allocation debt until a large VM is full.
        // Limit attempts to one per second if live data
        // prevents reclamation; a collection cannot guarantee enough free memory.
        if (freePages > totalPages / 8 && allocatedBytes - _allocated < AllocationBudget)
        {
            return false;
        }

        _nextCheck = timestamp + frequency;
        return true;
    }
}
