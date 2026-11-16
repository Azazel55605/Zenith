using Mono.Cecil;
using Mono.Cecil.Cil;

// C5.2 (smp=managed): the general scheduler dispatches on every CPU, not only the BSP.
//
// - _zenithEnableManagedCpus (exported as zenith_managed_cpu_dispatch) gives the installed policy per-CPU
//   state for every AP that C3 attached a permanent pinned owner to, then raises the dispatch count to the
//   online count. The pinned owner is each AP's idle thread: its native worker loop runs when no managed
//   thread is ready (src/Native/Cpu/workers.c, managed_window).
// - ScheduleFromInterrupt on an AP tells the GC protocol which stack the CPU is about to run, so a stopped
//   AP is scanned over the stack of the thread it was running (src/Native/Cpu/gc.c).
// - ReadyThread kicks the thread's CPU when that is another CPU, so a wake there is seen at once.
// - Thread creation takes its CPU from Zenith's placement request (zenith_sched_place); without a request
//   it is the BSP, exactly as before, and the create/ready calls use the chosen CPU.
// - The BSP's LAPIC timer tick sends preemption ticks to APs running managed threads (Core.X64).
//
// A thread runs only on its CpuId, which changes only at creation. Nothing here moves running threads.
internal static class ManagedDispatchFixup
{
    private static MethodDefinition Native(ModuleDefinition module, TypeDefinition owner, string name, string entry, TypeReference result, params TypeReference[] parameters)
    {
        var library = module.ModuleReferences.FirstOrDefault(r => r.Name == "*");
        if (library is null) { library = new ModuleReference("*"); module.ModuleReferences.Add(library); }
        var method = new MethodDefinition(name, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.PInvokeImpl, result);
        foreach (var p in parameters) { method.Parameters.Add(new ParameterDefinition(p)); }
        method.PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl, entry, library);
        method.ImplAttributes = MethodImplAttributes.PreserveSig;
        method.CustomAttributes.Add(new CustomAttribute(module.ImportReference(
            typeof(System.Runtime.InteropServices.SuppressGCTransitionAttribute).GetConstructor(Type.EmptyTypes)!)));
        owner.Methods.Add(method);
        return method;
    }

    private static VariableDefinition? StoredLocal(MethodBody body, Instruction i) => i.OpCode.Code switch
    {
        Code.Stloc_0 => body.Variables[0], Code.Stloc_1 => body.Variables[1], Code.Stloc_2 => body.Variables[2], Code.Stloc_3 => body.Variables[3],
        Code.Stloc_S or Code.Stloc => (VariableDefinition)i.Operand, _ => null,
    };

    internal static bool Apply(ModuleDefinition module)
    {
        var manager = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerManager")
            ?? throw new InvalidOperationException("Cosmos scheduler manager changed");
        if (manager.Methods.Any(m => m.Name == "_zenithEnableManagedCpus")) { return false; }
        if (!manager.Methods.Any(m => m.Name == "_zenithAttachPinnedCpus") || !manager.Methods.Any(m => m.Name == "_zenithSchedLock"))
        { throw new InvalidOperationException("C5.2 needs the C3 pinned owners and the C5.1 scheduler lock"); }
        var thread = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerThread");
        var stateType = module.GetType("Cosmos.Kernel.Core.Scheduler.PerCpuState");
        var policyType = module.GetType("Cosmos.Kernel.Core.Scheduler.IScheduler");
        var u64 = module.TypeSystem.UInt64; var u32 = module.TypeSystem.UInt32;
        var getCpu = thread.Methods.Single(m => m.Name == "get_CpuId");
        var getBase = thread.Methods.Single(m => m.Name == "get_StackBase");
        var getSize = thread.Methods.Single(m => m.Name == "get_StackSize");
        var states = manager.Fields.Single(f => f.Name == "s_cpuStates");
        var dispatchCount = manager.Fields.Single(f => f.Name == "s_cpuCount");
        var policy = manager.Fields.Single(f => f.Name == "s_currentScheduler");
        var initialize = policyType.Methods.Single(m => m.Name == "InitializeCpu");
        var take = manager.Methods.Single(m => m.Name == "_zenithSchedLock");
        var release = manager.Methods.Single(m => m.Name == "_zenithSchedUnlock");
        var cpuType = module.GetType("Cosmos.Kernel.Core.CPU.InternalCpu");
        var scope = cpuType.NestedTypes.Single(t => t.Name == "InterruptScope");
        var mask = cpuType.Methods.Single(m => m.Name == "DisableInterruptsScope");
        var dispose = scope.Methods.Single(m => m.Name == "Dispose");
        if (dispatchCount.FieldType.MetadataType != MetadataType.UInt32 || getCpu.ReturnType.MetadataType != MetadataType.UInt32)
        { throw new InvalidOperationException("Cosmos dispatch count/CPU id types changed"); }

        // 1. Enable dispatch on every attached CPU. Returns the new count, or 0 when refused.
        var enable = new MethodDefinition("_zenithEnableManagedCpus", MethodAttributes.Private | MethodAttributes.Static, u64);
        var exported = module.GetType("Cosmos.Kernel.Core.Runtime.Memory").Methods.Single(m => m.Name == "RhpNewFast")
            .CustomAttributes.Single(a => a.AttributeType.Name == "RuntimeExportAttribute");
        var attr = new CustomAttribute(exported.Constructor);
        attr.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, "zenith_managed_cpu_dispatch"));
        enable.CustomAttributes.Add(attr);
        enable.Body.InitLocals = true;
        var irq = new VariableDefinition(scope); var index = new VariableDefinition(u32); var result = new VariableDefinition(u64);
        enable.Body.Variables.Add(irq); enable.Body.Variables.Add(index); enable.Body.Variables.Add(result);
        var il = enable.Body.GetILProcessor();
        il.Emit(OpCodes.Call, mask); il.Emit(OpCodes.Stloc, irq);
        il.Emit(OpCodes.Call, take); il.Emit(OpCodes.Pop);
        var finish = il.Create(OpCodes.Call, release);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.Stloc, result);
        // Refuse unless exactly one dispatch CPU, a policy, and at least two attached states.
        il.Emit(OpCodes.Ldsfld, dispatchCount); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Bne_Un, finish);
        il.Emit(OpCodes.Ldsfld, policy); il.Emit(OpCodes.Brfalse, finish);
        il.Emit(OpCodes.Ldsfld, states); il.Emit(OpCodes.Brfalse, finish);
        il.Emit(OpCodes.Ldsfld, states); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Ldc_I4_2); il.Emit(OpCodes.Blt, finish);
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stloc, index);
        var test = il.Create(OpCodes.Ldloc, index);
        il.Emit(OpCodes.Br, test);
        var body = il.Create(OpCodes.Ldsfld, policy);
        il.Append(body);
        il.Emit(OpCodes.Ldsfld, states); il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Callvirt, initialize);
        il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, index);
        il.Append(test); il.Emit(OpCodes.Ldsfld, states); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_U4); il.Emit(OpCodes.Blt_Un, body);
        il.Emit(OpCodes.Ldsfld, states); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_U4); il.Emit(OpCodes.Stsfld, dispatchCount);
        il.Emit(OpCodes.Ldsfld, dispatchCount); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.Stloc, result);
        il.Append(finish); il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldloca, irq); il.Emit(OpCodes.Call, dispose);
        il.Emit(OpCodes.Ldloc, result); il.Emit(OpCodes.Ret);
        manager.Methods.Add(enable);

        // 2. GC bounds follow the thread an AP is about to run.
        var running = Native(module, manager, "_zenithRunningStack", "zenith_gc_running_stack", u64, u64, u64);
        var cpuNow = manager.Methods.Single(m => m.Name == "GetCurrentCpuId");
        var schedule = manager.Methods.Single(m => m.Name == "ScheduleFromInterrupt" && m.HasBody);
        var setSp = schedule.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "SetContextSwitchSp").ToArray();
        if (setSp.Length != 1) { throw new InvalidOperationException("Cosmos dispatch switch point changed"); }
        // The argument is next.StackPointer: `ldloc next; callvirt get_StackPointer; call SetContextSwitchSp`.
        var loadNext = setSp[0].Previous.Previous;
        if (loadNext.Next.Operand is not MethodReference sp || sp.Name != "get_StackPointer") { throw new InvalidOperationException("Cosmos dispatch switch argument changed"); }
        var si = schedule.Body.GetILProcessor();
        var skip = loadNext;
        Instruction Copy(Instruction load) { var c = Instruction.Create(OpCodes.Nop); c.OpCode = load.OpCode; c.Operand = load.Operand; return c; }
        foreach (var i in new[] {
            si.Create(OpCodes.Call, cpuNow), si.Create(OpCodes.Brfalse, skip),
            Copy(loadNext), si.Create(OpCodes.Callvirt, getBase), si.Create(OpCodes.Conv_U8),
            Copy(loadNext), si.Create(OpCodes.Callvirt, getSize), si.Create(OpCodes.Conv_U8),
            si.Create(OpCodes.Call, running), si.Create(OpCodes.Pop) })
        { si.InsertBefore(loadNext, i); }

        // 2b. Only the BSP walks the thread registry for expired sleepers: every AP doing it on every tick, under the
        //     shared scheduler lock, was the main contention. Sleepers of other CPUs are readied onto their CPU and kicked.
        var timer = manager.Methods.Single(m => m.Name == "OnTimerInterrupt" && m.HasBody);
        var sleepers = timer.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "CheckSleepingThreads").ToArray();
        if (sleepers.Length != 1 || sleepers[0].Previous.OpCode.Code is not (Code.Ldarg_2 or Code.Ldarg_S or Code.Ldarg))
        { throw new InvalidOperationException("Cosmos sleeper check changed"); }
        var ti = timer.Body.GetILProcessor();
        var argument = sleepers[0].Previous;
        ti.InsertBefore(argument, ti.Create(OpCodes.Call, cpuNow));
        ti.InsertBefore(argument, ti.Create(OpCodes.Brtrue, sleepers[0].Next));

        // 2c. Every 10 ticks the timer path refreshed Cosmos's host-debug snapshots under the scheduler lock; the memory
        //     one scans every page. With every CPU's ticks counted that stalled all CPUs. Validation diagnostics read only
        //     the thread snapshot: it is refreshed on the BSP; the GC and memory snapshots are no longer refreshed.
        int snapshots = 0;
        foreach (var call in timer.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "Update"
            && r.DeclaringType.Name is "DebugLiveSnapshot" or "DebugLiveGCSnapshot" or "DebugLiveMemorySnapshot").ToArray())
        {
            var owner = ((MethodReference)call.Operand).DeclaringType.Name;
            if (owner == "DebugLiveSnapshot")
            {
                ti.InsertBefore(call, ti.Create(OpCodes.Call, cpuNow));
                ti.InsertBefore(call, ti.Create(OpCodes.Brtrue, call.Next));
            }
            else { call.OpCode = OpCodes.Nop; call.Operand = null; }
            snapshots++;
        }
        if (snapshots != 3) { throw new InvalidOperationException("Cosmos debug snapshot refresh changed"); }

        // 2d. A current thread that is not Running (exited, blocked, sleeping) always gives up the CPU at a tick. The stride
        //     policy only reschedules when its queue is non-empty, so an exited thread stayed current on an AP with nothing
        //     queued and its halt loop raised the scheduler vector over and over, each time under the shared lock.
        // ZENITH_SMP_TICK_FORCE=off leaves the policy's tick decision alone (A/B diagnostics only).
        if (Environment.GetEnvironmentVariable("ZENITH_SMP_TICK_FORCE") != "off") {
        var onTick = timer.Body.Instructions.Where(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference r && r.Name == "OnTick").ToArray();
        if (onTick.Length != 1 || onTick[0].Next.OpCode.Code is not (Code.Brfalse or Code.Brfalse_S)
            || onTick[0].Previous.OpCode.Code is not (Code.Ldarg_2 or Code.Ldarg_S or Code.Ldarg))
        { throw new InvalidOperationException("Cosmos tick decision changed"); }
        // Arguments: ldsfld policy; ldloc state; ldloc state; callvirt get_CurrentThread; ldarg elapsed; callvirt OnTick.
        var stateLoad = onTick[0].Previous.Previous.Previous;
        if (stateLoad.Next.Operand is not MethodReference current || current.Name != "get_CurrentThread") { throw new InvalidOperationException("Cosmos tick arguments changed"); }
        var getState = thread.Methods.Single(m => m.Name == "get_State");
        Instruction Load(Instruction i) { var c = Instruction.Create(OpCodes.Nop); c.OpCode = i.OpCode; c.Operand = i.Operand; return c; }
        var decisionBranch = onTick[0].Next;
        // decision || CurrentThread.State != Running
        foreach (var i in new[] {
            Load(stateLoad), ti.Create(OpCodes.Callvirt, current), ti.Create(OpCodes.Callvirt, getState),
            ti.Create(OpCodes.Ldc_I4_2), ti.Create(OpCodes.Ceq), ti.Create(OpCodes.Ldc_I4_0), ti.Create(OpCodes.Ceq), ti.Create(OpCodes.Or) })
        { ti.InsertBefore(decisionBranch, i); }
        }

        // 3. A wake on another CPU is seen at once.
        var kick = Native(module, manager, "_zenithKick", "zenith_cpu_kick", u64, u64);
        var ready = manager.Methods.Single(m => m.Name == "ReadyThread" && m.HasBody);
        if (ready.Parameters.Count != 2 || ready.Parameters[1].ParameterType.FullName != thread.FullName) { throw new InvalidOperationException("Cosmos ReadyThread changed"); }
        var ri = ready.Body.GetILProcessor();
        foreach (var ret in ready.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            var first = ri.Create(OpCodes.Ldarg_1);
            ri.InsertBefore(ret, first);
            ri.InsertAfter(first, ri.Create(OpCodes.Pop));
            ri.InsertAfter(first, ri.Create(OpCodes.Call, kick));
            ri.InsertAfter(first, ri.Create(OpCodes.Conv_U8));
            ri.InsertAfter(first, ri.Create(OpCodes.Callvirt, getCpu));
            foreach (var i in ready.Body.Instructions) { if (i.Operand == ret && i != first) { i.Operand = first; } }
            foreach (var h in ready.Body.ExceptionHandlers) { if (h.HandlerEnd == ret) { h.HandlerEnd = first; } }
        }

        // 3b. A thread is queued at most once. A thread readied while it is still its CPU's current thread (woken
        //     between blocking and switching away) was queued by ReadyThread and again by its own switch-out, which
        //     then still saw it Ready. The second entry could resume it after it had exited, on a stack the collector
        //     no longer scans. Every insertion now first removes an existing entry for the same thread.
        var stride = module.GetType("Cosmos.Kernel.Core.Scheduler.Stride.StrideScheduler")
            ?? throw new InvalidOperationException("Cosmos stride scheduler changed");
        var insert = stride.Methods.Single(m => m.Name == "InsertByPass" && m.HasBody);
        var removeFromQueue = stride.Methods.Single(m => m.Name == "RemoveThreadFromQueue" && m.HasBody);
        if (insert.Parameters.Count != 2 || insert.Parameters[1].ParameterType.FullName != thread.FullName || removeFromQueue.Parameters.Count != 2)
        { throw new InvalidOperationException("Cosmos stride InsertByPass changed"); }
        var runQueue = insert.Parameters[0].ParameterType.Resolve().Methods.Single(m => m.Name == "get_RunQueue");
        {
            var qi = insert.Body.GetILProcessor();
            var start = insert.Body.Instructions[0];
            foreach (var i in new[] {
                qi.Create(OpCodes.Ldarg_0), qi.Create(OpCodes.Ldarg_1), qi.Create(OpCodes.Callvirt, module.ImportReference(runQueue)),
                qi.Create(OpCodes.Ldarg_2), qi.Create(OpCodes.Call, removeFromQueue) })
            { qi.InsertBefore(start, i); }
        }

        // 4. Placement at creation.
        var native = module.Types.First(t => t.Name == "libSystemNative");
        var create = native.Methods.Single(m => m.Name == "SystemNative_CreateThread" && m.HasBody);
        var place = Native(module, native, "_zenithPlace", "zenith_sched_place", u32, u64);
        var code = create.Body.Instructions;
        var setCpu = code.Where(i => i.Operand is MethodReference r && r.Name == "set_CpuId").ToArray();
        if (setCpu.Length != 1 || setCpu[0].Previous.OpCode != OpCodes.Ldc_I4_0) { throw new InvalidOperationException("Cosmos thread creation CPU contract changed"); }
        var size = create.Body.Variables.FirstOrDefault(v => v.VariableType.MetadataType == MetadataType.UIntPtr)
            ?? throw new InvalidOperationException("Cosmos thread creation stack size local changed");
        var ci = create.Body.GetILProcessor();
        var zero = setCpu[0].Previous;
        zero.OpCode = OpCodes.Ldloc; zero.Operand = size;
        ci.InsertAfter(zero, ci.Create(OpCodes.Call, place));
        ci.InsertAfter(zero, ci.Create(OpCodes.Conv_U8));
        int replaced = 0;
        foreach (var call in code.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.DeclaringType.Name == "SchedulerManager" && r.Name is "CreateThread" or "ReadyThread").ToArray())
        {
            var load = call.Previous; var cpu = load.Previous;
            if (cpu.OpCode != OpCodes.Ldc_I4_0) { throw new InvalidOperationException("Cosmos thread creation dispatch changed"); }
            cpu.OpCode = load.OpCode; cpu.Operand = load.Operand;
            ci.InsertAfter(cpu, ci.Create(OpCodes.Callvirt, getCpu));
            replaced++;
        }
        if (replaced != 2) { throw new InvalidOperationException("Cosmos thread creation dispatch changed"); }
        // The first iretq frame carries the creating CPU's code selector (the BSP's loader selector 0x28). An AP's
        // GDT has its kernel code at 8: a thread placed on an AP starts with that.
        var selector = code.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "GetCurrentCodeSelector").ToArray();
        if (selector.Length != 1 || selector[0].Next.OpCode != OpCodes.Conv_U2 || StoredLocal(create.Body, selector[0].Next.Next) is not VariableDefinition csLocal)
        { throw new InvalidOperationException("Cosmos thread creation code selector changed"); }
        var threadLoad = code.First(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "CreateThread").Previous;
        var store = selector[0].Next.Next; var after = store.Next;
        var loadThread = Instruction.Create(OpCodes.Nop);
        // `ldloc thread` right before the CreateThread call (its CPU argument, rewritten above, precedes it).
        if (threadLoad.OpCode.Code is not (Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3 or Code.Ldloc_S or Code.Ldloc))
        { throw new InvalidOperationException("Cosmos thread creation argument changed"); }
        loadThread.OpCode = threadLoad.OpCode; loadThread.Operand = threadLoad.Operand;
        ci.InsertBefore(after, loadThread);
        ci.InsertBefore(after, ci.Create(OpCodes.Callvirt, getCpu));
        ci.InsertBefore(after, ci.Create(OpCodes.Brfalse, after));
        ci.InsertBefore(after, ci.Create(OpCodes.Ldc_I4_8));
        ci.InsertBefore(after, ci.Create(OpCodes.Stloc, csLocal));
        // 5. Shared GC state that is not an allocation entry: on one CPU masking was enough, with several CPUs these
        //    take the C3 heap gate too. A thread returns its TLAB to the free list wherever it exits, and GC handles
        //    (Thread objects, delegates) are allocated and freed from any thread; the handle manager has no lock.
        var gcType = module.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector");
        var memory = module.GetType("Cosmos.Kernel.Core.Runtime.Memory");
        var gated = new[] {
            gcType.Methods.Single(m => m.Name == "ReturnAllocContext" && m.HasBody),
            gcType.Methods.Single(m => m.Name == "AddDeadThreadNonAllocBytes" && m.HasBody),
            memory.Methods.Single(m => m.Name == "RhHandleFree" && m.HasBody),
            memory.Methods.Single(m => m.Name == "RhpHandleAlloc" && m.HasBody),
            memory.Methods.Single(m => m.Name == "RhpHandleAllocDependent" && m.HasBody) };
        Gate(module, gcType, gated, mask, dispose, scope);

        // 6. The TLAB fast path needs no global gate. An allocation runs with interrupts masked from reservation to
        //    header publication, and a CPU acknowledges a GC stop only through its stop interrupt or while it waits on the
        //    gate, so no collection can observe a half-published object. The global gate on every allocation made all
        //    CPUs take turns allocating. It now guards only what is shared: the slow path (free lists, segments, collection)
        //    and pinned allocations.
        // ZENITH_SMP_ALLOCATION_GATE=keep leaves the gate on every allocation (A/B diagnostics only).
        bool keepGate = Environment.GetEnvironmentVariable("ZENITH_SMP_ALLOCATION_GATE") == "keep";
        var collectorEnter = gcType.Methods.Single(m => m.Name == "_zenith_gc_enter");
        var collectorExit = gcType.Methods.Single(m => m.Name == "_zenith_gc_exit");
        var wrappers = memory.Methods.Where(m => m.HasBody && m.Body.Instructions.Any(i => i.Operand is MethodReference c && c.Name.StartsWith("_zenithPublish_"))).ToArray();
        if (wrappers.Length != 6) { throw new InvalidOperationException("Publication wrapper contract changed"); }
        int enters = 0, exits = 0;
        foreach (var wrapper in keepGate ? Array.Empty<MethodDefinition>() : wrappers)
        {
            foreach (var call in wrapper.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && (r.Resolve() == collectorEnter || r.Resolve() == collectorExit)).ToArray())
            {
                // `call gate; ldc.i4.0; conv.u8; bne.un next; call fatal` (GcBarrierFixup)
                var seq = new[] { call, call.Next, call.Next.Next, call.Next.Next.Next, call.Next.Next.Next.Next };
                if (seq[1].OpCode != OpCodes.Ldc_I4_0 || seq[2].OpCode != OpCodes.Conv_U8 || seq[3].OpCode != OpCodes.Bne_Un
                    || seq[4].OpCode != OpCodes.Call || (Instruction)seq[3].Operand != seq[4].Next)
                { throw new InvalidOperationException("Publication gate sequence changed in " + wrapper.Name); }
                if (((MethodReference)call.Operand).Resolve() == collectorEnter) { enters++; } else { exits++; }
                foreach (var i in seq) { i.OpCode = OpCodes.Nop; i.Operand = null; }
            }
        }
        // One enter and an exit on both the normal and the exceptional path of each wrapper.
        if (!keepGate && (enters != 6 || exits != 12)) { throw new InvalidOperationException("Publication gate count changed: " + enters + "/" + exits); }
        Gate(module, gcType, new[] {
            gcType.Methods.Single(m => m.Name == "AllocObjectSlow" && m.HasBody),
            gcType.Methods.Single(m => m.Name == "AllocPinnedObject" && m.HasBody) }, mask, dispose, scope);
        Console.WriteLine("Adapted managed dispatch for smp=managed: per-CPU policy state, running-stack GC bounds, cross-CPU kicks, placement at creation");
        return true;
    }

    // The RuntimeHeapFixup pattern: the original body moves to a core method; the entry masks interrupts, takes the
    // recursive GC gate (fail-stop when refused), calls the core and releases both, also when it throws.
    private static void Gate(ModuleDefinition module, TypeDefinition gc, MethodDefinition[] targets, MethodDefinition mask, MethodDefinition dispose, TypeDefinition scope)
    {
        var enter = gc.Methods.Single(m => m.Name == "_zenith_gc_enter"); var exit = gc.Methods.Single(m => m.Name == "_zenith_gc_exit");
        var fatal = gc.Methods.Single(m => m.Name == "_zenith_gc_fail_stop");
        foreach (var method in targets)
        {
            if (!method.IsStatic) { throw new InvalidOperationException("Cosmos gated entry " + method.Name + " changed"); }
            var core = new MethodDefinition("_zenithSmp_" + method.Name, MethodAttributes.Private | MethodAttributes.Static, method.ReturnType);
            foreach (var p in method.Parameters) { core.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType)); }
            var old = method.Body; core.Body = new MethodBody(core) { InitLocals = old.InitLocals, MaxStackSize = old.MaxStackSize };
            foreach (var v in old.Variables) { core.Body.Variables.Add(v); }
            foreach (var i in old.Instructions) { if (i.Operand is ParameterDefinition p) { i.Operand = core.Parameters[p.Index]; } core.Body.Instructions.Add(i); }
            foreach (var handler in old.ExceptionHandlers) { core.Body.ExceptionHandlers.Add(handler); }
            method.DeclaringType.Methods.Add(core);
            method.ImplAttributes = (method.ImplAttributes & ~MethodImplAttributes.AggressiveInlining) | MethodImplAttributes.NoInlining;
            method.Body = new MethodBody(method) { InitLocals = true };
            var irq = new VariableDefinition(scope); method.Body.Variables.Add(irq);
            VariableDefinition? result = null;
            if (method.ReturnType.MetadataType != MetadataType.Void) { result = new VariableDefinition(method.ReturnType); method.Body.Variables.Add(result); }
            var il = method.Body.GetILProcessor(); il.Emit(OpCodes.Call, mask); il.Emit(OpCodes.Stloc, irq);
            il.Emit(OpCodes.Call, enter); var begin = il.Create(OpCodes.Nop); il.Emit(OpCodes.Brtrue, begin); il.Emit(OpCodes.Call, fatal); il.Append(begin);
            foreach (var p in method.Parameters) { il.Emit(OpCodes.Ldarg, p); }
            il.Emit(OpCodes.Call, core);
            if (result is not null) { il.Emit(OpCodes.Stloc, result); }
            void Finish()
            {
                il.Emit(OpCodes.Call, exit); var ok = il.Create(OpCodes.Ldloca, irq); il.Emit(OpCodes.Brtrue, ok); il.Emit(OpCodes.Call, fatal);
                il.Append(ok); il.Emit(OpCodes.Call, dispose);
            }
            Finish(); var done = il.Create(OpCodes.Nop); il.Emit(OpCodes.Leave, done);
            var failed = il.Create(OpCodes.Pop); il.Append(failed); Finish(); il.Emit(OpCodes.Rethrow); il.Append(done);
            if (result is not null) { il.Emit(OpCodes.Ldloc, result); }
            il.Emit(OpCodes.Ret);
            method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) { CatchType = module.ImportReference(typeof(Exception)), TryStart = begin, TryEnd = failed, HandlerStart = failed, HandlerEnd = done });
        }
    }

    // Core.X64: the BSP's tick also ticks APs that run managed threads.
    internal static bool Timer(ModuleDefinition module)
    {
        var apic = module.GetType("Cosmos.Kernel.Core.X64.Cpu.LocalApic") ?? module.Types.FirstOrDefault(t => t.Name == "LocalApic");
        if (apic is null) { return false; }
        if (apic.Methods.Any(m => m.Name == "_zenithTickAps")) { return false; }
        var handler = apic.Methods.Single(m => m.Name == "HandleTimerInterrupt" && m.HasBody);
        var tick = Native(module, apic, "_zenithTickAps", "zenith_cpu_tick_aps", module.TypeSystem.Void);
        var il = handler.Body.GetILProcessor();
        il.InsertBefore(handler.Body.Instructions[0], il.Create(OpCodes.Call, tick));
        Console.WriteLine("Adapted the BSP timer tick to tick APs that run managed threads");
        return true;
    }
}
