using System.IO;
using Cosmos.Kernel.System.Vfs;
using Zenith.Core.Network;
using Zenith.Core.Security;

namespace Zenith.Core.Storage;

internal static class DownloadSpace
{
    // Query the opened staging inode: parent symlinks and mounts can put it on a different volume.
    public static void Check(IVfsFileHandle staged, int bytes)
    {
        if (bytes == 0) { return; }
        if (!GuardedFilesystem.TryStatFs(staged.Inode, out var stat) || stat.BlockSize == 0)
        { throw new IOException("cannot determine free space on download destination"); }
        if (1 + ((ulong)bytes - 1) / stat.BlockSize > stat.Bavail) { throw new DownloadSpaceException(); }
    }
}
