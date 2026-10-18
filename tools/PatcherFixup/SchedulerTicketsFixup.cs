using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class SchedulerTicketsFixup
{
    // State is insufficient: BlockThread and ExitThread set Blocked/Dead before
    // calling the policy, and policy handover also exits non-runnable threads.
    // Keep admission with the policy record, counting each runnable lifetime once.
    internal static bool Apply(ModuleDefinition module)
    {
        const string prefix = "Cosmos.Kernel.Core.Scheduler.Stride.";
        var policy = module.GetType(prefix + "StrideScheduler");
        var data = module.GetType(prefix + "StrideThreadData");
        var cpu = module.GetType(prefix + "StrideCpuData");
        const string marker = "_zenithRunnableCounted";
        if (data.Fields.Any(f => f.Name == marker)) { return false; }
        var counted = new FieldDefinition(marker, FieldAttributes.Assembly, module.TypeSystem.Boolean);
        var getTotal = cpu.Methods.Single(m => m.Name == "get_TotalTickets");
        var setTotal = cpu.Methods.Single(m => m.Name == "set_TotalTickets");
        var getTickets = data.Methods.Single(m => m.Name == "get_Tickets");
        if (getTotal.ReturnType.MetadataType != MetadataType.UInt64
            || getTickets.ReturnType.MetadataType != MetadataType.UInt64)
        { throw new InvalidOperationException("Cosmos ticket type contract changed"); }
        var writes = policy.Methods.Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions.Where(i => i.Operand == setTotal).Select(i => m.Name)).OrderBy(n => n).ToArray();
        if (!writes.SequenceEqual(new[] { "OnThreadBlocked", "OnThreadCreate", "OnThreadExit", "OnThreadMigrate", "OnThreadMigrate", "OnThreadReady", "SetPriority" }.OrderBy(n => n)))
        { throw new InvalidOperationException("Cosmos ticket write sites changed"); }
        var adjust = new MethodDefinition("_zenithSetRunnable", MethodAttributes.Private | MethodAttributes.Static,
            module.TypeSystem.Void);
        adjust.Parameters.Add(new ParameterDefinition(cpu));
        adjust.Parameters.Add(new ParameterDefinition(data));
        adjust.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
        var il = adjust.Body.GetILProcessor();
        var done = il.Create(OpCodes.Ret);
        var debit = il.Create(OpCodes.Sub);
        var store = il.Create(OpCodes.Callvirt, setTotal);
        foreach (var instruction in new[] {
            il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Ldfld, counted), il.Create(OpCodes.Ldarg_2), il.Create(OpCodes.Beq, done),
            il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Callvirt, getTotal),
            il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Callvirt, getTickets),
            il.Create(OpCodes.Ldarg_2), il.Create(OpCodes.Brfalse, debit), il.Create(OpCodes.Add), il.Create(OpCodes.Br, store),
            debit, store, il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Ldarg_2), il.Create(OpCodes.Stfld, counted), done })
        { il.Append(instruction); }
        data.Fields.Add(counted); policy.Methods.Add(adjust);

        foreach (var (name, cpuLocal, dataLocal, admission) in new[] {
            ("OnThreadCreate", 1, 0, true), ("OnThreadReady", 0, 1, true),
            ("OnThreadBlocked", 0, 1, false), ("OnThreadExit", 0, 1, false),
            ("OnThreadMigrate", 0, 2, false), ("OnThreadMigrate", 1, 2, true) })
        {
            var method = policy.Methods.Single(m => m.Name == name);
            var code = method.Body.Instructions;
            var setter = code.Single(i => i.Operand == setTotal
                && i.Previous.OpCode == (admission ? OpCodes.Add : OpCodes.Sub));
            var range = code.Skip(code.IndexOf(setter) - 6).Take(7).ToArray();
            if (method.Body.Variables[cpuLocal].VariableType != cpu
                || method.Body.Variables[dataLocal].VariableType != data
                || range[0].OpCode != (cpuLocal == 0 ? OpCodes.Ldloc_0 : OpCodes.Ldloc_1)
                || range[1].OpCode != OpCodes.Dup || range[2].Operand != getTotal
                || range[3].OpCode != (dataLocal == 0 ? OpCodes.Ldloc_0 : dataLocal == 1 ? OpCodes.Ldloc_1 : OpCodes.Ldloc_2)
                || range[4].Operand != getTickets)
            { throw new InvalidOperationException("Cosmos ticket lifecycle contract changed: " + name); }
            range[1].OpCode = range[3].OpCode; range[1].Operand = null;
            range[2].OpCode = admission ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0; range[2].Operand = null;
            range[3].OpCode = OpCodes.Call; range[3].Operand = adjust;
            foreach (var instruction in range.Skip(4)) { instruction.OpCode = OpCodes.Nop; instruction.Operand = null; }
        }
        // A parked/unadmitted record has no runnable membership to transfer.
        var migrate = policy.Methods.Single(m => m.Name == "OnThreadMigrate");
        var migrateStart = migrate.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "RemoveThreadFromQueue").Previous.Previous.Previous.Previous;
        if (migrateStart.OpCode != OpCodes.Ldarg_0)
        { throw new InvalidOperationException("Cosmos migration entry contract changed"); }
        var migrateIl = migrate.Body.GetILProcessor();
        var continueMigration = migrateIl.Create(OpCodes.Nop);
        migrateStart.OpCode = OpCodes.Ldloc_2; migrateStart.Operand = null;
        var cursor = migrateStart;
        foreach (var instruction in new[] { migrateIl.Create(OpCodes.Ldfld, counted), migrateIl.Create(OpCodes.Brtrue, continueMigration),
            migrateIl.Create(OpCodes.Ret), continueMigration, migrateIl.Create(OpCodes.Ldarg_0) })
        { migrateIl.InsertAfter(cursor, instruction); cursor = instruction; }
        // Priority changes update the record even when asleep/Created, but only
        // change the CPU's total while that record has admitted runnable tickets.
        var priority = policy.Methods.Single(m => m.Name == "SetPriority");
        var write = priority.Body.Instructions.Single(i => i.Operand == setTotal);
        var start = priority.Body.Instructions[priority.Body.Instructions.IndexOf(write) - 7];
        if (start.OpCode != OpCodes.Ldloc_1 || start.Next.OpCode != OpCodes.Ldloc_1
            || start.Next.Next.Operand != getTotal || priority.Body.Variables[2].VariableType != data)
        { throw new InvalidOperationException("Cosmos priority ticket contract changed"); }
        var priorityIl = priority.Body.GetILProcessor();
        foreach (var instruction in new[] { priorityIl.Create(OpCodes.Ldloc_2), priorityIl.Create(OpCodes.Ldfld, counted),
            priorityIl.Create(OpCodes.Brfalse, write.Next) })
        { priorityIl.InsertBefore(start, instruction); }
        Console.WriteLine("Adapted stride tickets: count runnable admission once across block, exit, migration and priority changes");
        return true;
    }
}
