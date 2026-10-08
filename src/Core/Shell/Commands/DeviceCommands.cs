using System;
using System.IO;

namespace Zenith.Core.Shell.Commands;

/// <summary>Bounded binary file/device I/O, independent of Cosmos.</summary>
internal static class DeviceCommands
{
    public static void Register(Action<Command> add)
    {
        add(new Command("dd", "dd if=input of=output count=N [bs=N]", "Copy a bounded number of binary blocks", Copy));
    }

    private static int Copy(CommandContext c)
    {
        string? input = null, output = null;
        int blockSize = 512;
        long count = -1;
        foreach (string arg in c.Args)
        {
            int equal = arg.IndexOf('=');
            if (equal < 1)
            {
                return c.Fail("expected if=input of=output count=N [bs=N]");
            }
            string key = arg.Substring(0, equal), value = arg.Substring(equal + 1);
            switch (key)
            {
                case "if": input = value; break;
                case "of": output = value; break;
                case "bs":
                    if (!int.TryParse(value, out blockSize) || blockSize < 1 || blockSize > 1024 * 1024)
                    {
                        return c.Fail("bs must be between 1 and 1048576 bytes");
                    }
                    break;
                case "count":
                    if (!long.TryParse(value, out count) || count < 0)
                    {
                        return c.Fail("count must be a nonnegative integer");
                    }
                    break;
                default: return c.Fail("unsupported operand: " + key);
            }
        }
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output) || count < 0)
        {
            return c.Fail("expected if=input of=output count=N [bs=N]");
        }
        string sourcePath = c.Resolve(input), targetPath = c.Resolve(output);
        // Current root filesystems are FAT, so case-only aliases name the same file.
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            return c.Fail("input and output must be different paths");
        }

        // Validate all operands and open input before truncating the destination.
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read);
        using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
        byte[] buffer = new byte[blockSize];
        for (long block = 0; block < count; block++)
        {
            c.Shell.ThrowIfCancelled();
            int read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            c.Shell.ThrowIfCancelled();
            target.Write(buffer, 0, read);
        }
        target.Flush();
        return 0;
    }
}
