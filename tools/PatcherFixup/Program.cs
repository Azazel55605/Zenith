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
if (assembly.Name.Name == "Cosmos.Kernel.System")
{
    // Adapt only the released TCP receive storage; leave protocol state machinery in Cosmos.
    // Its AppendToData subtracts the consumed offset twice and races partial reads with IRQ
    // appends. Bounded immutable snapshots/CAS remove that race without private UnsafeAccessor.
    var tcp = module.GetType("Cosmos.Kernel.System.Network.TCP.Tcp")
        ?? throw new InvalidOperationException("Cosmos TCP type changed");
    var append = tcp.Methods.Single(m => m.Name == "AppendToData" && m.Parameters.Count == 1);
    var data = tcp.Methods.Single(m => m.Name == "get_Data" && m.Parameters.Count == 0);
    var advance = tcp.Methods.Single(m => m.Name == "AdvanceDataOffset" && m.Parameters.Count == 1);
    var dispose = tcp.Methods.Single(m => m.Name == "Dispose" && m.Parameters.Count == 0);
    var status = tcp.Methods.Single(m => m.Name == "set_Status" && m.Parameters.Count == 1);
    var enumType = tcp.Module.GetType(status.Parameters[0].ParameterType.FullName);
    if (append.Parameters[0].ParameterType.FullName != "System.ReadOnlySpan`1<System.Byte>"
        || data.ReturnType.FullName != "System.ReadOnlySpan`1<System.Byte>"
        || advance.Parameters[0].ParameterType.FullName != "System.Int32"
        || enumType?.Fields.Single(f => f.Name == "CLOSED").Constant is not int closed || closed != 10
        || !append.HasBody || !dispose.HasBody)
    { throw new InvalidOperationException("Cosmos TCP receive contract changed"); }
    if (!tcp.Fields.Any(f => f.Name == "_zenithReceive"))
    {
        var reference = new AssemblyNameReference("Zenith.Network", new Version(1, 0, 0, 0));
        using var support = resolver.Resolve(reference);
        var buffer = support.MainModule.GetType("Zenith.Core.Network.Transport.BoundedTcpBuffer");
        var bridge = support.MainModule.GetType("Zenith.Core.Network.Transport.TcpReceiveBridge");
        var field = new FieldDefinition("_zenithReceive", FieldAttributes.Private, module.ImportReference(buffer)); tcp.Fields.Add(field);
        MethodReference Bridge(string name) => module.ImportReference(bridge.Methods.Single(m => m.Name == name));
        void Replace(MethodDefinition method, string helper, bool argument, bool closeOnOverflow = false)
        {
            method.Body = new MethodBody(method) { MaxStackSize = 3 };
            var il = method.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldflda, field));
            if (argument) { il.Append(il.Create(OpCodes.Ldarg_1)); }
            il.Append(il.Create(OpCodes.Call, Bridge(helper)));
            var end = il.Create(OpCodes.Ret);
            if (closeOnOverflow)
            {
                il.Append(il.Create(OpCodes.Brtrue, end)); il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldc_I4, closed)); il.Append(il.Create(OpCodes.Call, status));
            }
            il.Append(end); method.DebugInformation.SequencePoints.Clear();
        }
        Replace(data, "Data", false); Replace(advance, "Advance", true); Replace(append, "Append", true, true);
        var release = dispose.Body.GetILProcessor(); var first = dispose.Body.Instructions[0];
        release.InsertBefore(first, release.Create(OpCodes.Ldarg_0)); release.InsertBefore(first, release.Create(OpCodes.Ldflda, field));
        release.InsertBefore(first, release.Create(OpCodes.Call, Bridge("Release")));
        dispose.Body.MaxStackSize = Math.Max(dispose.Body.MaxStackSize, 1);
        changed = true; Console.WriteLine("Adapted Cosmos TCP receive buffer: bounded CAS snapshots (128 KiB)");
    }
}
if (!changed) { return; }
string temporary = Path.Combine(Path.GetDirectoryName(path)!, "fixed-" + Path.GetFileName(path));
assembly.Write(temporary, new WriterParameters { WriteSymbols = symbols });
File.Move(temporary, path, true);
if (symbols) { File.Move(Path.ChangeExtension(temporary, ".pdb"), Path.ChangeExtension(path, ".pdb"), true); }
if (self.Length != 0) { Console.WriteLine("Removed redundant self reference: " + assembly.Name.Name); }
