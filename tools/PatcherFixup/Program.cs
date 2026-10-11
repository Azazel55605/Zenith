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
    // 3.0.89 bypasses ARP for limited broadcasts but sends their zero-filled
    // Ethernet destination unchanged. Slirp accepts this; VMware did not answer.
    // Set the L2 broadcast address before enqueueing, retaining the unicast path.
    var outgoing = module.GetType("Cosmos.Kernel.System.Network.IPv4.OutgoingBuffer")
        ?? throw new InvalidOperationException("Cosmos outgoing IPv4 buffer changed");
    var enqueue = outgoing.Methods.Single(m => m.Name == "AddPacket" && m.Parameters.Count == 2);
    if (!enqueue.IsStatic || !enqueue.HasBody
        || enqueue.Parameters[0].ParameterType.FullName != "Cosmos.Kernel.System.Network.IPv4.IPPacket"
        || enqueue.Parameters[1].ParameterType.FullName != "Cosmos.Kernel.HAL.Interfaces.Devices.INetworkDevice"
        || !enqueue.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "set_SourceMac"))
    { throw new InvalidOperationException("Cosmos outgoing IPv4 enqueue contract changed"); }
    if (!enqueue.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "set_DestinationMac"))
    {
        var entry = outgoing.NestedTypes.Single(t => t.Name == "BufferEntry");
        var constructor = entry.Methods.Single(m => m.IsConstructor);
        var send = outgoing.Methods.Single(m => m.Name == "Send");
        MethodReference Call(MethodDefinition method, string name) =>
            method.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().First(m => m.Name == name);
        var destination = Call(constructor, "get_DestinationIP");
        var isBroadcast = Call(constructor, "get_IsBroadcastAddress");
        var broadcastMac = Call(send, "get_Broadcast");
        var setDestination = Call(send, "set_DestinationMac");
        if (broadcastMac.ReturnType.FullName != "Cosmos.Kernel.HAL.Interfaces.Devices.MACAddress"
            || isBroadcast.ReturnType.FullName != "System.Boolean")
        { throw new InvalidOperationException("Cosmos broadcast address contract changed"); }
        var il = enqueue.Body.GetILProcessor(); var first = enqueue.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Callvirt, destination));
        il.InsertBefore(first, il.Create(OpCodes.Callvirt, isBroadcast));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Call, broadcastMac));
        il.InsertBefore(first, il.Create(OpCodes.Callvirt, setDestination));
        enqueue.Body.MaxStackSize = Math.Max(2, enqueue.Body.MaxStackSize);
        changed = true; Console.WriteLine("Fixed IPv4 limited-broadcast Ethernet destination");
    }
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
    // 3.0.89 appends ACK-only payload but does not ACK it, exhausting the peer's
    // window on large responses. Route all nonempty payload through the ACK path.
    var established = tcp.Methods.Single(m => m.Name == "ProcessEstablished" && m.Parameters.Count == 1);
    var receive = tcp.Methods.Single(m => m.Name == "ReceiveDataInternal" && m.Parameters.Count == 1);
    var packetType = module.GetType("Cosmos.Kernel.System.Network.TCP.TcpPacket");
    var payloadLength = packetType.Methods.Single(m => m.Name == "get_TcpDataLength");
    var push = established.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference f && f.Name == "_psh").ToArray();
    if (push.Length == 1)
    {
        if (push[0].Next.OpCode != OpCodes.Brfalse) { throw new InvalidOperationException("Cosmos TCP payload branch changed"); }
        var il = established.Body.GetILProcessor(); push[0].OpCode = OpCodes.Callvirt; push[0].Operand = payloadLength;
        var zero = il.Create(OpCodes.Ldc_I4_0); il.InsertAfter(push[0], zero); il.InsertAfter(zero, il.Create(OpCodes.Cgt_Un));
        foreach (var instruction in established.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "[TCP] PSH received, data length: "))
        { instruction.Operand = "[TCP] Payload received, data length: "; }
        changed = true;
    }
    else if (push.Length != 0 || !established.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "[TCP] Payload received, data length: "))
    { throw new InvalidOperationException("Cosmos TCP payload ACK contract changed"); }
    if (!receive.Body.Instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.Name == "TcpSegmentPolicy"))
    {
        var instructions = receive.Body.Instructions;
        var firstRead = instructions.First(i => i.Operand is MethodReference m && m.Name == "get_RcvNxt");
        var lastRead = instructions.First(i => i.Operand is MethodReference m && m.Name == "get_RcvWnd");
        var first = firstRead.Previous.Previous;
        var end = lastRead.Next.Next.Next;
        var range = instructions.Skip(instructions.IndexOf(first)).Take(instructions.IndexOf(end) - instructions.IndexOf(first)).ToArray();
        var lower = range.Single(i => i.OpCode == OpCodes.Bgt_Un); var upper = range.Single(i => i.OpCode == OpCodes.Bge_Un);
        if (!ReferenceEquals(lower.Operand, upper.Operand) || range.Count(i => i.Operand is MethodReference m && m.Name == "get_RcvNxt") != 2
            || range.Count(i => i.Operand is MethodReference m && m.Name == "get_SequenceNumber") != 2 || first.OpCode != OpCodes.Ldarg_0)
        { throw new InvalidOperationException("Cosmos TCP sequence/window contract changed"); }
        var tcb = tcp.Methods.Single(m => m.Name == "get_TCB");
        var next = (MethodReference)firstRead.Operand; var window = (MethodReference)lastRead.Operand;
        var sequence = packetType.Methods.Single(m => m.Name == "get_SequenceNumber");
        var policy = transportSupport.MainModule.GetType("Zenith.Core.Network.Transport.TcpSegmentPolicy");
        var accept = module.ImportReference(policy.Methods.Single(m => m.Name == "Accept"));
        var il = receive.Body.GetILProcessor(); first.OpCode = OpCodes.Nop; first.Operand = null;
        foreach (var instruction in range.Skip(1)) { il.Remove(instruction); }
        var replacement = new[] { il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, tcb), il.Create(OpCodes.Callvirt, next),
            il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, tcb), il.Create(OpCodes.Callvirt, window),
            il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Callvirt, sequence), il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Callvirt, payloadLength),
            il.Create(OpCodes.Call, accept), il.Create(OpCodes.Brfalse, (Instruction)lower.Operand) };
        var previous = first; foreach (var instruction in replacement) { il.InsertAfter(previous, instruction); previous = instruction; }
        receive.Body.MaxStackSize = System.Math.Max(receive.Body.MaxStackSize, 4); changed = true;
        Console.WriteLine("Adapted TCP contiguous sequence/window validation and ACK-only payload delivery");
    }
    var tcbType = module.GetType("Cosmos.Kernel.System.Network.TCP.TransmissionControlBlock");
    var receiveWindow = tcbType.Methods.Single(m => m.Name == "get_RcvWnd");
    foreach (var method in tcp.Methods.Where(m => m.Name == "SendEmptyPacket"))
    {
        var original = method.Body.Instructions.Where(i => i.Operand is MethodReference m && m.Name == "get_SndWnd").ToArray();
        if (original.Length == 1)
        {
            original[0].Operand = receiveWindow; method.Body.GetILProcessor().InsertAfter(original[0], Instruction.Create(OpCodes.Conv_U2)); changed = true;
        }
        else if (original.Length != 0 || !method.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "get_RcvWnd"))
        { throw new InvalidOperationException("Cosmos TCP advertised-window contract changed"); }
    }
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
        var control = tcp.Methods.Single(m => m.Name == "get_TCB");
        var setWindow = tcbType.Methods.Single(m => m.Name == "set_RcvWnd");
        int ackFlag = System.Convert.ToInt32(module.GetType("Cosmos.Kernel.System.Network.TCP.TcpFlags").Fields.Single(f => f.Name == "ACK").Constant);
        if (ackFlag != 16) { throw new InvalidOperationException("Cosmos TCP ACK flag changed"); }
        var sendAck = tcp.Methods.Single(m => m.Name == "SendEmptyPacket" && m.Parameters.Count == 1);
        void Replace(MethodDefinition method, string helper, bool argument, bool closeOnOverflow = false, bool acknowledge = false)
        {
            method.Body = new MethodBody(method) { MaxStackSize = 3 };
            var il = method.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldflda, field));
            if (argument) { il.Append(il.Create(OpCodes.Ldarg_1)); }
            il.Append(il.Create(OpCodes.Call, Bridge(helper)));
            if (closeOnOverflow)
            {
                var success = il.Create(OpCodes.Ldarg_0); il.Append(il.Create(OpCodes.Brtrue, success));
                il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldc_I4, closed)); il.Append(il.Create(OpCodes.Call, status)); il.Append(il.Create(OpCodes.Ret));
                il.Append(success); il.Append(il.Create(OpCodes.Call, control));
                il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldflda, field)); il.Append(il.Create(OpCodes.Call, Bridge("Window"))); il.Append(il.Create(OpCodes.Callvirt, setWindow));
            }
            if (acknowledge)
            {
                il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Call, control));
                il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldflda, field)); il.Append(il.Create(OpCodes.Call, Bridge("Window"))); il.Append(il.Create(OpCodes.Callvirt, setWindow));
                var end = il.Create(OpCodes.Ret);
                il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Call, tcp.Methods.Single(m => m.Name == "get_Status")));
                il.Append(il.Create(OpCodes.Ldc_I4_3)); il.Append(il.Create(OpCodes.Bne_Un, end));
                il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldc_I4, ackFlag)); il.Append(il.Create(OpCodes.Call, sendAck)); il.Append(end);
            }
            il.Append(il.Create(OpCodes.Ret)); method.DebugInformation.SequencePoints.Clear();
        }
        Replace(data, "Data", false); Replace(advance, "Advance", true, acknowledge: true); Replace(append, "Append", true, true);
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
