using System;
using System.IO;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Vfs;
using Zenith.Core.Storage;

namespace Zenith.Core.Security;

internal static class NativeAccessProbe
{
    private static void Denied(Action operation)
    {
        try { operation(); }
        catch (Exception e) { if (e is UnauthorizedAccessException) { return; } throw; }
        throw new IOException("operation escaped permission checks");
    }
    public static string Run(string directory)
    {
        GuardedFilesystem.RequireRoot();
        var metadata = new VfsFileMetadata();
        if (!metadata.TryRead(directory, out var stat) || !stat.UnixAttributes) { throw new IOException("probe requires ext2"); }
        string scratch = directory.TrimEnd('/') + "/.access-probe";
        if (Directory.Exists(scratch)) { throw new IOException("probe directory already exists"); }
        Directory.CreateDirectory(scratch);
        string held = scratch + "/held";
        bool complete = false;
        string stage = "deferred close";
        try
        {
            string deferred = scratch + "/deferred";
            File.WriteAllText(deferred, "pending");
            if (!VfsManager.TryOpenFile(deferred, out var pending)) { throw new IOException("pending test open failed"); }
            File.Delete(deferred);
            using (CredentialContext.Enter(new Credentials(2000,2000))) { pending.Dispose(); }
            if (File.Exists(deferred)) { throw new IOException("authorized deferred unlink did not finish"); }
            File.WriteAllText(deferred, "pending-io");
            var pendingIo = new FileStream(deferred,FileMode.Open,FileAccess.Read,FileShare.ReadWrite | FileShare.Delete);
            File.Delete(deferred);
            using (CredentialContext.Enter(new Credentials(2000,2000))) { pendingIo.Dispose(); }
            if (File.Exists(deferred)) { throw new IOException("System.IO authorized deferred unlink did not finish"); }
            stage = "held denials";
            File.WriteAllText(held, "protected"); metadata.SetMode(held, 0x180);
            using (var handle = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                using (CredentialContext.Enter(new Credentials(1000,1000)))
                {
                    Denied(() => handle.ReadByte());
                    Denied(() => VfsManager.TryUnlink(held));
                    Denied(() => metadata.SetMode(held, 0x1ff));
                }
                if (handle.ReadByte() != 'p') { throw new IOException("denied unlink damaged held data"); }
            }
            if (!File.Exists(held)) { throw new IOException("denied unlink marked handle pending"); }
            stage = "denied-operation cleanup";
            File.Delete(held);
            if (File.Exists(held)) { throw new IOException("denied operation leaked an open handle"); }
            stage = "denied truncation cleanup";
            File.WriteAllText(held, "protected"); metadata.SetMode(held, 0x1a4);
            using (CredentialContext.Enter(new Credentials(1000,1000)))
            { Denied(() => File.WriteAllText(held, "bad")); }
            if (File.ReadAllText(held) != "protected") { throw new IOException("denied truncation changed data"); }
            File.Delete(held);
            if (File.Exists(held)) { throw new IOException("denied truncation leaked an open handle"); }
            stage = "owned creation";
            metadata.SetOwner(scratch,1000,1000); metadata.SetMode(scratch,0x1c0);
            string owned = scratch + "/owned";
            using (CredentialContext.Enter(new Credentials(1000,1000)))
            {
                File.WriteAllText(owned,"owned");
                if (!metadata.TryRead(owned,out var created) || created.Uid != 1000 || created.Gid != 1000) { throw new IOException("creation ownership mismatch"); }
                if (!VfsManager.TryOpenFile(owned,out var file)) { throw new IOException("open failed"); }
                using (file)
                {
                    metadata.SetMode(owned,0);
                    Denied(() => file.Read(new byte[1]));
                    Denied(() => file.Write(new byte[1]));
                    metadata.SetMode(owned,0x180);
                }
            }
            // A nested mount cannot skip an inaccessible parent directory.
            stage = "covered mount traversal";
            string covered = scratch + "/covered"; Directory.CreateDirectory(covered);
            if (!VfsManager.TryMount("proc","",MountFlags.ReadOnly,covered,out _)) { throw new IOException("probe mount failed"); }
            try
            {
                using (CredentialContext.Enter(new Credentials(2000,2000)))
                { Denied(() => File.ReadAllText(covered + "/meminfo")); }
            }
            finally { VfsManager.TryUnmount(covered); }
            complete = true;
            return "access probe passed: held I/O, pending unlink, creation ownership, mount traversal";
        }
        catch (Exception e) { throw new IOException(stage + ": " + e.Message); }
        finally
        {
            try { Directory.Delete(scratch,true); }
            catch (Exception e) { if (complete) { throw; } Zenith.Core.Log.Write("accessprobe", "cleanup: " + e.Message); }
        }
    }
}
