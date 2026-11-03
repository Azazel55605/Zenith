using System;
using System.Collections.Generic;
using System.Globalization;

namespace Zenith.Core.Processes;

/// <summary>Invocation-local CPU affinity; never a credential or service route.</summary>
internal static class ProcessPlacement
{
    internal static uint Parse(IReadOnlyDictionary<string,string>? environment)
    {
        if (environment is null || !environment.TryGetValue("ZENITH_CPU_MASK",out string? value)) return 255;
        bool hex = value is not null && value.StartsWith("0x",StringComparison.OrdinalIgnoreCase);
        if (!uint.TryParse(hex ? value[2..] : value,hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
            CultureInfo.InvariantCulture,out uint mask) || mask is 0 or > 255)
            throw new ArgumentException("ZENITH_CPU_MASK must be a nonzero CPU bit mask (1..255 or 0x01..0xff)");
        return mask;
    }
}
