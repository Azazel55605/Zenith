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
    public static void Initialize()
    {
        SystemMounts.Initialize();
        Accounts.Initialize();
        SystemConfiguration.Initialize();
        Network.NetworkService.Initialize();
        SystemCommands.Register(CommandShell.Register);
        CommandShell.DefaultMetadata = new VfsFileMetadata();
        CommandShell.DirectorySearch = path =>
        {
            if (VfsManager.TryOpenDirectory(path, out var directory))
            { using (directory) { FileAccessPolicy.Require(directory.Inode, PermissionPolicy.Search); } }
        };
        Log.Write("system", "shared initialization complete");
    }
}
