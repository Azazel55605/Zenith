using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class MonitorTimeoutFixup
{
    internal static bool ApplyCondition(ModuleDefinition module)
    {
        var type = module.GetType("Cosmos.Kernel.Core.Scheduler.ConditionVariable");
        var states = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerThreadState");
        if (Convert.ToInt32(states.Fields.Single(f => f.Name == "Blocked").Constant) != 3
            || Convert.ToInt32(states.Fields.Single(f => f.Name == "Sleeping").Constant) != 4)
        { throw new InvalidOperationException("Cosmos condition thread states changed"); }
        bool changed = false;
        foreach (var (name, state) in new[] { ("Wait", 3), ("WaitTimeout", 4) })
        {
            var method = type.Methods.Single(m => m.Name == name);
            var code = method.Body.Instructions;
            var halt = code.Single(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference m
                && m.DeclaringType.FullName == "Cosmos.Kernel.Core.CPU.InternalCpu" && m.Name == "Halt");
            var branch = halt.Previous;
            var start = branch.Previous.Previous.Previous;
            if (branch.OpCode != OpCodes.Bne_Un_S || branch.Previous.OpCode != (state == 3 ? OpCodes.Ldc_I4_3 : OpCodes.Ldc_I4_4)
                || start.OpCode != OpCodes.Ldloc_0 || start.Next.Operand is not MethodReference getter
                || getter.Name != "get_State"
                || getter.DeclaringType.FullName != "Cosmos.Kernel.Core.Scheduler.SchedulerThread"
                || getter.ReturnType.FullName != states.FullName || (Instruction)branch.Operand != (halt.Next.OpCode == OpCodes.Br ? halt.Next.Next : halt.Next))
            { throw new InvalidOperationException("Cosmos condition park contract changed"); }
            if (halt.Next.OpCode == OpCodes.Br)
            {
                if (halt.Next.Operand != start) throw new InvalidOperationException("Cosmos condition park loop changed");
                continue;
            }
            method.Body.GetILProcessor().InsertAfter(halt, Instruction.Create(OpCodes.Br, start));
            changed = true;
        }
        if (changed) Console.WriteLine("Adapted condition waits: remain parked after unrelated interrupts");
        return changed;
    }

    // Apply to both the source plug and the body already transplanted into CoreLib.
    internal static bool Apply(TypeDefinition type)
    {
        var method = type.Methods.Single(m => m.Name == "LowLevelMonitor_TimedWait");
        var code = method.Body.Instructions;
        var waits = code.Where(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference m
            && m.DeclaringType.FullName == "Cosmos.Kernel.Core.Scheduler.Monitor" && m.Name == "Wait").ToArray();
        if (method.ReturnType.FullName != "System.Boolean" || method.Parameters.Count != 2
            || method.Parameters[0].ParameterType.FullName != "System.IntPtr"
            || method.Parameters[1].ParameterType.FullName != "System.Int32" || waits.Length != 2
            || ((MethodReference)waits[0].Operand).Parameters.Count != 0
            || ((MethodReference)waits[0].Operand).ReturnType.FullName != "System.Void"
            || ((MethodReference)waits[1].Operand).Parameters.Count != 1
            || ((MethodReference)waits[1].Operand).Parameters[0].ParameterType.FullName != "System.Int32"
            || ((MethodReference)waits[1].Operand).ReturnType.FullName != "System.Boolean"
            || waits[0].Next.OpCode != OpCodes.Ldc_I4_1 || waits[0].Next.Next.OpCode != OpCodes.Ret)
        { throw new InvalidOperationException("Cosmos monitor timeout contract changed"); }
        var pop = waits[1].Next;
        if (pop.OpCode == OpCodes.Nop && pop.Next.OpCode == OpCodes.Nop && pop.Next.Next.OpCode == OpCodes.Ret)
        { return false; }
        if (pop.OpCode != OpCodes.Pop || pop.Next.OpCode != OpCodes.Ldc_I4_1
            || pop.Next.Next.OpCode != OpCodes.Ret
            || code.Any(i => i.Operand is Instruction target && (target == pop || target == pop.Next)))
        { throw new InvalidOperationException("Cosmos monitor timeout return contract changed"); }
        pop.OpCode = OpCodes.Nop;
        pop.Next.OpCode = OpCodes.Nop;
        Console.WriteLine("Adapted monitor timed wait: preserve timeout versus signal result");
        return true;
    }
}
