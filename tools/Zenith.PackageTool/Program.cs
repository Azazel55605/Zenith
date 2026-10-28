using System.Text;
using Zenith.Core.Packages;
using Zenith.Core.Processes;

try
{
    int? signedResult = SignedCommands.Run(args); if (signedResult.HasValue) { return signedResult.Value; }
    if ((args.Length == 5 || (args.Length == 7 && args[5] == "--repo" && (args[6] is "core" or "extra"))) && args[0] is "build" or "build-native" or "build-meta")
    {
        bool native = args[0] == "build-native";
        bool meta = args[0] == "build-meta";
        string name = args[1], version = args[2], source = Path.GetFullPath(args[3]), output = Path.GetFullPath(args[4]);
        if (!PackageManifest.ValidName(name) || !Directory.Exists(source) || Directory.Exists(output) || File.Exists(output))
        { throw new IOException("invalid package/source or output already exists"); }
        if (output.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        { throw new IOException("output must be outside the payload source"); }
        RealParents(source); RealParents(output);
        RejectLinks(source);
        var files = new List<string>(); int visited = 0;
        if (!meta) { Walk(source, files, ref visited); }
        if (!meta && files.Count == 0) { throw new IOException("empty package"); }
        files.Sort(StringComparer.Ordinal);
        string temporary = output + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(temporary);
            var manifest = new StringBuilder("ZENITH-PACKAGE\t1\nname\t" + name + "\nversion\t" + version
                + "\narchitecture\t" + (native ? "x86_64" : "any") + "\nkind\tdata\nruntime\t" + PackageManifest.Runtime + "\nrequires\tzenith-base\t0.1\n");
            if (native) { manifest.Append("requires\tzenith-native-abi\t1.0\nrequires\tzenith-native-fs-abi\t1.0\nrequires\tzenith-native-io-abi\t1.0\nrequires\tzenith-native-stream-abi\t1.0\nrequires\tzenith-native-process-abi\t1.0\nrequires\tzenith-native-terminal-abi\t1.0\nrequires\tzenith-native-path-abi\t1.0\n"); }
            if (native && name is "zenith-gui-probe" or "zenith-ui-gallery" or "zenith-settings" or "zenith-desktop-tools") { manifest.Append("requires\tzenith-native-graphics-abi\t1.0\n"); }
            if (native && name is "zenith-settings" or "zenith-desktop-tools") { manifest.Append("requires\tzenith-native-interactive-abi\t1.0\nrequires\tzenith-native-system-info-abi\t1.0\nrequires\tzenith-native-viewport-abi\t1.0\nrequires\tzenith-native-background-abi\t1.0\n"); }
            if (native && name == "zenith-settings") { manifest.Append("requires\tzenith-native-settings-abi\t1.0\nrequires\tzenith-native-window-state-abi\t1.0\n"); }
            if (native && name == "zenith-net-tools") { manifest.Append("requires\tzenith-native-network-abi\t1.0\n"); }
            if (meta)
            {
                manifest.Replace("ZENITH-PACKAGE\t1\n", "ZENITH-PACKAGE\t3\nrepository\t" + (args.Length == 7 ? args[6] : "extra") + "\n");
                manifest.Replace("kind\tdata", "kind\tmeta");
                manifest.Append("requires\tzenith-package-meta-abi\t1.0\n");
                string requirements = Encoding.UTF8.GetString(ReadBounded(Path.Combine(source, "requires.tsv"), 4096));
                if (!requirements.EndsWith('\n') || requirements.Split('\n').Any(line => line.Length > 0 && !line.StartsWith("requires\t", StringComparison.Ordinal)))
                { throw new IOException("meta definition requires only newline-terminated dependency records"); }
                manifest.Append(requirements);
            }
            int total = 0, executables = 0;
            foreach (string file in files)
            {
                string path = "/" + Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
                if (!PackageManifest.ValidPath(path)) { throw new IOException("unsupported payload path: " + path); }
                byte[] data = ReadBounded(file, PackageManifest.MaximumFileBytes);
                if (native && path.EndsWith(".elf", StringComparison.Ordinal)) { _ = ElfImage.Read(data); executables++; }
                total = checked(total + data.Length);
                if (total > PackageManifest.MaximumPayloadBytes) { throw new IOException("payload exceeds limit"); }
                string target = temporary + "/payload" + path;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, data);
                manifest.Append("file\t").Append(path).Append(native && path.EndsWith(".elf", StringComparison.Ordinal) ? "\t0755\t" : "\t0644\t").Append(data.Length).Append('\t').Append(PackagePlanner.Digest(data)).Append('\n');
            }
            if (native && executables == 0) { throw new IOException("native package requires a valid Zenith ABI executable"); }
            string text = manifest.ToString();
            if (args.Length == 7) { text = text.Replace("ZENITH-PACKAGE\t1\n", "ZENITH-PACKAGE\t2\nrepository\t" + args[6] + "\n"); }
            _ = PackageManifest.Parse(text);
            File.WriteAllText(temporary + "/manifest.tsv", text, new UTF8Encoding(false));
            Directory.Move(temporary, output);
        }
        catch { if (Directory.Exists(temporary)) { Directory.Delete(temporary, true); } throw; }
        Console.WriteLine("built experimental bundle: " + output);
    }
    else if (args.Length == 2 && args[0] == "native-check")
    { var image = ElfImage.Read(ReadBounded(args[1], ElfImage.MaximumBytes)); Console.WriteLine("verified native ABI 1 entry=" + image.Entry + " segments=" + image.Segments.Count); }
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
    else { Console.Error.WriteLine("usage: index-seal REPO GENERATION ISSUED_UNIX EXPIRES_UNIX PUBLICATION_ROOT PRIVATE_PEM OUTPUT.zri | index-check FILE PUBLIC_PEM REPO | kernel-seal VERSION ELF PRIVATE_PEM OUTPUT.zpk | kernel-check ARCHIVE PUBLIC_PEM | keygen KEY_DIR | seal BUNDLE PRIVATE_PEM OUTPUT.zpk | check ARCHIVE PUBLIC_PEM | build NAME VERSION SOURCE OUTPUT [--repo core|extra] | inspect MANIFEST | plan MANIFEST TEST_ROOT"); return 2; }
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
