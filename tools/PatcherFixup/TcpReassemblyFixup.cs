using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class TcpReassemblyFixup
{
    private const string Policy = "Zenith.Core.Network.Transport.TcpSegmentPolicy";
    private const string Bridge = "Zenith.Core.Network.Transport.TcpReceiveBridge";

    internal static bool Apply(TypeDefinition tcp, TypeDefinition packet, ModuleDefinition support)
    {
        var receive = tcp.Methods.Single(m => m.Name == "ReceiveDataInternal");
        var process = tcp.Methods.Single(m => m.Name == "ProcessEstablished");
        var allowed = receive.Body.Instructions.Single(i => i.Operand is MethodReference m && m.DeclaringType.FullName == Policy);
        var guard = (MethodReference)allowed.Operand;
        var module = tcp.Module;
        var frame = tcp.Fields.Single(f => f.Name == "_zenithReceive");
        var control = tcp.Methods.Single(m => m.Name == "get_TCB");
        var tcb = module.GetType(control.ReturnType.FullName);
        var next = tcb.Methods.Single(m => m.Name == "get_RcvNxt");
        var status = tcp.Methods.Single(m => m.Name == "get_Status");
        var flags = packet.Methods.Single(m => m.Name == "get_FlagBits");
        var sequence = packet.Methods.Single(m => m.Name == "get_SequenceNumber");
        var fin = packet.Fields.Single(f => f.Name == "_fin");
        var applied = process.Body.Instructions.Where(i => i.Operand is MethodReference m && m.DeclaringType.FullName == Bridge
            && m.Name == "Receive").ToArray();
        if (guard.Name == "ReceiveAllowed" && applied.Length == 1)
        {
            if (allowed.Previous.Operand != flags || allowed.Previous.Previous.Previous.Operand != status ||
                !Calls(applied[0].Previous.Previous.Previous, "get_TcpData") || applied[0].Previous.Operand != fin)
                throw new InvalidOperationException("Cosmos adapted reassembly contract changed");
            return false;
        }
        if (guard.Name != "Accept" || guard.Parameters.Count != 4 || allowed.Next.OpCode != OpCodes.Brfalse ||
            applied.Length != 0 || flags.ReturnType.MetadataType != MetadataType.Byte ||
            (int)module.GetType(status.ReturnType.FullName).Fields.Single(f => f.Name == "ESTABLISHED").Constant != 3)
            throw new InvalidOperationException("Cosmos TCP receive admission changed");

        var il = receive.Body.GetILProcessor();
        il.InsertBefore(allowed, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(allowed, il.Create(OpCodes.Call, status));
        il.InsertBefore(allowed, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(allowed, il.Create(OpCodes.Callvirt, flags));
        allowed.Operand = module.ImportReference(support.GetType(Policy).Methods.Single(m => m.Name == "ReceiveAllowed"));
        receive.Body.MaxStackSize = Math.Max(6, receive.Body.MaxStackSize);

        // Replace just the established payload append/RCV.NXT update. ACK processing, FIN,
        // state transitions and SendEmptyPacket stay in the pinned implementation.
        var code = process.Body.Instructions;
        var appends = code.Where(i => Calls(i, "AppendToData")).ToArray();
        if (appends.Length != 2) throw new InvalidOperationException("Cosmos TCP established append sites changed");
        int last = code.IndexOf(appends[0]), first = last - 12;
        var pattern = new[] { Code.Ldarg_0, Code.Call, Code.Dup, Code.Callvirt, Code.Ldarg_1, Code.Callvirt,
            Code.Add, Code.Callvirt, Code.Ldarg_0, Code.Ldarg_1, Code.Callvirt, Code.Call, Code.Call };
        if (first < 0 || !code.Skip(first).Take(13).Select(i => i.OpCode.Code).SequenceEqual(pattern) ||
            code[first + 1].Operand != control || !Calls(code[first + 3], "get_RcvNxt") ||
            !Calls(code[first + 5], "get_TcpDataLength") || !Calls(code[first + 7], "set_RcvNxt") ||
            !Calls(code[first + 10], "get_TcpData") || !Calls(code[first + 11], "op_Implicit") ||
            code.Any(i => i.Operand is Instruction dest && code.IndexOf(dest) > first && code.IndexOf(dest) <= last))
            throw new InvalidOperationException("Cosmos TCP contiguous delivery changed");
        var bridge = support.GetType(Bridge);
        MethodReference Import(string name) => module.ImportReference(bridge.Methods.Single(m => m.Name == name));
        var advance = new VariableDefinition(module.TypeSystem.Int32);
        process.Body.Variables.Add(advance);
        il = process.Body.GetILProcessor();
        var ok = il.Create(OpCodes.Ldarg_0);
        var instructions = new[] {
            il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldflda, frame),
            il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, control), il.Create(OpCodes.Callvirt, next),
            il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Callvirt, sequence),
            il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Callvirt, (MethodReference)code[first + 10].Operand),
            il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Ldfld, fin), il.Create(OpCodes.Call, Import("Receive")),
            il.Create(OpCodes.Stloc, advance), il.Create(OpCodes.Ldloc, advance), il.Create(OpCodes.Ldc_I4_0),
            il.Create(OpCodes.Bge, ok), il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldc_I4, 10),
            il.Create(OpCodes.Call, tcp.Methods.Single(m => m.Name == "set_Status")), il.Create(OpCodes.Ret),
            ok, il.Create(OpCodes.Call, control), il.Create(OpCodes.Dup), il.Create(OpCodes.Callvirt, next),
            il.Create(OpCodes.Ldloc, advance), il.Create(OpCodes.Add), il.Create(OpCodes.Callvirt, tcb.Methods.Single(m => m.Name == "set_RcvNxt")),
            il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, control), il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldflda, frame),
            il.Create(OpCodes.Call, Import("Window")), il.Create(OpCodes.Callvirt, tcb.Methods.Single(m => m.Name == "set_RcvWnd"))
        };
        var anchor = code[first];
        anchor.OpCode = OpCodes.Nop;
        for (int i = last; i > first; i--) il.Remove(code[i]);
        foreach (var item in instructions) { il.InsertAfter(anchor, item); anchor = item; }
        process.Body.MaxStackSize = Math.Max(6, process.Body.MaxStackSize);
        // The larger payload block may push short branches past 127 bytes.
        Mono.Cecil.Rocks.MethodBodyRocks.SimplifyMacros(process.Body);
        Console.WriteLine("Adapted bounded TCP reassembly on established connections");
        return true;
    }

    private static bool Calls(Instruction instruction, string name) =>
        instruction.Operand is MethodReference method && method.Name == name;
}
