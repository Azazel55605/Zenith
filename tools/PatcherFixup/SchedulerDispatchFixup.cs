using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class SchedulerDispatchFixup
{
    // A timer can wake/enqueue CurrentThread before it has switched away from a
    // short sleep. Picking that same thread consumes its queue entry. The pinned
    // dispatcher skips the whole different-thread branch, including Running.
    // Restore only that Ready -> Running transition, without saving/replacing a
    // live stack or requesting a native context switch to ourselves.
    internal static bool Apply(ModuleDefinition module)
    {
        var scheduler = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerManager");
        const string marker = "_zenithSelfDispatchRunning";
        if (scheduler.Fields.Any(f => f.Name == marker)) { return false; }
        var thread = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerThread");
        var getState = thread.Methods.Single(m => m.Name == "get_State");
        var setState = thread.Methods.Single(m => m.Name == "set_State");
        var states = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerThreadState");
        if (Convert.ToInt32(states.Fields.Single(f => f.Name == "Ready").Constant) != 1
            || Convert.ToInt32(states.Fields.Single(f => f.Name == "Running").Constant) != 2)
        { throw new InvalidOperationException("Cosmos dispatch thread state contract changed"); }
        foreach (string name in new[] { "Schedule", "ScheduleFromInterrupt" })
        {
            var method = scheduler.Methods.Single(m => m.Name == name);
            var code = method.Body.Instructions;
            if (!method.IsStatic || method.Body.Variables.Count != 3
                || method.Body.Variables[1].VariableType.FullName != thread.FullName
                || method.Body.Variables[2].VariableType.FullName != thread.FullName)
            { throw new InvalidOperationException("Cosmos dispatch locals changed"); }
            var compare = code.Single(i => (i.OpCode == OpCodes.Beq_S || i.OpCode == OpCodes.Beq)
                && i.Previous.OpCode == OpCodes.Ldloc_1 && i.Previous.Previous.OpCode == OpCodes.Ldloc_2);
            var same = (Instruction)compare.Operand;
            if (same.OpCode != (name == "Schedule" ? OpCodes.Ldloc_0 : OpCodes.Ret))
            { throw new InvalidOperationException("Cosmos same-thread dispatch exit changed"); }
            var different = compare.Next;
            compare.OpCode = OpCodes.Bne_Un; compare.Operand = different;
            var il = method.Body.GetILProcessor();
            foreach (var instruction in new[] {
                il.Create(OpCodes.Ldloc_2), il.Create(OpCodes.Callvirt, getState),
                il.Create(OpCodes.Ldc_I4_1), il.Create(OpCodes.Bne_Un, same),
                il.Create(OpCodes.Ldloc_2), il.Create(OpCodes.Ldc_I4_2), il.Create(OpCodes.Callvirt, setState),
                il.Create(OpCodes.Br, same) })
            { il.InsertBefore(different, instruction); }
        }
        scheduler.Fields.Add(new FieldDefinition(marker, FieldAttributes.Private | FieldAttributes.Static
            | FieldAttributes.Literal | FieldAttributes.HasDefault, module.TypeSystem.Boolean) { Constant = true });
        Console.WriteLine("Adapted same-thread dispatch: restore Running after a current-thread wake without a context switch");
        return true;
    }
}
