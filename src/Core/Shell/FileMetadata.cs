using System;

namespace Zenith.Core.Shell;

/// <summary>Filesystem attributes for display; mode/identity may be unavailable on FAT.</summary>
internal readonly record struct FileMetadata(ushort Mode, uint Uid, uint Gid, ulong Links,
    ulong Size, long Mtime, ulong Inode, ulong Blocks, long BlockSize, bool UnixAttributes, string? Target)
{
    public bool IsDirectory => (Mode & 0xf000) == 0x4000;
    public bool IsSymlink => (Mode & 0xf000) == 0xa000;
    public string Type => (Mode & 0xf000) switch
    {
        0x4000 => "directory", 0xa000 => "symbolic link", 0x8000 => "regular file",
        0x2000 => "character device", 0x6000 => "block device", 0x1000 => "fifo",
        0xc000 => "socket", _ => "unknown",
    };
    public string Permissions
    {
        get
        {
            char kind = (Mode & 0xf000) switch
            {
                0x4000 => 'd', 0xa000 => 'l', 0x8000 => '-', 0x2000 => 'c',
                0x6000 => 'b', 0x1000 => 'p', 0xc000 => 's', _ => '?',
            };
            if (!UnixAttributes) { return kind + "?????????"; }
            char[] chars = new char[10]; chars[0] = kind;
            const string bits = "rwxrwxrwx";
            for (int i = 0; i < 9; i++) { chars[i + 1] = (Mode & (1 << (8 - i))) != 0 ? bits[i] : '-'; }
            if ((Mode & 0x800) != 0) { chars[3] = chars[3] == 'x' ? 's' : 'S'; }
            if ((Mode & 0x400) != 0) { chars[6] = chars[6] == 'x' ? 's' : 'S'; }
            if ((Mode & 0x200) != 0) { chars[9] = chars[9] == 'x' ? 't' : 'T'; }
            return new string(chars);
        }
    }
    public string OctalMode => UnixAttributes ? Convert.ToString(Mode & 0xfff, 8).PadLeft(4, '0') : "unknown";
}

internal interface IFileMetadata
{
    // Inspect the final entry without following a symlink.
    bool TryRead(string path, out FileMetadata metadata);
    // Mutations follow the final symlink, matching ordinary chmod/chown behavior.
    bool SetMode(string path, ushort mode);
    bool SetOwner(string path, uint uid, uint? gid);
}
