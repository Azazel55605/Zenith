using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class ThreadStartFixup
{
    // A new thread's saved context sits at the lowest address of its stack, and the context-switch stub
    // re-enables interrupts (popfq) one instruction before it loads the real stack pointer (pop rsp). An
    // interrupt in that window ran its handler from the stack's base downward, below the allocation, where
    // an allocation in the handler could hand out and zero the very page it was running on: a jump to
    // address 0 and "System halted". Keep the context a quarter of the way up, so that window stays on this
    // thread's own stack. The thread still starts from the top, so it loses no usable stack.
    internal static bool Apply(ModuleDefinition module)
    {
        var thread = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerThread")
            ?? throw new InvalidOperationException("Cosmos thread type changed");
        const string marker = "_zenithThreadStartReserve";
        if (thread.Fields.Any(f => f.Name == marker)) { return false; }
        var method = thread.Methods.Single(m => m.Name == "InitializeStack");
        var size = method.Parameters.Single(p => p.Name == "stackSize");
        var code = method.Body.Instructions;
        // stackBase = (StackBase + 15) & ~15: the one alignment of the context's address.
        var align = code.Where(i => i.OpCode == OpCodes.Ldloc_0 && i.Next.OpCode == OpCodes.Ldc_I4_S
            && (sbyte)i.Next.Operand == 15 && i.Previous.OpCode == OpCodes.Stloc_0).ToList();
        if (align.Count != 1 || align[0].Previous.Previous.OpCode != OpCodes.Call)
        { throw new InvalidOperationException("Cosmos new-thread stack layout changed"); }
        var il = method.Body.GetILProcessor();
        var after = align[0].Next;
        foreach (var instruction in new[] {
            il.Create(OpCodes.Ldarg, size), il.Create(OpCodes.Ldc_I4_4), il.Create(OpCodes.Conv_U),
            il.Create(OpCodes.Div_Un), il.Create(OpCodes.Add) })
        { il.InsertBefore(after, instruction); }
        thread.Fields.Add(new FieldDefinition(marker, FieldAttributes.Private | FieldAttributes.Static
            | FieldAttributes.Literal | FieldAttributes.HasDefault, module.TypeSystem.Boolean) { Constant = true });
        Console.WriteLine("Adapted thread start: keep the initial context above the stack base for the popfq window");
        return true;
    }
}
