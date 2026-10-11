using Mono.Cecil;
using Mono.Cecil.Cil;

// Cosmos 3.0.89 imports a redundant AssemblyRef to the target itself. Its initializer
// sorter treats this unused reference as a cycle. Rebind any self TypeRefs and
// remove only this redundant edge, preserving all real assembly dependencies.
string path = Path.GetFullPath(args[0]);
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Environment.CurrentDirectory);
resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
bool symbols = File.Exists(Path.ChangeExtension(path, ".pdb"));
using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = symbols });
var module = assembly.MainModule;
var self = module.AssemblyReferences.Where(r => r.Name == assembly.Name.Name).ToArray();
bool changed = self.Length != 0;
foreach (var type in module.GetTypeReferences())
{
    if (type.Scope is AssemblyNameReference reference && reference.Name == assembly.Name.Name)
    { type.Scope = module; }
}
foreach (var reference in self) { module.AssemblyReferences.Remove(reference); }
if (assembly.Name.Name == "Cosmos.Kernel.Plugs")
{
    // Keep the released descriptor implementation; add authorization before its
    // handle allocation. A denied O_TRUNC otherwise throws while owning a handle
    // that has not yet been wrapped in a SafeFileHandle and cannot be disposed.
    var type = module.GetType("Cosmos.Kernel.Plugs.System.IO.FileDescriptorTable")
        ?? throw new InvalidOperationException("Cosmos PAL descriptor table changed");
    var open = type.Methods.Single(m => m.Name == "Open" && m.Parameters.Count == 4);
    if (open.Parameters[0].ParameterType.FullName != "System.String"
        || open.Parameters[1].ParameterType.FullName != "Interop/Sys/OpenFlags"
        || !open.HasBody) { throw new InvalidOperationException("Cosmos PAL Open signature changed"); }
    if (!open.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "PrepareOpen"))
    {
        var security = new AssemblyNameReference("Zenith.Security", new Version(1, 0, 0, 0));
        module.AssemblyReferences.Add(security);
        var policy = new TypeReference("Zenith.Core.Security", "FileAccessPolicy", module, security);
        var guard = new MethodReference("PrepareOpen", module.TypeSystem.Void, policy) { HasThis = false };
        guard.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        guard.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        var il = open.Body.GetILProcessor();
        var first = open.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Call, guard));
        open.Body.MaxStackSize = Math.Max(open.Body.MaxStackSize, 2);
        changed = true;
        Console.WriteLine("Added PAL open permission precheck");
    }
}
if (!changed) { return; }
string temporary = Path.Combine(Path.GetDirectoryName(path)!, "fixed-" + Path.GetFileName(path));
assembly.Write(temporary, new WriterParameters { WriteSymbols = symbols });
File.Move(temporary, path, true);
if (symbols) { File.Move(Path.ChangeExtension(temporary, ".pdb"), Path.ChangeExtension(path, ".pdb"), true); }
if (self.Length != 0) { Console.WriteLine("Removed redundant self reference: " + assembly.Name.Name); }
