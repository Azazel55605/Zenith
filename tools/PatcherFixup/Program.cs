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
    // DateTime and DateTimeOffset (including filesystem timestamps) share this clock.
    // Apply at the raw RTC seam so SystemClock never recursively calls its own source.
    var clock = module.GetType("Cosmos.Kernel.Plugs.System.DateTimePlug")
        ?? throw new InvalidOperationException("Cosmos DateTime plug changed");
    var ticks = clock.Methods.Single(m => m.Name == "GetCurrentTicks" && m.Parameters.Count == 0);
    if (ticks.ReturnType.FullName != "System.Int64" || !ticks.HasBody)
    { throw new InvalidOperationException("Cosmos clock signature changed"); }
    if (!ticks.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "Adjust"))
    {
        using var support = resolver.Resolve(new AssemblyNameReference("Zenith.Network", new Version(1, 0, 0, 0)));
        var correction = support.MainModule.GetType("Zenith.Core.Network.Transport.ClockCorrection");
        var adjust = module.ImportReference(correction.Methods.Single(m => m.Name == "Adjust"));
        var il = ticks.Body.GetILProcessor();
        foreach (var instruction in ticks.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            // Turn each original return into the call, preserving branch destinations.
            instruction.OpCode = OpCodes.Call; instruction.Operand = adjust;
            il.InsertAfter(instruction, il.Create(OpCodes.Ret));
        }
        changed = true; Console.WriteLine("Adapted Cosmos UTC clock: shared NTP correction");
    }
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
    var transportReference = new AssemblyNameReference("Zenith.Network", new Version(1, 0, 0, 0));
    using var transportSupport = resolver.Resolve(transportReference);
    var guardType = transportSupport.MainModule.GetType("Zenith.Core.Network.Transport.EthernetGuard");
    var handle = module.GetType("Cosmos.Kernel.System.Network.NetworkStack").Methods.Single(m => m.Name == "HandlePacket");
    if (handle.Parameters.Count != 2 || handle.Parameters[0].ParameterType.FullName != "System.Byte[]"
        || handle.Parameters[1].ParameterType.FullName != "System.Int32" || !handle.HasBody)
    { throw new InvalidOperationException("Cosmos Ethernet delivery contract changed"); }
    if (!handle.Body.Instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.Name == "EthernetGuard"))
    {
        var il = handle.Body.GetILProcessor(); var first = handle.Body.Instructions[0];
        var accept = module.ImportReference(guardType.Methods.Single(m => m.Name == "Accept"));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0)); il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Call, accept)); il.InsertBefore(first, il.Create(OpCodes.Brtrue, first));
        il.InsertBefore(first, il.Create(OpCodes.Ret)); handle.Body.MaxStackSize = Math.Max(2, handle.Body.MaxStackSize);
        changed = true; Console.WriteLine("Added bounded Ethernet/IPv4 ingress guard");
    }
    // The legacy DHCP constructor parses unchecked fixed offsets before Zenith's codec.
    // Deliver the checked UDP envelope directly; Zenith parses BOOTP/options afterward.
    var dhcpHandler = module.GetType("Cosmos.Kernel.System.Network.IPv4.DHCP.DhcpPacket").Methods.Single(m => m.Name == "DHCPHandler");
    var udpPacket = module.GetType("Cosmos.Kernel.System.Network.UDP.UdpPacket");
    var dhcpCtor = dhcpHandler.Body.Instructions.Single(i => i.OpCode == OpCodes.Newobj);
    if (dhcpCtor.Operand is MethodReference originalCtor && originalCtor.DeclaringType.Name == "DhcpPacket")
    {
        if (!dhcpHandler.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "ReceiveData" && m.DeclaringType.Name == "UdpClient"))
        { throw new InvalidOperationException("Cosmos DHCP delivery body changed"); }
        dhcpCtor.Operand = udpPacket.Methods.Single(m => m.IsConstructor && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Byte[]");
        foreach (var variable in dhcpHandler.Body.Variables.Where(v => v.VariableType.Name == "DhcpPacket")) { variable.VariableType = udpPacket; } changed = true;
        Console.WriteLine("Adapted DHCP delivery to bounded Zenith codec");
    }
    // Upstream TCP option parsing incorrectly walks payload length, and treats EOL
    // as a length-bearing option. The FSM never reads Options. Guard their lengths
    // at ingress; retain all header/flag fields and omit this unused unsafe parsing.
    var tcpPacket = module.GetType("Cosmos.Kernel.System.Network.TCP.TcpPacket");
    var fields = tcpPacket.Methods.Single(m => m.Name == "InitializeFields");
    var urgent = fields.Body.Instructions.Single(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "_urg");
    if (urgent.Next.OpCode != OpCodes.Ret)
    {
        if (fields.Body.ExceptionHandlers.Count != 0 || !fields.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "get_TcpDataLength"))
        { throw new InvalidOperationException("Cosmos TCP option parser changed"); }
        var il = fields.Body.GetILProcessor();
        while (urgent.Next is not null) { il.Remove(urgent.Next); }
        il.Append(il.Create(OpCodes.Ret)); fields.DebugInformation.SequencePoints.Clear(); changed = true;
        Console.WriteLine("Removed unused unsafe TCP option materialization (ingress validated)");
    }
    var queue = module.ImportReference(transportSupport.MainModule.GetType("Zenith.Core.Network.Transport.BoundedPacketQueue`1"));
    foreach (string clientName in new[] { "Cosmos.Kernel.System.Network.UDP.UdpClient", "Cosmos.Kernel.System.Network.IPv4.IcmpClient" })
    {
        var client = module.GetType(clientName); var field = client.Fields.Single(f => f.Name == "_rxBuffer");
        if (field.FieldType.Name.StartsWith("BoundedPacketQueue", StringComparison.Ordinal)) { continue; }
        if (field.FieldType is not GenericInstanceType old || old.ElementType.FullName != "System.Collections.Generic.Queue`1")
        { throw new InvalidOperationException("Cosmos receive queue changed"); }
        var bounded = new GenericInstanceType(queue); bounded.GenericArguments.Add(old.GenericArguments[0]);
        string oldName = old.FullName; field.FieldType = bounded;
        foreach (var type in module.Types)
        foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is FieldReference f && f.Name == "_rxBuffer" && f.DeclaringType.FullName == clientName) { instruction.Operand = field; }
            if (instruction.Operand is not MethodReference call || call.DeclaringType.FullName != oldName) { continue; }
            if (call.Name is not (".ctor" or "get_Count" or "Enqueue" or "Dequeue" or "Clear"))
            { throw new InvalidOperationException("Cosmos receive queue operation changed: " + call.Name); }
            var replacement = new MethodReference(call.Name, call.ReturnType, bounded) { HasThis = call.HasThis };
            foreach (var parameter in call.Parameters) { replacement.Parameters.Add(new ParameterDefinition(parameter.ParameterType)); }
            instruction.Operand = replacement;
        }
        changed = true; Console.WriteLine("Bounded IRQ-safe receive queue (32 packets): " + clientName);
    }
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
// Imports above can reintroduce the released patcher's redundant self edge.
// Rebind again after all adaptations, immediately before writing the assembly.
foreach (var type in module.GetTypeReferences())
{
    if (type.Scope is AssemblyNameReference reference && reference.Name == assembly.Name.Name)
    { type.Scope = module; }
}
foreach (var reference in module.AssemblyReferences.Where(r => r.Name == assembly.Name.Name).ToArray())
{ module.AssemblyReferences.Remove(reference); changed = true; }
if (!changed) { return; }
string temporary = Path.Combine(Path.GetDirectoryName(path)!, "fixed-" + Path.GetFileName(path));
assembly.Write(temporary, new WriterParameters { WriteSymbols = symbols });
File.Move(temporary, path, true);
if (symbols) { File.Move(Path.ChangeExtension(temporary, ".pdb"), Path.ChangeExtension(path, ".pdb"), true); }
if (self.Length != 0) { Console.WriteLine("Removed redundant self reference: " + assembly.Name.Name); }
