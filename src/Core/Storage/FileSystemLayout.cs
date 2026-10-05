using System.IO;

namespace MyOS.Core.Storage;

/// <summary>
/// The Unix directory hierarchy and the default files under <c>/etc</c>. Creating it is
/// idempotent: existing files are never overwritten, so it is safe on every boot.
/// </summary>
internal static class FileSystemLayout
{
    public const string OsReleasePath = "/etc/os-release";

    private static readonly string[] s_directories =
    {
        "/bin", "/boot", "/dev", "/etc", "/home", "/home/user", "/mnt", "/proc", "/root",
        "/tmp", "/usr", "/usr/share", "/usr/share/doc", "/var", "/var/log",
    };

    public static void Create(string root = "")
    {
        foreach (string dir in s_directories)
        {
            Directory.CreateDirectory(root + dir);
        }

        WriteIfMissing(root + OsReleasePath,
            "NAME=\"MyOS\"\nID=myos\nPRETTY_NAME=\"MyOS (Cosmos Gen 3)\"\nVERSION_ID=0.1\n");
        WriteIfMissing(root + "/etc/hostname", "myos\n");
        WriteIfMissing(root + "/etc/passwd",
            "root:x:0:0:root:/root:/bin/sh\nuser:x:1000:1000:User:/home/user:/bin/sh\n");
        WriteIfMissing(root + "/etc/group", "root:x:0:\nusers:x:100:user\n");
        WriteIfMissing(root + "/etc/motd", "Welcome to MyOS. Type 'help' to list commands.\n");
        WriteIfMissing(root + "/etc/profile", "export PATH=/bin\nexport EDITOR=notes\n");
        WriteIfMissing(root + "/home/user/readme.txt",
            "This is your home directory.\nFiles here persist when MyOS runs from an installed disk.\n");
    }

    /// <summary>Whether the filesystem mounted at <paramref name="root"/> carries a MyOS installation.</summary>
    public static bool IsMyOsRoot(string root = "")
    {
        string path = root + OsReleasePath;
        return File.Exists(path) && File.ReadAllText(path).Contains("ID=myos");
    }

    private static void WriteIfMissing(string path, string content)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, content);
        }
    }
}
