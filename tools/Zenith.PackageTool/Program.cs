using System.Text;
using Zenith.Core.Packages;

try
{
    if (args.Length == 5 && args[0] == "build")
    {
        string name = args[1], version = args[2], source = Path.GetFullPath(args[3]), output = Path.GetFullPath(args[4]);
        if (!PackageManifest.ValidName(name) || !Directory.Exists(source) || Directory.Exists(output) || File.Exists(output))
        { throw new IOException("invalid package/source or output already exists"); }
        if (output.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        { throw new IOException("output must be outside the payload source"); }
        RealParents(source); RealParents(output);
        RejectLinks(source);
        var files = new List<string>(); int visited = 0; Walk(source, files, ref visited);
        if (files.Count == 0) { throw new IOException("empty package"); }
        files.Sort(StringComparer.Ordinal);
        string temporary = output + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(temporary);
            var manifest = new StringBuilder("ZENITH-PACKAGE\t1\nname\t" + name + "\nversion\t" + version
                + "\narchitecture\tany\nkind\tdata\nruntime\t" + PackageManifest.Runtime + "\nrequires\tzenith-base\t0.1\n");
            int total = 0;
            foreach (string file in files)
            {
                string path = "/" + Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
                if (!PackageManifest.ValidPath(path)) { throw new IOException("unsupported payload path: " + path); }
                byte[] data = ReadBounded(file, PackageManifest.MaximumFileBytes);
                total = checked(total + data.Length);
                if (total > PackageManifest.MaximumPayloadBytes) { throw new IOException("payload exceeds limit"); }
                string target = temporary + "/payload" + path;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, data);
                manifest.Append("file\t").Append(path).Append("\t0644\t").Append(data.Length).Append('\t').Append(PackagePlanner.Digest(data)).Append('\n');
            }
            string text = manifest.ToString(); _ = PackageManifest.Parse(text);
            File.WriteAllText(temporary + "/manifest.tsv", text, new UTF8Encoding(false));
            Directory.Move(temporary, output);
        }
        catch { if (Directory.Exists(temporary)) { Directory.Delete(temporary, true); } throw; }
        Console.WriteLine("built experimental bundle: " + output);
    }
    else if (args.Length == 2 && args[0] == "inspect")
    {
        var manifest = Load(args[1]); Console.WriteLine(manifest.Name + " " + manifest.Version + ": " + manifest.Files.Count + " files, " + manifest.PayloadBytes + " bytes");
    }
    else if (args.Length == 3 && args[0] == "plan")
    {
        string path = Path.GetFullPath(args[1]), root = Path.GetFullPath(args[2]);
        if (LinuxFileType.Read(root) != 0x4000) { throw new IOException("plan requires a real test-root directory"); }
        var manifest = Load(path);
        var plan = PackagePlanner.Plan(manifest, new DirectorySource(Path.GetDirectoryName(path)!), "x86_64", BootstrapComponents.All,
            target => State(Path.Combine(root, target.TrimStart('/'))));
        foreach (string action in plan.Actions) { Console.WriteLine(action); }
        foreach (string blocker in plan.Blockers) { Console.WriteLine("blocked: " + blocker); }
        Console.WriteLine("dry-run only; hashes are not signatures; no target files changed");
        return plan.Applicable ? 0 : 1;
    }
    else { Console.Error.WriteLine("usage: build NAME VERSION SOURCE OUTPUT | inspect MANIFEST | plan MANIFEST TEST_ROOT"); return 2; }
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }

static PackageManifest Load(string path) => PackageManifest.Parse(Encoding.UTF8.GetString(ReadBounded(path, PackageManifest.MaximumBytes)));
static void RejectLinks(string path)
{
    ushort type = LinuxFileType.Read(path);
    if (type != 0x4000 && type != 0x8000) { throw new IOException("symlinks and special nodes are unsupported: " + path); }
}
static void RealParents(string path)
{
    for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
    {
        try { if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) { throw new IOException("symlink ancestor refused: " + parent); } }
        catch (DirectoryNotFoundException) { }
        catch (FileNotFoundException) { }
    }
}
static void Walk(string directory, List<string> files, ref int visited, int depth = 0)
{
    if (depth > 32) { throw new IOException("payload directory nesting exceeds limit"); }
    int entries = 0;
    foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
    {
        if (++entries > 128 || ++visited > 512) { throw new IOException("payload tree exceeds entry limit"); }
        RejectLinks(entry);
        if (Directory.Exists(entry)) { Walk(entry, files, ref visited, depth + 1); }
        else { if (files.Count == PackageManifest.MaximumFiles) { throw new IOException("too many payload files"); } files.Add(entry); }
    }
}
static byte[] ReadBounded(string path, int maximum)
{
    RejectLinks(path);
    if (LinuxFileType.Read(path) != 0x8000) { throw new IOException("expected regular file: " + path); }
    using var stream = File.OpenRead(path);
    if (stream.Length > maximum) { throw new IOException("file exceeds limit: " + path); }
    byte[] data = new byte[checked((int)stream.Length)]; stream.ReadExactly(data);
    if (stream.ReadByte() != -1) { throw new IOException("file grew during read"); }
    return data;
}
static PackagePathState State(string path)
{
    try
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) { return PackagePathState.Occupied; }
        return (attributes & FileAttributes.Directory) != 0 ? PackagePathState.Directory : PackagePathState.Occupied;
    }
    catch (FileNotFoundException) { return PackagePathState.Missing; }
    catch (DirectoryNotFoundException) { return PackagePathState.Missing; }
}
sealed class DirectorySource(string directory) : IPackageSource
{
    public byte[] Read(PackageFile file)
    {
        string path = directory + "/payload" + file.Path;
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        { ushort type = LinuxFileType.Read(current); if (type != 0x4000 && type != 0x8000) { throw new IOException("payload link or special node refused"); } }
        if (LinuxFileType.Read(path) != 0x8000) { throw new IOException("expected regular payload file"); }
        using var stream = File.OpenRead(path);
        if (stream.Length != file.Length) { throw new IOException("payload length mismatch"); }
        byte[] data = new byte[file.Length]; stream.ReadExactly(data);
        if (stream.ReadByte() != -1) { throw new IOException("payload grew during read"); }
        return data;
    }
}
