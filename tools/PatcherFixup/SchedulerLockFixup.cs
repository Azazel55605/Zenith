using Mono.Cecil;
using Mono.Cecil.Cil;

// C5.1: the pinned scheduler guards its registry, run queues and thread states by masking interrupts,
// which excludes the local timer but not another CPU. Every masked section in SchedulerManager and in
// thread creation also takes the native recursive scheduler lock (src/Native/Cpu/sched.c) right after
// masking and releases it right before the mask is restored. The interrupt-time scheduling paths run
// with interrupts already masked and take it for their whole body. On one dispatch CPU this only adds
// an uncontended atomic; it is the precondition for letting more CPUs dispatch (C5.2).
//
// Cosmos skips finally blocks while unwinding an exception, which would leak the mask as well as the
// lock; none of these sections throw today (they report through Serial), and the lock counts any
// mismatched release instead of freeing it under its real owner.
internal static class SchedulerLockFixup
{
    private const string Lock = "_zenithSchedLock", Unlock = "_zenithSchedUnlock";

    private static MethodDefinition Native(ModuleDefinition module, TypeDefinition owner, string name, string entry)
    {
        var library = module.ModuleReferences.FirstOrDefault(r => r.Name == "*");
        if (library is null) { library = new ModuleReference("*"); module.ModuleReferences.Add(library); }
        var method = new MethodDefinition(name, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.PInvokeImpl, module.TypeSystem.UInt64);
        method.PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl, entry, library);
        method.ImplAttributes = MethodImplAttributes.PreserveSig;
        method.CustomAttributes.Add(new CustomAttribute(module.ImportReference(
            typeof(System.Runtime.InteropServices.SuppressGCTransitionAttribute).GetConstructor(Type.EmptyTypes)!)));
        owner.Methods.Add(method);
        return method;
    }

    private static int? StoredLocal(Instruction i) => i.OpCode.Code switch
    {
        Code.Stloc_0 => 0, Code.Stloc_1 => 1, Code.Stloc_2 => 2, Code.Stloc_3 => 3,
        Code.Stloc_S or Code.Stloc => ((VariableDefinition)i.Operand).Index, _ => null,
    };

    private static int? AddressedLocal(Instruction i) => i.OpCode.Code is Code.Ldloca_S or Code.Ldloca ? ((VariableDefinition)i.Operand).Index : null;

    /// <summary>Takes the lock after each mask in <paramref name="method"/> and releases it before each matching restore.
    /// Returns the number of masked sections adapted.</summary>
    private static int Sections(MethodDefinition method, MethodDefinition mask, MethodDefinition dispose, MethodDefinition take, MethodDefinition release)
    {
        var code = method.Body.Instructions;
        var il = method.Body.GetILProcessor();
        var scopes = new List<int>();
        foreach (var call in code.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference m && m.Resolve() == mask).ToArray())
        {
            int local = StoredLocal(call.Next) ?? throw new InvalidOperationException("Cosmos masked section in " + method.Name + " is not a stored scope");
            scopes.Add(local);
            var store = call.Next;
            il.InsertAfter(store, il.Create(OpCodes.Pop));
            il.InsertAfter(store, il.Create(OpCodes.Call, take));
        }
        int restored = 0;
        foreach (var call in code.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference m && m.Resolve() == dispose).ToArray())
        {
            var load = call.Previous;
            if (AddressedLocal(load) is not int local || !scopes.Contains(local)) { continue; }
            // Branches and handler boundaries that pointed at the load now land on the release instead.
            var first = il.Create(OpCodes.Call, release);
            il.InsertBefore(load, first);
            il.InsertAfter(first, il.Create(OpCodes.Pop));
            foreach (var i in code) { if (i.Operand == load && i != first.Next) { i.Operand = first; } if (i.Operand is Instruction[] targets) { for (int t = 0; t < targets.Length; t++) { if (targets[t] == load) { targets[t] = first; } } } }
            foreach (var h in method.Body.ExceptionHandlers)
            {
                if (h.HandlerStart == load) { h.HandlerStart = first; }
                if (h.TryEnd == load) { h.TryEnd = first; }
                if (h.HandlerEnd == load) { h.HandlerEnd = first; }
            }
            restored++;
        }
        if (restored < scopes.Count) { throw new InvalidOperationException("Cosmos masked section in " + method.Name + " has no matching restore"); }
        return scopes.Count;
    }

    /// <summary>The whole body under the lock: interrupt-time paths, already masked.</summary>
    private static void Whole(MethodDefinition method, MethodDefinition take, MethodDefinition release)
    {
        if (method.Body.ExceptionHandlers.Count != 0) { throw new InvalidOperationException("Cosmos " + method.Name + " gained exception handlers"); }
        var code = method.Body.Instructions;
        var il = method.Body.GetILProcessor();
        foreach (var ret in code.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            var first = il.Create(OpCodes.Call, release);
            il.InsertBefore(ret, first);
            il.InsertAfter(first, il.Create(OpCodes.Pop));
            foreach (var i in code) { if (i.Operand == ret && i != first.Next) { i.Operand = first; } if (i.Operand is Instruction[] targets) { for (int t = 0; t < targets.Length; t++) { if (targets[t] == ret) { targets[t] = first; } } } }
        }
        var start = code[0];
        il.InsertBefore(start, il.Create(OpCodes.Call, take));
        il.InsertBefore(start, il.Create(OpCodes.Pop));
    }

    internal static bool Apply(ModuleDefinition module)
    {
        var scheduler = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerManager")
            ?? throw new InvalidOperationException("Cosmos scheduler manager changed");
        if (scheduler.Methods.Any(m => m.Name == Lock)) { return false; }
        var cpu = module.GetType("Cosmos.Kernel.Core.CPU.InternalCpu");
        var scope = cpu.NestedTypes.Single(t => t.Name == "InterruptScope");
        var mask = cpu.Methods.Single(m => m.Name == "DisableInterruptsScope");
        var dispose = scope.Methods.Single(m => m.Name == "Dispose");
        var take = Native(module, scheduler, Lock, "zenith_sched_lock");
        var release = Native(module, scheduler, Unlock, "zenith_sched_unlock");

        var masked = new[] { "GetBusyCpuTimeNs", "RegisterThread", "CreateThread", "ReadyThread", "BlockThread", "ExitThread", "YieldThread", "MarkSleeping" };
        int sections = 0;
        foreach (string name in masked)
        {
            var methods = scheduler.Methods.Where(m => m.Name == name && m.HasBody).ToArray();
            if (methods.Length != 1) { throw new InvalidOperationException("Cosmos scheduler " + name + " changed"); }
            int n = Sections(methods[0], mask, dispose, take, release);
            if (n == 0) { throw new InvalidOperationException("Cosmos scheduler " + name + " is no longer masked"); }
            sections += n;
        }
        // UnregisterThread runs inside ExitThread's section; it is covered by nesting either way.
        foreach (string name in new[] { "OnTimerInterrupt", "ReschedulePendingFromIrq", "ScheduleFromInterrupt" })
        {
            var method = scheduler.Methods.Single(m => m.Name == name && m.HasBody);
            Whole(method, take, release);
        }
        // Thread creation (libSystemNative) builds the thread object inside its masked section, so an allocation slow
        // path can run there: it must not hold the scheduler lock. Its CreateThread/ReadyThread calls lock themselves;
        // only the thread id counter it reads in between is shared, and that takes the lock on its own.
        Whole(scheduler.Methods.Single(m => m.Name == "AllocateThreadId" && m.HasBody), take, release);
        Console.WriteLine("Adapted scheduler locking: " + sections + " masked sections, 3 interrupt-time paths and the thread id counter take the SMP scheduler lock");
        return true;
    }
}
