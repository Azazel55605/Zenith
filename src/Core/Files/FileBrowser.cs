using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Zenith.Core.Files;

internal sealed record FileEntry(string Name, string Path, bool Directory, long? Size);

/// <summary>Plain-.NET file operations shared by the desktop browser and host tests.</summary>
internal sealed class FileBrowser
{
    public string CurrentPath { get; private set; } = "/";
    public List<FileEntry> Entries { get; private set; } = new();

    public static string Normalize(string path, string basis = "/")
    {
        if (!path.StartsWith('/'))
        {
            path = basis.TrimEnd('/') + "/" + path;
        }
        var parts = new List<string>();
        foreach (string part in path.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }

    public void Navigate(string path)
    {
        string resolved = Normalize(path, CurrentPath);
        var entries = new List<FileEntry>();
        foreach (string item in System.IO.Directory.GetFileSystemEntries(resolved))
        {
            bool dir = System.IO.Directory.Exists(item);
            long? size = null;
            if (!dir)
            {
                try { size = new FileInfo(item).Length; }
                catch (IOException) { }
            }
            entries.Add(new FileEntry(System.IO.Path.GetFileName(item), item, dir, size));
        }
        entries.Sort((a, b) => a.Directory != b.Directory ? (a.Directory ? -1 : 1) : string.CompareOrdinal(a.Name, b.Name));
        CurrentPath = resolved;
        Entries = entries;
    }

    public void Refresh() => Navigate(CurrentPath);
    public void Up() => Navigate(Normalize("..", CurrentPath));

    public string Child(string name)
    {
        if (name.Length == 0 || name == "." || name == ".." || Encoding.UTF8.GetByteCount(name) > 255)
            throw new IOException("Enter a name between 1 and 255 bytes.");
        foreach (char c in name)
        {
            if (c < ' ' || c == '/' || c == '\\' || c == ':' || c == '\0')
                throw new IOException("Use a name without path separators or control characters.");
        }
        return Normalize(name, CurrentPath);
    }

    public string Create(string name, bool directory)
    {
        string path = Child(name);
        RequireVacant(path);
        if (directory)
        {
            System.IO.Directory.CreateDirectory(path);
        }
        else using (File.Open(path, FileMode.CreateNew, FileAccess.Write)) { }
        Refresh();
        return path;
    }

    public string Transfer(string source, string name, bool move)
    {
        string target = Child(name);
        RequireVacant(target);
        bool directory = System.IO.Directory.Exists(source);
        if (directory)
        {
            if (!move)
            {
                throw new IOException("Folder copying is not available yet.");
            }
            if (target.StartsWith(source.TrimEnd('/') + "/", StringComparison.Ordinal))
                throw new IOException("A folder cannot be moved inside itself.");
            if (Normalize(System.IO.Path.GetDirectoryName(source) ?? "/") != CurrentPath)
            {
                throw new IOException("Folder moves between directories are not available yet.");
            }
            System.IO.Directory.Move(source, target);
        }
        else if (move) File.Move(source, target);
        else CopyFile(source, target);
        Refresh();
        return target;
    }

    public void Delete(string path)
    {
        // Directory.Delete without recursion refuses nonempty directories.
        if (System.IO.Directory.Exists(path))
        {
            System.IO.Directory.Delete(path, false);
        }
        else
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The selected file no longer exists.", path);
            }
            File.Delete(path);
        }
        Refresh();
    }

    public static void RequireEditable(string path)
    {
        RequireEditableSource(path);
        if (new FileInfo(path).Length > 128 * 1024)
            throw new IOException("Editor opening is limited to 128 KiB here.");
    }

    private static void CopyFile(string source, string target)
    {
        RequireEditableSource(source);
        const int limit = 8 * 1024 * 1024;
        if (new FileInfo(source).Length > limit)
        {
            throw new IOException("File copying is limited to 8 MiB here.");
        }
        using var input = File.OpenRead(source);
        using var output = File.Open(target, FileMode.CreateNew, FileAccess.Write);
        try
        {
            byte[] buffer = new byte[16384];
            int total = 0, read;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                total += read;
                if (total > limit)
                {
                    throw new IOException("File copying is limited to 8 MiB here.");
                }
                output.Write(buffer, 0, read);
            }
        }
        catch
        {
            output.Dispose();
            File.Delete(target);
            throw;
        }
    }

    private static void RequireEditableSource(string path)
    {
        string normalized = Normalize(path);
        if (normalized == "/dev" || normalized.StartsWith("/dev/", StringComparison.Ordinal)
            || normalized == "/proc" || normalized.StartsWith("/proc/", StringComparison.Ordinal)
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Device, virtual and linked files cannot be copied or opened here.");
    }

    private static void RequireVacant(string path)
    {
        if (File.Exists(path) || System.IO.Directory.Exists(path))
            throw new IOException("That name already exists. Choose another name.");
    }
}
