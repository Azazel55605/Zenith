using Zenith.Core.Storage;
if (args.Length != 1) { throw new ArgumentException("supply package payload root"); }
string directory = Path.Combine(Path.GetFullPath(args[0]), "usr/share/zenith/man");
Directory.CreateDirectory(directory);
foreach (var (name, text) in ManualPages.Pages) { File.WriteAllText(Path.Combine(directory, name + ".txt"), text + "\n"); }
Console.WriteLine("Emitted " + ManualPages.Pages.Length + " independent manual pages");
