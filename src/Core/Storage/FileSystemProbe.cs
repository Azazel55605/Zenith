using System;
using System.IO;

namespace Zenith.Core.Storage;

/// <summary>Small opt-in I/O/lifetime probe in a unique temporary directory.</summary>
internal static class FileSystemProbe
{
    public static string Run(string directory)
    {
        if (!Directory.Exists(directory)) { throw new IOException("probe directory does not exist"); }
        string scratch = Path.Combine(directory, ".zenith-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string path = Path.Combine(scratch, "held");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
            bool deferred;
            using (var first = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            using (var second = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                File.Delete(path);
                deferred = File.Exists(path);
                first.Position = 1;
                first.WriteByte(42);
                first.Flush();
                second.Position = 0;
                byte[] data = new byte[4];
                second.ReadExactly(data);
                if (data[0] != 1 || data[1] != 42 || data[2] != 3 || data[3] != 4)
                { throw new IOException("open handles lost data after unlink"); }
                // Closing one handle must not reclaim storage under the other.
                first.Dispose();
                second.Position = 1;
                if (second.ReadByte() != 42) { throw new IOException("data lost before final close"); }
            }
            if (File.Exists(path)) { throw new IOException("entry survived final close"); }
            File.WriteAllBytes(path, new byte[] { 9, 8, 7 });
            if (File.ReadAllBytes(path)[0] != 9) { throw new IOException("inode reuse failed"); }
            File.Delete(path);
            return "open-unlink: " + (deferred ? "deferred until final close" : "entry removed immediately")
                + "; shared I/O, final-close deletion and reuse passed";
        }
        finally
        {
            Directory.Delete(scratch, true);
        }
    }
}
