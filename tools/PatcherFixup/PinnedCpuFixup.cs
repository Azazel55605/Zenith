using Mono.Cecil;
using Mono.Cecil.Cil;

// Preallocate permanent AP execution/TLS/TLAB owners on the BSP. This does not
// hand APs to the general scheduler: its dispatch CPU count remains exactly one.
internal static class PinnedCpuFixup
{
    // Missing safety adapters must fail the build/link, not silently admit APs
    // with just an allocation or TLS half of the C3 protocol installed.
    internal static bool Seal(ModuleDefinition module)
    {
        var gc=module.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector");
        if(!gc.Methods.Any(m=>m.Name=="_zenith_gc_enter"))return false;
        var manager=module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerManager");
        string[] entries={"_zenith_gc_enter","_zenith_gc_exit","_zenith_gc_begin","_zenith_gc_end","_zenith_gc_abort","_zenithScanStoppedRoots"};
        if(entries.Any(name=>!gc.Methods.Any(m=>m.Name==name))
            ||!manager.Methods.Any(m=>m.Name=="_zenithAttachPinnedCpus")
            ||!manager.Methods.Any(m=>m.Name=="_zenithExceptionHead")
            ||!module.GetType("Cosmos.Kernel.Core.Runtime.StackTraceMetadata").Methods.Any(m=>m.Name=="_zenithMetadataNames")
            ||!module.GetType("Cosmos.Kernel.Core.IO.Serial").Methods.Any(m=>m.Name=="_zenithSerialCpu"))
            throw new InvalidOperationException("Incomplete C3 runtime ownership contract");
        if(gc.Methods.Any(m=>m.Name=="_zenithC3Contract"))return false;
        var contract=new MethodDefinition("_zenithC3Contract",MethodAttributes.Private|MethodAttributes.Static,module.TypeSystem.UInt64);
        var exported=module.GetType("Cosmos.Kernel.Core.Runtime.Memory").Methods.Single(m=>m.Name=="RhpNewFast")
            .CustomAttributes.Single(a=>a.AttributeType.Name=="RuntimeExportAttribute");
        var attr=new CustomAttribute(exported.Constructor);
        attr.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,"zenith_managed_cpu_contract"));
        contract.CustomAttributes.Add(attr);
        var il=contract.Body.GetILProcessor();il.Emit(OpCodes.Ldc_I4_1);il.Emit(OpCodes.Conv_U8);il.Emit(OpCodes.Ret);
        gc.Methods.Add(contract);return true;
    }

    internal static bool Apply(ModuleDefinition module)
    {
        var manager = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerManager");
        if (manager.Methods.Any(m => m.Name == "_zenithAttachPinnedCpus")) return false;
        if (!module.GetType("Cosmos.Kernel.Core.Scheduler.ThreadContext").Fields.Any(f => f.Name == "Xmm")) return false;
        var state = module.GetType("Cosmos.Kernel.Core.Scheduler.PerCpuState");
        var thread = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerThread");
        var states = manager.Fields.Single(f => f.Name == "s_cpuStates");
        var nextId = manager.Fields.Single(f => f.Name == "s_nextThreadId");
        var registry = manager.Methods.Single(m => m.Name == "RegisterThread");
        var gc = module.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector");
        var library = module.ModuleReferences.First(r => r.Name == "*");
        MethodDefinition Native(string name, int argc)
        {
            var method = new MethodDefinition("_" + name, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.PInvokeImpl, module.TypeSystem.UInt64);
            for (int n = 0; n < argc; n++) method.Parameters.Add(new ParameterDefinition(module.TypeSystem.UInt64));
            method.PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl, name, library); method.ImplAttributes = MethodImplAttributes.PreserveSig;
            method.CustomAttributes.Add(new CustomAttribute(module.ImportReference(typeof(System.Runtime.InteropServices.SuppressGCTransitionAttribute).GetConstructor(Type.EmptyTypes)!)));
            manager.Methods.Add(method); return method;
        }
        var field = Native("zenith_cpu_field", 2); var configure = Native("zenith_gc_configure", 5);
        var current = manager.Methods.Single(m => m.Name == "GetCurrentCpuId");
        var attach = new MethodDefinition("_zenithAttachPinnedCpus", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.UInt64);
        attach.Parameters.Add(new ParameterDefinition(module.TypeSystem.UInt64));
        var exported = module.GetType("Cosmos.Kernel.Core.Runtime.Memory").Methods.Single(m => m.Name == "RhpNewFast")
            .CustomAttributes.Single(a => a.AttributeType.Name == "RuntimeExportAttribute");
        var attr = new CustomAttribute(exported.Constructor); attr.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, "zenith_managed_cpu_attach")); attach.CustomAttributes.Add(attr);
        attach.Body.InitLocals = true;
        var array = new VariableDefinition(new ArrayType(state)); var index = new VariableDefinition(module.TypeSystem.UInt32);
        var owner = new VariableDefinition(thread); var cpu = new VariableDefinition(state);
        foreach (var local in new[] { array, index, owner, cpu }) attach.Body.Variables.Add(local);
        var il = attach.Body.GetILProcessor(); var fail = il.Create(OpCodes.Ldc_I4_0); var done = il.Create(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Call, current); il.Emit(OpCodes.Brtrue, fail);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.Blt_Un, fail);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.Bgt_Un, fail);
        il.Emit(OpCodes.Ldsfld, states); il.Emit(OpCodes.Brfalse, fail);
        // Capacity alone is not an attachment witness: the unadapted runtime
        // also allocates physical-count slots during BSP startup.
        var completed = new FieldDefinition("_zenithPinnedAttachedCount", FieldAttributes.Private | FieldAttributes.Static, module.TypeSystem.UInt64); manager.Fields.Add(completed);
        il.Emit(OpCodes.Ldsfld, completed); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Beq, done);

        il.Emit(OpCodes.Ldsfld, states); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_U4); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Bne_Un, fail);
        var attempted = new FieldDefinition("_zenithPinnedAttachAttempted", FieldAttributes.Private | FieldAttributes.Static, module.TypeSystem.Boolean); manager.Fields.Add(attempted);
        il.Emit(OpCodes.Ldsfld, attempted); il.Emit(OpCodes.Brtrue, fail); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stsfld, attempted);
        il.Emit(OpCodes.Ldsfld, manager.Fields.Single(f => f.Name == "s_allThreadCount")); il.Emit(OpCodes.Conv_U8);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ldc_I4, 257); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.Bgt_Un, fail);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Newarr, state); il.Emit(OpCodes.Stloc, array);
        il.Emit(OpCodes.Ldloc, array); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ldsfld, states); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ldelem_Ref); il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stloc, index);
        var test = il.Create(OpCodes.Ldloc, index); var loop = il.Create(OpCodes.Newobj, state.Methods.Single(m => m.IsConstructor && !m.IsStatic));
        il.Emit(OpCodes.Br, test); il.Append(loop); il.Emit(OpCodes.Stloc, cpu);
        il.Emit(OpCodes.Ldloc, cpu); il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Callvirt, state.Methods.Single(m => m.Name == "set_CpuId"));
        il.Emit(OpCodes.Newobj, thread.Methods.Single(m => m.IsConstructor && !m.IsStatic)); il.Emit(OpCodes.Stloc, owner);
        void Set(string name, Action value)
        { il.Emit(OpCodes.Ldloc, owner); value(); il.Emit(OpCodes.Callvirt, thread.Methods.Single(m => m.Name == "set_" + name)); }
        Set("CpuId", () => il.Emit(OpCodes.Ldloc, index));
        Set("Id", () => { il.Emit(OpCodes.Ldsfld, nextId); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Dup); il.Emit(OpCodes.Stsfld, nextId); });
        Set("State", () => il.Emit(OpCodes.Ldc_I4_2));
        Set("Flags", () => il.Emit(OpCodes.Ldc_I4_4));
        void Value(int selector) { il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.Ldc_I4, selector); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.Call, field); }
        Set("StackBase", () => { Value(21); il.Emit(OpCodes.Conv_U); });
        Set("StackSize", () => { Value(22); il.Emit(OpCodes.Conv_U); });
        il.Emit(OpCodes.Ldloc, cpu); il.Emit(OpCodes.Ldloc, owner); il.Emit(OpCodes.Callvirt, state.Methods.Single(m => m.Name == "set_CurrentThread"));
        il.Emit(OpCodes.Ldloc, cpu); il.Emit(OpCodes.Ldloc, owner); il.Emit(OpCodes.Callvirt, state.Methods.Single(m => m.Name == "set_IdleThread"));
        il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Conv_U8); Value(21); Value(22); Value(23); Value(24); il.Emit(OpCodes.Call, configure); il.Emit(OpCodes.Brfalse, fail);
        il.Emit(OpCodes.Ldloc, owner); il.Emit(OpCodes.Call, registry);
        il.Emit(OpCodes.Ldloc, array); il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldloc, cpu); il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, index);
        il.Append(test); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Blt_Un, loop);
        il.Emit(OpCodes.Ldloc, array); il.Emit(OpCodes.Stsfld, states); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Stsfld, completed); il.Append(done); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.Ret);
        il.Append(fail); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.Ret); manager.Methods.Add(attach);
        // Permanent pinned owners are roots/TLAB/TLS containers, not policy-owned
        // runnable threads. Policy hand-over must never enqueue or exit them.
        var policy = manager.Methods.Single(m => m.Name == "SetScheduler");
        var guards = policy.Body.Instructions.Where(i => i.Operand is MethodReference m && m.Name == "get_State").ToArray();
        var dead = guards.Where(i => i.Next.OpCode == OpCodes.Ldc_I4_5 && (i.Next.Next.OpCode == OpCodes.Beq || i.Next.Next.OpCode == OpCodes.Beq_S)).ToArray();
        if (dead.Length != 2) throw new InvalidOperationException("Cosmos policy handover ownership contract changed");
        var pi = policy.Body.GetILProcessor();
        foreach (var read in dead)
        {
            var load = read.Previous; var skip = (Instruction)read.Next.Next.Operand;
            var copy = Instruction.Create(OpCodes.Nop); copy.OpCode = load.OpCode; copy.Operand = load.Operand;
            pi.InsertBefore(load, copy); pi.InsertBefore(load, pi.Create(OpCodes.Callvirt, thread.Methods.Single(m => m.Name == "get_CpuId")));
            pi.InsertBefore(load, pi.Create(OpCodes.Brtrue, skip));
        }
        // The BSP creates/registers every permanent owner atomically with
        // respect to its scheduler and thread-ID/registry writers.
        var core = new MethodDefinition("_zenithAttachPinnedCore", MethodAttributes.Private | MethodAttributes.Static, attach.ReturnType);
        core.Parameters.Add(new ParameterDefinition(module.TypeSystem.UInt64));
        var prior = attach.Body; core.Body = new MethodBody(core) { InitLocals = prior.InitLocals, MaxStackSize = prior.MaxStackSize };
        foreach (var v in prior.Variables) core.Body.Variables.Add(v);
        foreach (var i in prior.Instructions) core.Body.Instructions.Add(i);
        manager.Methods.Add(core);
        var cpuType = module.GetType("Cosmos.Kernel.Core.CPU.InternalCpu");
        var scope = cpuType.NestedTypes.Single(t => t.Name == "InterruptScope");
        var dispose = scope.Methods.Single(m => m.Name == "Dispose");
        attach.Body = new MethodBody(attach) { InitLocals = true }; var wrap = attach.Body.GetILProcessor();
        var flags = new VariableDefinition(scope); var result = new VariableDefinition(module.TypeSystem.UInt64);
        attach.Body.Variables.Add(flags); attach.Body.Variables.Add(result);
        wrap.Emit(OpCodes.Call, cpuType.Methods.Single(m => m.Name == "DisableInterruptsScope")); wrap.Emit(OpCodes.Stloc, flags);
        var begin = wrap.Create(OpCodes.Ldarg_0); wrap.Append(begin); wrap.Emit(OpCodes.Call, core); wrap.Emit(OpCodes.Stloc, result);
        wrap.Emit(OpCodes.Ldloca, flags); wrap.Emit(OpCodes.Call, dispose); var finish = wrap.Create(OpCodes.Ldloc, result); wrap.Emit(OpCodes.Leave, finish);
        var failed = wrap.Create(OpCodes.Pop); wrap.Append(failed); wrap.Emit(OpCodes.Ldloca, flags); wrap.Emit(OpCodes.Call, dispose); wrap.Emit(OpCodes.Rethrow);
        wrap.Append(finish); wrap.Emit(OpCodes.Ret);
        attach.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) { CatchType = module.ImportReference(typeof(Exception)), TryStart = begin, TryEnd = failed, HandlerStart = failed, HandlerEnd = finish });
        Console.WriteLine("Prepared permanent pinned AP scheduler/TLS/TLAB owners; BSP dispatch and policy ownership retained");
        return true;
    }
}
