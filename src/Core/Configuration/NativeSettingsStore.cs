using System;
using System.IO;
using System.Text;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Vfs;
using Zenith.Core.Storage;

namespace Zenith.Core.Configuration;

internal sealed class NativeSettingsStore : ISettingsStore
{
    public bool Persistent => SystemMounts.Mode == BootMode.Installed;
    public string? Read(string path)
    {
        return BoundedFile.ReadText(path, SettingsService.MaximumBytes);
    }
    public void Replace(string path, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > SettingsService.MaximumBytes) { throw new IOException("configuration exceeds size limit"); }
        string temporary = path + ".new";
        try
        {
            if (File.Exists(temporary)) { File.Delete(temporary); }
            if (!VfsManager.TryCreateFile(temporary, (VfsMode)0x180)
                || !VfsManager.TryOpenFile(temporary, out var handle)) { throw new IOException("cannot stage configuration"); }
            using (handle)
            {
                if (handle.Write(bytes) != bytes.Length || !handle.TryFlush()) { throw new IOException("configuration write failed"); }
            }
            if (!VfsManager.TryRename(temporary, path)) { throw new IOException("configuration replacement failed"); }
            var metadata = new VfsFileMetadata();
            if (metadata.TryRead(path, out var stat) && stat.UnixAttributes && !metadata.SetMode(path, 0x1a4))
            { throw new IOException("cannot set configuration mode"); }
            SystemMounts.SyncAll();
        }
        catch
        {
            try { if (File.Exists(temporary)) { File.Delete(temporary); } } catch { }
            throw;
        }
    }
}
