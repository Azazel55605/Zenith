using System;
using Zenith.Core.Network;
using Zenith.Core.Security;
namespace Zenith.Core.Processes;

internal static class NativeSettingsInformation
{
    public static byte[] Snapshot(uint kind) => ProcessSettingsInformation.Encode(kind, kind switch
    {
        1 => NetworkService.Status(),
        2 => Accounts.Service?.PublicSummary() ?? "Account storage unavailable on this boot.\n",
        _ => throw new ArgumentException("unknown settings information kind")
    });
}
