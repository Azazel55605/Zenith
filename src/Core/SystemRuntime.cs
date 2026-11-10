using Zenith.Core.Configuration;
using Zenith.Core.Security;
using Zenith.Core.Shell.Commands;
using Zenith.Core.Storage;
using CommandShell = Zenith.Core.Shell.Shell;
using VfsManager = Cosmos.Kernel.System.Vfs.VfsManager;

namespace Zenith.Core;

/// <summary>Shared boot phase, independent of desktop allocation and app startup.</summary>
internal static class SystemRuntime
{
    /// <param name="phase">Told as each startup phase begins, after the disks for the first one.</param>
    public static void Initialize(System.Action<string>? phase = null)
    {
        SystemMounts.Initialize();
        phase?.Invoke("Loading accounts");
        Accounts.Initialize();
        phase?.Invoke("Loading settings");
        SystemConfiguration.Initialize();
        phase?.Invoke("Starting network");
        Network.NetworkService.Initialize();
        // An installer ISO's seed: pin its package key, TLS CA and mirror in the live /etc.
        Packages.NativeSeed.ApplyLive();
        SystemCommands.Register(CommandShell.Register);
        NativeProcessCommands.Register(CommandShell.Register);
        CommandShell.DefaultMetadata = new VfsFileMetadata();
        CommandShell.DirectorySearch = path =>
        {
            if (VfsManager.TryOpenDirectory(path, out var directory))
            { using (directory) { FileAccessPolicy.Require(directory.Inode, PermissionPolicy.Search); } }
        };
        Log.Write("system", "shared initialization complete");
    }
}
