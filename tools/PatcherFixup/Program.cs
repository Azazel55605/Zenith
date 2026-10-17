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
void QuietTrace(MethodDefinition method, string firstText, string lastText)
{
    var code = method.Body.Instructions;
    var first = code.Single(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == firstText);
    if (first.Next.OpCode == OpCodes.Pop) { return; }
    var last = code.Skip(code.IndexOf(first)).First(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == lastText);
    var end = last.Next;
    while (end is not null && !(end.Operand is MethodReference m && m.DeclaringType.Name == "Serial" && m.Name == "WriteString"))
    { end = end.Next; }
    if (end is null || end.OpCode != OpCodes.Call) { throw new InvalidOperationException("Cosmos trace contract changed"); }
    var range = code.Skip(code.IndexOf(first)).Take(code.IndexOf(end) - code.IndexOf(first) + 1).ToArray();
    if (range.Any(i => i.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw
        || i.OpCode == OpCodes.Stfld || i.OpCode == OpCodes.Stsfld)
        || code.Except(range).Any(i => i.Operand is Instruction target && range.Contains(target) && target != first))
    { throw new InvalidOperationException("Cosmos trace has unexpected effects/control flow"); }
    // Retain the first literal for idempotent adaptation checks, consuming it
    // immediately; branch destinations remain valid instruction objects.
    range[1].OpCode = OpCodes.Pop; range[1].Operand = null;
    foreach (var instruction in range.Skip(2)) { instruction.OpCode = OpCodes.Nop; instruction.Operand = null; }
    changed = true;
}
if (assembly.Name.Name == "Cosmos.Kernel.Core")
{
    var allocator = module.GetType("Cosmos.Kernel.Core.Memory.PageAllocator")
        ?? throw new InvalidOperationException("Cosmos allocator type changed");
    var pages = allocator.Methods.Single(m => m.Name == "AllocPages" && m.Parameters.Count == 3);
    if (!pages.HasBody || pages.ReturnType.FullName != "System.Void*"
        || pages.Parameters[1].ParameterType.FullName != "System.UInt64"
        || pages.Parameters[2].ParameterType.FullName != "System.Boolean")
    { throw new InvalidOperationException("Cosmos allocator signature changed"); }
    QuietTrace(pages, "[PageAllocator] AllocPages - Type: ", "\n");
    Console.WriteLine("Quieted pinned per-allocation success trace; allocator/error paths retained");
}
if (assembly.Name.Name == "Cosmos.Kernel.Core.X64")
{
    // Released load_idt unconditionally STI's before PIC/APIC setup. During PIC
    // ICW remapping a pending IRQ0 can then arrive as vector 0 and halt the boot.
    // Preserve the caller's IF instead; the system initializer explicitly enables
    // interrupts after platform/handler setup, before interrupt-driven disk I/O.
    var native = module.GetType("Cosmos.Kernel.Core.X64.Bridge.IdtNative")
        ?? throw new InvalidOperationException("Cosmos IDT native import changed");
    var load = native.Methods.Single(m => m.Name == "LoadIdt");
    if (!load.IsStatic || !load.HasPInvokeInfo || load.ReturnType.FullName != "System.Void"
        || load.Parameters.Count != 1 || load.Parameters[0].ParameterType.FullName != "System.Void*"
        || load.PInvokeInfo.Module.Name != "*"
        || (load.PInvokeInfo.EntryPoint != "_native_x64_load_idt"
            && load.PInvokeInfo.EntryPoint != "zenith_load_idt_preserve_irq"))
    { throw new InvalidOperationException("Cosmos IDT loader signature changed"); }
    if (load.PInvokeInfo.EntryPoint == "_native_x64_load_idt")
    {
        load.PInvokeInfo.EntryPoint = "zenith_load_idt_preserve_irq";
        changed = true; Console.WriteLine("Adapted IDT loader: preserve interrupt state until platform setup completes");
    }
}
if (assembly.Name.Name == "Cosmos.Kernel.Plugs")
{
    var socketPlug = module.GetType("Cosmos.Kernel.Plugs.System.Net.Sockets.SocketPlug")
        ?? throw new InvalidOperationException("Cosmos socket plug changed");
    // CloseTcp does not retire SYN_SENT registrations. Mark a timed-out connect
    // CLOSED before throwing so the owner's explicit Close removes its table
    // entry and local port. Only adapt the pinned failure path in build copies.
    var connectTcp = socketPlug.Methods.Single(m => m.Name == "ConnectTcp" && m.Parameters.Count == 3);
    var connectFailure = connectTcp.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr
        && (string)i.Operand == "Failed to open TCP connection!");
    var tcpLocal = connectTcp.Body.Variables.Single(v => v.VariableType.FullName == "Cosmos.Kernel.System.Network.TCP.Tcp");
    var statusSetter = connectTcp.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
        .First(m => m.Name == "set_Status" && m.DeclaringType.FullName == tcpLocal.VariableType.FullName);
    var statusType = statusSetter.Parameters.Single().ParameterType.Resolve();
    int closedStatus = Convert.ToInt32(statusType.Fields.Single(f => f.Name == "CLOSED").Constant);
    if (connectFailure.Previous?.Operand is not MethodReference preceding || preceding.FullName != statusSetter.FullName)
    {
        if (connectFailure.Next.OpCode != OpCodes.Newobj || connectFailure.Next.Next.OpCode != OpCodes.Throw
            || !connectTcp.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "WaitStatus"))
        { throw new InvalidOperationException("Cosmos TCP connect timeout contract changed"); }
        var il = connectTcp.Body.GetILProcessor();
        // Mutate the original target too, so branches cannot bypass retirement.
        connectFailure.OpCode = OpCodes.Ldloc; connectFailure.Operand = tcpLocal;
        var status = il.Create(OpCodes.Ldc_I4, closedStatus); il.InsertAfter(connectFailure, status);
        var setter = il.Create(OpCodes.Callvirt, statusSetter); il.InsertAfter(status, setter);
        il.InsertAfter(setter, il.Create(OpCodes.Ldstr, "Failed to open TCP connection!"));
        changed = true; Console.WriteLine("Adapted TCP connect timeout: retire failed registration on close");
    }
    else if (connectFailure.Previous.Previous.OpCode != OpCodes.Ldc_I4
        || (int)connectFailure.Previous.Previous.Operand != closedStatus
        || connectFailure.Previous.Previous.Previous.OpCode != OpCodes.Ldloc
        || connectFailure.Previous.Previous.Previous.Operand != tcpLocal)
    { throw new InvalidOperationException("Cosmos TCP timeout retirement contract changed"); }
    // Packet headers advertise our receive capacity, never the peer's send window.
    var sendTcp = socketPlug.Methods.Single(m => m.Name == "SendTcp" && m.Parameters.Count == 4);
    if (!sendTcp.HasBody || sendTcp.ReturnType.FullName != "System.Int32")
    { throw new InvalidOperationException("Cosmos SendTcp signature changed"); }
    var sendWindows = sendTcp.Body.Instructions.Where(i => i.Operand is MethodReference m
        && m.Name == "get_SndWnd").ToArray();
    if (sendWindows.Length == 2)
    {
        var il = sendTcp.Body.GetILProcessor();
        foreach (var instruction in sendWindows)
        {
            var original = (MethodReference)instruction.Operand;
            var receive = original.DeclaringType.Resolve().Methods.Single(m => m.Name == "get_RcvWnd");
            if (original.ReturnType.FullName != "System.UInt16" || receive.ReturnType.FullName != "System.UInt32"
                || instruction.Next.OpCode != OpCodes.Ldc_I4_0)
            { throw new InvalidOperationException("Cosmos SendTcp window contract changed"); }
            instruction.Operand = module.ImportReference(receive);
            il.InsertAfter(instruction, il.Create(OpCodes.Conv_U2));
        }
        changed = true; Console.WriteLine("Adapted TCP payload headers: local receive window");
    }
    else if (sendWindows.Length != 0 || sendTcp.Body.Instructions.Count(i => i.Operand is MethodReference m
        && m.Name == "get_RcvWnd" && i.Next.OpCode == OpCodes.Conv_U2) != 2)
    { throw new InvalidOperationException("Cosmos SendTcp window sites changed"); }
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
if (assembly.Name.Name == "Cosmos.Kernel.HAL.X64")
{
    // Firmware can supply MAC bytes without an enabled exact-match filter. The
    // fast ReadMacAddress return skipped setting RAH0.AV, losing unicast replies.
    var device = module.GetType("Cosmos.Kernel.HAL.X64.Devices.Network.E1000E")
        ?? throw new InvalidOperationException("Cosmos E1000E driver changed");
    var readMac = device.Methods.Single(m => m.Name == "ReadMacAddress");
    var read = device.Methods.Single(m => m.Name == "ReadMmio");
    var write = device.Methods.Single(m => m.Name == "WriteMmio");
    if (!readMac.HasBody || readMac.Parameters.Count != 0 || readMac.ReturnType.FullName != "System.Void"
        || read.Parameters.Count != 1 || read.Parameters[0].ParameterType.FullName != "System.UInt32"
        || read.ReturnType.FullName != "System.UInt32" || write.Parameters.Count != 2
        || write.Parameters.Any(p => p.ParameterType.FullName != "System.UInt32")
        || write.ReturnType.FullName != "System.Void"
        || System.Convert.ToUInt32(device.Fields.Single(f => f.Name == "REG_RAH0").Constant) != 0x5404u)
    { throw new InvalidOperationException("Cosmos E1000E receive address contract changed"); }
    if (!readMac.Body.Instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.Name == "E1000ReceiveFilter"))
    {
        using var support = resolver.Resolve(new AssemblyNameReference("Zenith.Network", new Version(1, 0, 0, 0)));
        var activate = module.ImportReference(support.MainModule.GetType("Zenith.Core.Network.Transport.E1000ReceiveFilter")
            .Methods.Single(m => m.Name == "Activate"));
        var il = readMac.Body.GetILProcessor();
        foreach (var ret in readMac.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            // Preserve existing branch destinations by changing the original ret.
            ret.OpCode = OpCodes.Ldarg_0; ret.Operand = null;
            var cursor = ret;
            foreach (var instruction in new[] {
                il.Create(OpCodes.Ldc_I4, 0x5404), il.Create(OpCodes.Ldarg_0),
                il.Create(OpCodes.Ldc_I4, 0x5404), il.Create(OpCodes.Call, read),
                il.Create(OpCodes.Call, activate), il.Create(OpCodes.Call, write), il.Create(OpCodes.Ret) })
            { il.InsertAfter(cursor, instruction); cursor = instruction; }
        }
        readMac.Body.MaxStackSize = Math.Max(4, readMac.Body.MaxStackSize);
        changed = true; Console.WriteLine("Activated E1000E exact-match receive address on all MAC-read returns");
    }
    var irq = device.Methods.Single(m => m.Name == "HandleIRQ");
    if (!irq.IsStatic || !irq.HasBody
        || System.Convert.ToUInt32(device.Fields.Single(f => f.Name == "REG_ICR").Constant) != 0xc0u
        || System.Convert.ToUInt32(device.Fields.Single(f => f.Name == "ICR_RXT0").Constant) != 0x80u
        || System.Convert.ToUInt32(device.Fields.Single(f => f.Name == "ICR_RXDMT0").Constant) != 0x10u)
    { throw new InvalidOperationException("Cosmos E1000E interrupt contract changed"); }
    if (!irq.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "ObserveInterrupt"))
    {
        var cause = irq.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "ReadMmio"
            && i.Previous.OpCode == OpCodes.Ldc_I4 && i.Previous.Operand is int value && value == 0xc0);
        using var support = resolver.Resolve(new AssemblyNameReference("Zenith.Network", new Version(1, 0, 0, 0)));
        var observe = module.ImportReference(support.MainModule.GetType("Zenith.Core.Network.Transport.E1000ReceiveFilter")
            .Methods.Single(m => m.Name == "ObserveInterrupt"));
        var il = irq.Body.GetILProcessor(); var duplicate = il.Create(OpCodes.Dup);
        il.InsertAfter(cause, duplicate); il.InsertAfter(duplicate, il.Create(OpCodes.Call, observe));
        irq.Body.MaxStackSize = Math.Max(2, irq.Body.MaxStackSize);
        changed = true; Console.WriteLine("Added read-only E1000E IRQ/receive-cause counters");
    }
    var drain = device.Methods.Single(m => m.Name == "ProcessReceivedPackets");
    var initialize = device.Methods.Single(m => m.Name == "InitializeNetwork");
    int ringSize = Convert.ToInt32(device.Fields.Single(f => f.Name == "RxDescCount").Constant);
    if (!drain.HasBody || drain.IsStatic || drain.Parameters.Count != 0
        || drain.ReturnType.FullName != "System.Void" || !initialize.HasBody || initialize.IsStatic
        || initialize.Parameters.Count != 0 || initialize.ReturnType.FullName != "System.Void" || ringSize != 32)
    { throw new InvalidOperationException("Cosmos E1000E receive polling contract changed"); }
    if (!initialize.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "RegisterPoll"))
    {
        // Both IRQ and poll draining are bounded to one ring per invocation.
        var il = drain.Body.GetILProcessor();
        var back = drain.Body.Instructions.Single(i => (i.OpCode == OpCodes.Br || i.OpCode == OpCodes.Br_S)
            && i.Operand is Instruction target && target.Offset < i.Offset);
        var loop = (Instruction)back.Operand;
        var exit = drain.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret);
        var budget = new VariableDefinition(module.TypeSystem.Int32);
        drain.Body.Variables.Add(budget); drain.Body.InitLocals = true;
        var start = il.Create(OpCodes.Ldloc, budget);
        foreach (var instruction in new[] { start, il.Create(OpCodes.Ldc_I4, ringSize),
            il.Create(OpCodes.Bge, exit), il.Create(OpCodes.Ldloc, budget), il.Create(OpCodes.Ldc_I4_1),
            il.Create(OpCodes.Add), il.Create(OpCodes.Stloc, budget) })
        { il.InsertBefore(loop, instruction); }
        foreach (var instruction in drain.Body.Instructions)
        { if (instruction.Operand == loop) { instruction.Operand = start; } }
        drain.Body.MaxStackSize = Math.Max(2, drain.Body.MaxStackSize);

        using var support = resolver.Resolve(new AssemblyNameReference("Zenith.Network", new Version(1, 0, 0, 0)));
        var register = module.ImportReference(support.MainModule.GetType("Zenith.Core.Network.Transport.E1000ReceiveFilter")
            .Methods.Single(m => m.Name == "RegisterPoll"));
        var action = register.Parameters[0].ParameterType.Resolve();
        var constructor = module.ImportReference(action.Methods.Single(m => m.IsConstructor && m.Parameters.Count == 2));
        il = initialize.Body.GetILProcessor();
        foreach (var ret in initialize.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            ret.OpCode = OpCodes.Ldarg_0; ret.Operand = null;
            var cursor = ret;
            foreach (var instruction in new[] { il.Create(OpCodes.Ldftn, drain),
                il.Create(OpCodes.Newobj, constructor), il.Create(OpCodes.Call, register), il.Create(OpCodes.Ret) })
            { il.InsertAfter(cursor, instruction); cursor = instruction; }
        }
        initialize.Body.MaxStackSize = Math.Max(2, initialize.Body.MaxStackSize);
        changed = true; Console.WriteLine("Registered bounded E1000E receive polling independent of NIC interrupts");
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
    // Change only candidate generation. The pinned allocator still excludes active
    // local ports and bounds collision probes; build copies alone are modified.
    var portTcp = module.GetType("Cosmos.Kernel.System.Network.TCP.Tcp")
        ?? throw new InvalidOperationException("Cosmos TCP type changed");
    var allocate = portTcp.Methods.Single(m => m.Name == "GetDynamicPort" && m.Parameters.Count == 1);
    var portInstructions = allocate.Body.Instructions;
    var seeded = portInstructions.Where(i => i.Operand is MethodReference m
        && m.DeclaringType.FullName == "Zenith.Core.Network.Transport.TcpPortAllocation" && m.Name == "Next").ToArray();
    if (seeded.Length == 0)
    {
        var first = portInstructions.First(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference f && f.Name == "s_nextPort");
        var range = portInstructions.Skip(portInstructions.IndexOf(first)).Take(12).ToArray();
        var expected = new[] { OpCodes.Ldsfld, OpCodes.Dup, OpCodes.Ldc_I4_1, OpCodes.Add, OpCodes.Conv_U2,
            OpCodes.Stsfld, OpCodes.Stloc_1, OpCodes.Ldsfld, OpCodes.Ldc_I4, OpCodes.Blt_S, OpCodes.Ldsfld, OpCodes.Stsfld };
        if (allocate.ReturnType.FullName != "System.UInt16" || allocate.Parameters[0].ParameterType.FullName != "System.Int32"
            || range.Length != expected.Length || !range.Select(i => i.OpCode).SequenceEqual(expected)
            || (int)range[8].Operand != 65535 || ((FieldReference)range[10].Operand).Name != "DynamicPortStart"
            || new[] { 0, 5, 7, 11 }.Any(index => ((FieldReference)range[index].Operand).FullName != ((FieldReference)first.Operand).FullName)
            || portInstructions.Except(range).Any(i => i.Operand is Instruction target && range.Skip(1).Contains(target))
            || !portInstructions.Any(i => i.Operand is MethodReference m && m.Name == "get_Connections")
            || !portInstructions.Any(i => i.Operand is MethodReference m && m.Name == "get_Port"))
        { throw new InvalidOperationException("Cosmos TCP port allocation contract changed"); }
        var support = transportSupport.MainModule.GetType("Zenith.Core.Network.Transport.TcpPortAllocation")
            ?? throw new InvalidOperationException("Seeded TCP port allocator missing");
        first.OpCode = OpCodes.Call; first.Operand = module.ImportReference(support.Methods.Single(m => m.Name == "Next"));
        foreach (var instruction in range.Skip(1).Where(i => i != range[6]))
        { instruction.OpCode = OpCodes.Nop; instruction.Operand = null; }
        changed = true; Console.WriteLine("Adapted TCP port candidates: entropy-seeded dynamic-port sequence");
    }
    else if (seeded.Length != 1 || seeded[0].Next?.OpCode != OpCodes.Nop
        || !portInstructions.Any(i => i.Operand is MethodReference m && m.Name == "get_Connections"))
    { throw new InvalidOperationException("Seeded TCP port allocation contract changed"); }
    var guardType = transportSupport.MainModule.GetType("Zenith.Core.Network.Transport.EthernetIngress");
    var handle = module.GetType("Cosmos.Kernel.System.Network.NetworkStack").Methods.Single(m => m.Name == "HandlePacket");
    if (handle.Parameters.Count != 2 || handle.Parameters[0].ParameterType.FullName != "System.Byte[]"
        || handle.Parameters[1].ParameterType.FullName != "System.Int32" || !handle.HasBody)
    { throw new InvalidOperationException("Cosmos Ethernet delivery contract changed"); }
    var existingGuard = handle.Body.Instructions.FirstOrDefault(i => i.Operand is MethodReference m && m.DeclaringType.Name == "EthernetGuard");
    if (existingGuard is not null)
    { existingGuard.Operand = module.ImportReference(guardType.Methods.Single(m => m.Name == "Accept")); changed = true; }
    if (!handle.Body.Instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.Name == "EthernetIngress"))
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
    // Successful-packet tracing performs hundreds of port writes in the RX path.
    // Remove only pinned informational statements; preserve rejection/error logs.
    QuietTrace(handle, "[NetworkStack] HandlePacket called, len=", "\n");
    QuietTrace(handle, "[NetworkStack] EtherType: 0x", "\n");
    QuietTrace(handle, "[NetworkStack] -> IPv4\n", "[NetworkStack] -> IPv4\n");
    var ipv4 = module.GetType("Cosmos.Kernel.System.Network.IPv4.IPPacket").Methods.Single(m => m.Name == "IPv4Handler");
    QuietTrace(ipv4, "[IP] IPv4Handler called\n", "[IP] IPv4Handler called\n");
    QuietTrace(ipv4, "[IP] From ", "\n");
    QuietTrace(established, "[TCP] Payload received, data length: ", ", storing data\n");
    QuietTrace(established, "[TCP] Data buffer now has ", " bytes\n");
    QuietTrace(outgoing.Methods.Single(m => m.Name == "Send"), "[OutgoingBuffer] Sent via ARP cache\n", "[OutgoingBuffer] Sent via ARP cache\n");
    if (!tcp.Fields.Any(f => f.Name == "_zenithQuietPacketTrace"))
    {
        var packetTrace = receive.Body.Instructions.First(i => i.Operand is MethodReference m
            && m.DeclaringType.Name == "Serial" && m.Name == "WriteString");
        if (packetTrace.OpCode != OpCodes.Call || ((MethodReference)packetTrace.Operand).Parameters.Count != 1)
        { throw new InvalidOperationException("Cosmos TCP packet trace contract changed"); }
        packetTrace.OpCode = OpCodes.Pop; packetTrace.Operand = null; changed = true;
        tcp.Fields.Add(new FieldDefinition("_zenithQuietPacketTrace", FieldAttributes.Private | FieldAttributes.Static
            | FieldAttributes.Literal | FieldAttributes.HasDefault, module.TypeSystem.Boolean) { Constant = true });
    }
    // Skipping only WriteString still formats TcpPacket.ToString for every frame.
    // Remove the pinned first, side-effect-free interpolated trace expression too.
    if (!tcp.Fields.Any(f => f.Name == "_zenithQuietPacketFormatting"))
    {
        var traceEnd = receive.Body.Instructions.First(i => i.OpCode == OpCodes.Pop);
        var trace = receive.Body.Instructions.Take(receive.Body.Instructions.IndexOf(traceEnd) + 1).ToArray();
        if (!trace.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "] ")
            || trace.Any(i => i.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw
                || i.OpCode == OpCodes.Stfld || i.OpCode == OpCodes.Stsfld))
        { throw new InvalidOperationException("Cosmos TCP packet formatting contract changed"); }
        foreach (var instruction in trace) { instruction.OpCode = OpCodes.Nop; instruction.Operand = null; }
        tcp.Fields.Add(new FieldDefinition("_zenithQuietPacketFormatting", FieldAttributes.Private | FieldAttributes.Static
            | FieldAttributes.Literal | FieldAttributes.HasDefault, module.TypeSystem.Boolean) { Constant = true });
        changed = true;
    }
    Console.WriteLine("Quieted pinned successful IPv4/TCP packet traces; errors remain enabled");
}
// Keyboard adaptations apply only to build copies of pinned Cosmos 3.0.89.
if (assembly.Name.Name == "Cosmos.Kernel.System")
{
    var kernel = module.GetType("Cosmos.Kernel.System.Kernel")
        ?? throw new InvalidOperationException("Cosmos kernel lifecycle changed");
    var lifecycle = kernel.Methods.Single(m => m.Name == "Start" && m.Parameters.Count == 0);
    QuietTrace(lifecycle, "[Kernel] Calling Run()...\n", "[Kernel] Calling Run()...\n");
    QuietTrace(lifecycle, "[Kernel] Run() returned\n", "[Kernel] Run() returned\n");
    Console.WriteLine("Quieted pinned per-tick success traces; lifecycle and failures retained");
}
if (assembly.Name.Name is "Cosmos.Kernel.System" or "Cosmos.Kernel.HAL.X64" or "Cosmos.Kernel.HAL")
{
    using var support = resolver.Resolve(new AssemblyNameReference("Zenith.Network", new Version(1, 0, 0, 0)));
    var policy = support.MainModule.GetType("Zenith.Core.Input.KeyboardScanPolicy")
        ?? throw new InvalidOperationException("Keyboard support policy missing");
    MethodReference Policy(string name) => module.ImportReference(policy.Methods.Single(m => m.Name == name));
    if (assembly.Name.Name == "Cosmos.Kernel.System")
    {
        var scan = module.GetType("Cosmos.Kernel.System.Keyboard.ScanMapBase").Methods
            .Single(m => m.Name == "ConvertScanCode" && m.Parameters.Count == 7);
        if (scan.Parameters[0].ParameterType.FullName != "System.Byte" || scan.Parameters[4].Name != "numLock" || !scan.HasBody)
        { throw new InvalidOperationException("Keyboard layout signature changed"); }
        if (!scan.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "UseNumLock"))
        {
            var il = scan.Body.GetILProcessor(); var first = scan.Body.Instructions[0];
            foreach (var instruction in new[] {
                il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Ldarg, scan.Parameters[4]),
                il.Create(OpCodes.Call, Policy("UseNumLock")), il.Create(OpCodes.Starg, scan.Parameters[4]),
                il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Call, Policy("LayoutScan")),
                il.Create(OpCodes.Starg, scan.Parameters[0]) }) { il.InsertBefore(first, instruction); }
            changed = true;
        }
    }
    if (assembly.Name.Name == "Cosmos.Kernel.HAL.X64")
    {
        var irq = module.GetType("Cosmos.Kernel.HAL.X64.Devices.Input.PS2Keyboard").Methods.Single(m => m.Name == "StaticHandleIRQ");
        if (!irq.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "Ps2"))
        {
            var old = irq.Body.Instructions.Where(i => i.Offset >= 0x49 && i.Offset < 0x53).ToArray();
            if (old.Length != 6 || old[0].OpCode != OpCodes.Brfalse_S || old[3].OpCode != OpCodes.Bne_Un_S
                || irq.Body.Variables.Count != 2)
            { throw new InvalidOperationException("PS2 extended scan contract changed"); }
            var extended = new VariableDefinition(module.TypeSystem.Boolean); irq.Body.Variables.Add(extended);
            var il = irq.Body.GetILProcessor(); old[0].OpCode = OpCodes.Stloc; old[0].Operand = extended;
            var cursor = old[0];
            foreach (var instruction in new[] { il.Create(OpCodes.Ldloc_0), il.Create(OpCodes.Ldloc, extended),
                il.Create(OpCodes.Call, Policy("Ps2")), il.Create(OpCodes.Stloc_0) })
            { il.InsertAfter(cursor, instruction); cursor = instruction; }
            foreach (var instruction in old.Skip(1)) { instruction.OpCode = OpCodes.Nop; instruction.Operand = null; }
            changed = true;
        }
    }
    if (assembly.Name.Name == "Cosmos.Kernel.HAL")
    {
        var translate = module.GetType("Cosmos.Kernel.HAL.Devices.Input.VirtioKeyboard").Methods.Single(m => m.Name == "LinuxToPS2ScanCode");
        if (!translate.IsStatic || translate.Parameters.Count != 1 || translate.Parameters[0].ParameterType.FullName != "System.UInt16"
            || translate.ReturnType.FullName != "System.Byte") { throw new InvalidOperationException("Virtio keyboard signature changed"); }
        if (!translate.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "Virtio"))
        {
            var il = translate.Body.GetILProcessor();
            foreach (var instruction in translate.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
            {
                instruction.OpCode = OpCodes.Ldarg_0;
                var call = il.Create(OpCodes.Call, Policy("Virtio")); il.InsertAfter(instruction, call); il.InsertAfter(call, il.Create(OpCodes.Ret));
            }
            translate.Body.MaxStackSize = Math.Max(2, translate.Body.MaxStackSize); changed = true;
        }
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
