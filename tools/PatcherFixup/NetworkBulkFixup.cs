using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

internal static class NetworkBulkFixup
{
    private const string Count = "ZenithReceiveCount", Copy = "ZenithCopyPrefix";
    private const string TcpType = "Cosmos.Kernel.System.Network.TCP.Tcp";

    // A recursive network update defers sends, and the pinned drain walks the list
    // backwards. A receive burst consequently puts cumulative ACKs on the wire in
    // reverse order. Keep the original bounded pass and all seven send/remove paths.
    public static bool OrderIpv4Queue(TypeDefinition outgoing)
    {
        var send = outgoing.Methods.Single(m => m.Name == "Send" && !m.HasParameters);
        var body = send.Body;
        var item = body.Instructions.Single(i => Calls(i, "get_Item") && QueueMember(i));
        var removes = body.Instructions.Where(i => Calls(i, "RemoveAt") && QueueMember(i)).ToArray();
        if (removes.Length != 7) { throw new InvalidOperationException("Cosmos IPv4 queue removal sites changed"); }
        if (item.Previous.OpCode == OpCodes.Ldloc && item.Previous.Operand is VariableDefinition cursor
            && removes.All(i => i.Previous.Operand == cursor))
        { return false; }
        var start = body.Instructions.Single(i => Calls(i, "get_Count") && i.Next.OpCode == OpCodes.Ldc_I4_1 && i.Next.Next.OpCode == OpCodes.Sub);
        var index = VariableAt(body, item.Previous);
        if (index is null || VariableAt(body, start.Next.Next.Next) != index || removes.Any(i => VariableAt(body, i.Previous) != index)
            || start.Next.Next.Next.Next.OpCode != OpCodes.Br || start.Next.Next.Next.Next.Operand is not Instruction check
            || VariableAt(body, check) != index || check.Next.Next.OpCode != OpCodes.Bge || check.Next.Next.Operand != item.Previous.Previous)
        { throw new InvalidOperationException("Cosmos IPv4 queue traversal changed"); }

        // 'index' remains the number of original entries still to visit. Appends
        // during NIC.Send join the next pass. Unresolved ARP entries stay in order.
        var position = new VariableDefinition(send.Module.TypeSystem.Int32);
        body.Variables.Add(position);
        var il = body.GetILProcessor();
        var startBranch = start.Next.Next.Next.Next;
        il.InsertBefore(startBranch, il.Create(OpCodes.Ldc_I4_M1));
        il.InsertBefore(startBranch, il.Create(OpCodes.Stloc, position));
        foreach (var site in removes.Prepend(item))
        {
            var load = site.Previous;
            load.OpCode = OpCodes.Ldloc; load.Operand = position;
            if (site == item)
            {
                il.InsertBefore(load, il.Create(OpCodes.Ldloc, position));
                il.InsertBefore(load, il.Create(OpCodes.Ldc_I4_1));
                il.InsertBefore(load, il.Create(OpCodes.Add));
                il.InsertBefore(load, il.Create(OpCodes.Stloc, position));
            }
            else
            {
                var next = site.Next;
                il.InsertBefore(next, il.Create(OpCodes.Ldloc, position));
                il.InsertBefore(next, il.Create(OpCodes.Ldc_I4_1));
                il.InsertBefore(next, il.Create(OpCodes.Sub));
                il.InsertBefore(next, il.Create(OpCodes.Stloc, position));
            }
        }
        body.SimplifyMacros(); // Added instructions may take short branches beyond their byte range.
        Console.WriteLine("Adapted IPv4 queued transmissions to FIFO order");
        return true;
    }

    private static bool Calls(Instruction instruction, string name) =>
        instruction.Operand is MethodReference method && method.Name == name;
    private static bool QueueMember(Instruction instruction) =>
        ((MethodReference)instruction.Operand).DeclaringType.FullName ==
            "System.Collections.Generic.List`1<Cosmos.Kernel.System.Network.IPv4.OutgoingBuffer/BufferEntry>";

    private static VariableDefinition? VariableAt(MethodBody body, Instruction i) => i.OpCode.Code switch
    {
        Code.Ldloc_0 or Code.Stloc_0 => body.Variables[0], Code.Ldloc_1 or Code.Stloc_1 => body.Variables[1],
        Code.Ldloc_2 or Code.Stloc_2 => body.Variables[2], Code.Ldloc_3 or Code.Stloc_3 => body.Variables[3],
        Code.Ldloc or Code.Ldloc_S or Code.Ldloca or Code.Ldloca_S or Code.Stloc or Code.Stloc_S => (VariableDefinition)i.Operand,
        _ => null
    };

    // Expose exact-prefix copying and count to Cosmos.Kernel.Plugs and to the
    // Socket.Available method body transplanted into System.Net.Sockets.
    public static bool AddReceiveAccessors(TypeDefinition tcp, TypeDefinition bridge)
    {
        var field = tcp.Fields.Single(f => f.Name == "_zenithReceive");
        bool changed = false;
        foreach (var (name, target) in new[] { (Count, "Count"), (Copy, "CopyPrefix") })
        {
            var helper = bridge.Methods.Single(m => m.Name == target);
            var existing = tcp.Methods.SingleOrDefault(m => m.Name == name);
            if (existing != null)
            {
                if (existing.Body.Instructions.Count(i => Calls(i, target)) != 1)
                { throw new InvalidOperationException("Cosmos TCP bulk accessor changed"); }
                continue;
            }
            var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.HideBySig,
                tcp.Module.ImportReference(helper.ReturnType));
            var il = method.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldflda, field));
            if (target == "CopyPrefix")
            {
                method.Parameters.Add(new ParameterDefinition(tcp.Module.ImportReference(helper.Parameters[1].ParameterType)));
                il.Append(il.Create(OpCodes.Ldarg_1));
            }
            il.Append(il.Create(OpCodes.Call, tcp.Module.ImportReference(helper))); il.Append(il.Create(OpCodes.Ret));
            tcp.Methods.Add(method); changed = true;
        }
        return changed;
    }

    public static bool AdaptReads(MethodDefinition method, int countSites, int copySites)
    {
        var body = method.Body;
        bool IsTcp(Instruction i, string name) => Calls(i, name) && ((MethodReference)i.Operand).DeclaringType.FullName == TcpType;
        var sites = body.Instructions.Where(i => IsTcp(i, "get_Data")).ToArray();
        if (sites.Length == 0 && body.Instructions.Count(i => IsTcp(i, Count)) == countSites
            && body.Instructions.Count(i => IsTcp(i, Copy)) == copySites)
        { return false; }
        if (sites.Length != countSites + copySites)
        { throw new InvalidOperationException("Cosmos TCP socket snapshot access changed: " + method.FullName); }

        int counts = 0, copies = 0;
        foreach (var site in sites)
        {
            var tcp = ((MethodReference)site.Operand).DeclaringType;
            var local = VariableAt(body, site.Next);
            if (local?.VariableType.FullName != "System.ReadOnlySpan`1<System.Byte>" || VariableAt(body, site.Next.Next) != local)
            { throw new InvalidOperationException("Cosmos TCP socket snapshot local changed"); }
            var operation = site.Next.Next.Next;
            if (Calls(operation, "get_Length"))
            {
                site.Operand = new MethodReference(Count, method.Module.TypeSystem.Int32, tcp) { HasThis = true };
                Clear(site.Next, operation.Next);
                counts++;
            }
            else
            {
                // sm.Data.Slice(0, readLength).CopyTo(target) -> sm.ZenithCopyPrefix(target)
                var slice = operation.Next.Next;
                var target = slice.Next.Next.Next;
                var span = VariableAt(body, target);
                if (operation.OpCode != OpCodes.Ldc_I4_0 || !Calls(slice, "Slice") || !Calls(target.Next, "CopyTo")
                    || span?.VariableType.FullName != "System.Span`1<System.Byte>"
                    || VariableAt(body, slice.Next) is null || VariableAt(body, slice.Next) != VariableAt(body, slice.Next.Next))
                { throw new InvalidOperationException("Cosmos TCP socket copy changed"); }
                var copy = new MethodReference(Copy, method.Module.TypeSystem.Void, tcp) { HasThis = true };
                copy.Parameters.Add(new ParameterDefinition(span.VariableType));
                Clear(site, target);
                target.Next.OpCode = OpCodes.Callvirt; target.Next.Operand = copy;
                copies++;
            }
        }
        if (counts != countSites || copies != copySites)
        { throw new InvalidOperationException("Cosmos TCP socket read sites changed"); }
        return true;
    }

    private static void Clear(Instruction from, Instruction until)
    {
        for (var i = from; i != until; i = i.Next) { i.OpCode = OpCodes.Nop; i.Operand = null; }
    }
}
