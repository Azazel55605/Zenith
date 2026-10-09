using System;

namespace Zenith.Core.Shell.Commands;

internal static class MetadataCommands
{
    public static void Register(Action<Command> add)
    {
        add(new Command("chmod", "chmod OCTAL file...", "Set persisted permission bits", Chmod));
        add(new Command("chown", "chown UID[:GID] file...", "Set persisted numeric ownership", Chown));
    }

    private static int Chmod(CommandContext c)
    {
        if (c.Args.Length < 2 || !TryMode(c.Args[0], out ushort mode))
        {
            return c.Fail("usage: chmod OCTAL file... (0000..7777)");
        }
        int status = 0;
        for (int i = 1; i < c.Args.Length; i++)
        {
            if (c.Shell.Metadata is null || !c.Shell.Metadata.SetMode(c.Resolve(c.Args[i]), mode))
            {
                status = c.Fail("cannot change mode of '" + c.Args[i] + "' (requires writable ext2)");
            }
        }
        return status;
    }

    private static int Chown(CommandContext c)
    {
        if (c.Args.Length < 2) { return c.Fail("usage: chown UID[:GID] file..."); }
        string[] ids = c.Args[0].Split(':');
        if (ids.Length > 2 || !TryId(ids[0], out uint uid)
            || (ids.Length == 2 && !TryId(ids[1], out _)))
        {
            return c.Fail("ownership must use numeric UID[:GID]");
        }
        uint? gid = ids.Length == 2 ? uint.Parse(ids[1]) : null;
        int status = 0;
        for (int i = 1; i < c.Args.Length; i++)
        {
            if (c.Shell.Metadata is null || !c.Shell.Metadata.SetOwner(c.Resolve(c.Args[i]), uid, gid))
            {
                status = c.Fail("cannot change owner of '" + c.Args[i] + "' (requires writable ext2)");
            }
        }
        return status;
    }

    private static bool TryId(string text, out uint id)
    {
        id = 0;
        if (text.Length == 0) { return false; }
        foreach (char c in text) { if (c < '0' || c > '9') { return false; } }
        return uint.TryParse(text, out id);
    }

    internal static bool TryMode(string text, out ushort mode)
    {
        mode = 0;
        if (text.Length == 0 || text.Length > 4) { return false; }
        foreach (char c in text)
        {
            if (c < '0' || c > '7') { return false; }
            mode = (ushort)(mode * 8 + c - '0');
        }
        return true;
    }
}
