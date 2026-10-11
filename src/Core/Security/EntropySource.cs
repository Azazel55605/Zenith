using System;

namespace Zenith.Core.Security;

internal interface IEntropySource
{
    bool TryFill(Span<byte> destination);
}

internal delegate bool SeedWord(out ulong word);

/// <summary>Bounded all-or-nothing extraction; never substitutes timers or a PRNG.</summary>
internal sealed class SeedEntropySource : IEntropySource
{
    private readonly SeedWord _word;
    public SeedEntropySource(SeedWord word) => _word = word;

    public bool TryFill(Span<byte> destination)
    {
        if (destination.Length > 4096) { destination.Clear(); return false; }
        for (int offset = 0; offset < destination.Length; offset += 8)
        {
            ulong word = 0;
            bool available = false;
            for (int attempt = 0; attempt < 256; attempt++)
            {
                if (_word(out word)) { available = true; break; }
            }
            if (!available) { destination.Clear(); return false; }
            for (int i = 0; i < 8 && offset + i < destination.Length; i++)
            {
                destination[offset + i] = (byte)(word >> (i * 8));
            }
        }
        return true;
    }
}
