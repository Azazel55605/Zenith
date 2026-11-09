using Mono.Cecil;
using Mono.Cecil.Cil;

// The kernel loop is the scheduler's idle thread: it runs only when no other thread is ready.
// Every pinned wait (sleep, timed condition/monitor wait, contended mutex, interrupt event,
// exit) marks its thread Sleeping/Blocked/Dead and then halts in place. Nothing switches
// away from it until the next 10 ms timer tick, so a parked thread kept the BSP, the desktop
// included, halted for up to a tick per wait. A halt of a parked non-idle thread goes to
// zenith_cpu_park_counted instead, which raises the scheduler's own interrupt so its exit
// switches away at once; every other halt (the idle loop, a running thread) is unchanged.
internal static class ParkSwitchFixup
{
    private const string Park = "_zenithParkSwitch", Parking = "_zenithParking";

    private static ModuleReference Library(ModuleDefinition module)
    {
        var library = module.ModuleReferences.FirstOrDefault(r => r.Name == "*");
        if (library is null) { library = new ModuleReference("*"); module.ModuleReferences.Add(library); }
        return library;
    }

    private static MethodDefinition Native(ModuleDefinition module, TypeDefinition owner, string name, string entry, TypeReference result)
    {
        // Internal: the stride scheduler reads the policy too.
        var method = new MethodDefinition(name, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.PInvokeImpl, result);
        method.PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl, entry, Library(module));
        method.ImplAttributes = MethodImplAttributes.PreserveSig;
        method.CustomAttributes.Add(new CustomAttribute(module.ImportReference(
            typeof(System.Runtime.InteropServices.SuppressGCTransitionAttribute).GetConstructor(Type.EmptyTypes)!)));
        owner.Methods.Add(method);
        return method;
    }

    // The kernel loop runs only when no other thread is ready. A program's busy service thread, or a
    // few of them taking turns, kept it off the CPU for many ticks: drags and animations stopped.
    // Picking the idle thread when it has not run for 6 ms (policy bit 2) gives it a turn at every
    // timer tick at the latest; it hands the CPU back as soon as it would halt (bit 4).
    private static void ShareDesktop(ModuleDefinition module, TypeDefinition perCpu, MethodDefinition currentThread,
        MethodDefinition idleThread, MethodDefinition policy)
    {
        var stride = module.GetType("Cosmos.Kernel.Core.Scheduler.Stride.StrideScheduler")
            ?? throw new InvalidOperationException("Cosmos stride scheduler changed");
        var pick = stride.Methods.Single(m => m.Name == "PickNext");
        // The scheduler's own clock: its GetTimestamp body is one Stopwatch.GetTimestamp call.
        var clock = stride.Methods.Single(m => m.Name == "GetTimestamp" && m.Parameters.Count == 0).Body.Instructions;
        if (clock.Count != 2 || !(clock[0].Operand is MethodReference read && read.Name == "GetTimestamp" && read.DeclaringType.Name == "Stopwatch"))
        { throw new InvalidOperationException("Cosmos stride clock changed"); }
        var timestamp = (MethodReference)clock[0].Operand;
        var quantum = stride.Fields.Single(f => f.Name == "s_ticksPerQuantum" && f.IsStatic);
        if (pick.Parameters.Count != 1 || pick.Parameters[0].ParameterType.FullName != perCpu.FullName
            || timestamp.ReturnType.MetadataType != MetadataType.Int64 || quantum.FieldType.MetadataType != MetadataType.UInt64
            || pick.Body.Instructions[0].OpCode != OpCodes.Ldarg_1)
        { throw new InvalidOperationException("Cosmos stride pick contract changed"); }
        var left = new FieldDefinition("_zenithIdleLeft", FieldAttributes.Private | FieldAttributes.Static, module.TypeSystem.UInt64);
        stride.Fields.Add(left);
        var due = new MethodDefinition("_zenithDesktopDue", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Boolean);
        due.Parameters.Add(new ParameterDefinition(perCpu));
        due.Body.InitLocals = true;
        var prev = new VariableDefinition(currentThread.ReturnType); var idle = new VariableDefinition(idleThread.ReturnType);
        var now = new VariableDefinition(module.TypeSystem.UInt64);
        due.Body.Variables.Add(prev); due.Body.Variables.Add(idle); due.Body.Variables.Add(now);
        var il = due.Body.GetILProcessor();
        var no = il.Create(OpCodes.Ldc_I4_0); var other = il.Create(OpCodes.Call, policy);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt, currentThread); il.Emit(OpCodes.Stloc, prev);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt, idleThread); il.Emit(OpCodes.Stloc, idle);
        il.Emit(OpCodes.Ldloc, prev); il.Emit(OpCodes.Brfalse, no);
        il.Emit(OpCodes.Ldloc, idle); il.Emit(OpCodes.Brfalse, no);
        il.Emit(OpCodes.Call, timestamp); il.Emit(OpCodes.Stloc, now);
        il.Emit(OpCodes.Ldloc, prev); il.Emit(OpCodes.Ldloc, idle); il.Emit(OpCodes.Bne_Un, other);
        // The idle thread is leaving (or staying with nothing queued): remember when.
        il.Emit(OpCodes.Ldloc, now); il.Emit(OpCodes.Stsfld, left); il.Emit(OpCodes.Br, no);
        il.Append(other); il.Emit(OpCodes.Ldc_I4_2); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.And); il.Emit(OpCodes.Brfalse, no);
        il.Emit(OpCodes.Ldloc, now); il.Emit(OpCodes.Ldsfld, left); il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Ldsfld, quantum); il.Emit(OpCodes.Ldc_I4_3); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Ldc_I4_5); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.Div_Un);
        il.Emit(OpCodes.Clt_Un); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Ret);
        il.Append(no); il.Emit(OpCodes.Ret);
        stride.Methods.Add(due);
        var code = pick.Body.GetILProcessor(); var first = pick.Body.Instructions[0];
        code.InsertBefore(first, code.Create(OpCodes.Ldarg_1));
        code.InsertBefore(first, code.Create(OpCodes.Call, due));
        code.InsertBefore(first, code.Create(OpCodes.Brfalse, first));
        code.InsertBefore(first, code.Create(OpCodes.Ldnull));
        code.InsertBefore(first, code.Create(OpCodes.Ret));
        pick.Body.MaxStackSize = Math.Max(pick.Body.MaxStackSize, 2);
        Console.WriteLine("Adapted stride pick: the kernel loop gets the CPU when it has not run for 6 ms");
    }

    internal static bool Apply(ModuleDefinition module)
    {
        // x64 only: the ARM64 runtime keeps its own halt and timer.
        if (!module.GetType("Cosmos.Kernel.Core.Scheduler.ThreadContext").Fields.Any(f => f.Name == "Xmm")) { return false; }
        var cpu = module.GetType("Cosmos.Kernel.Core.CPU.InternalCpu")
            ?? throw new InvalidOperationException("Cosmos InternalCpu changed");
        var halt = cpu.Methods.Single(m => m.Name == "Halt" && m.Parameters.Count == 0);
        if (cpu.Methods.Any(m => m.Name == Park))
        {
            var first = halt.Body.Instructions[0];
            if (!(first.Operand is MethodReference called && called.Name == Parking)
                || halt.Body.Instructions.Count(i => i.Operand is MethodReference m && m.Name == Park) != 1)
            { throw new InvalidOperationException("Incomplete park switch adaptation"); }
            return false;
        }
        var original = halt.Body.Instructions;
        if (!halt.IsStatic || halt.ReturnType.MetadataType != MetadataType.Void || original.Count != 2
            || !(original[0].OpCode == OpCodes.Call && original[0].Operand is MethodReference native
                && native.Name == "Halt" && native.DeclaringType.Name == "CpuNative")
            || original[1].OpCode != OpCodes.Ret)
        { throw new InvalidOperationException("Cosmos halt body changed"); }

        var scheduler = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerManager");
        var perCpu = module.GetType("Cosmos.Kernel.Core.Scheduler.PerCpuState");
        var thread = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerThread");
        var states = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerThreadState");
        if (Convert.ToInt32(states.Fields.Single(f => f.Name == "Blocked").Constant) != 3
            || Convert.ToInt32(states.Fields.Single(f => f.Name == "Sleeping").Constant) != 4
            || Convert.ToInt32(states.Fields.Single(f => f.Name == "Dead").Constant) != 5)
        { throw new InvalidOperationException("Cosmos thread state contract changed"); }
        var currentState = scheduler.Methods.Single(m => m.Name == "get_CurrentCpuState" && m.IsStatic);
        var currentThread = perCpu.Methods.Single(m => m.Name == "get_CurrentThread");
        var idleThread = perCpu.Methods.Single(m => m.Name == "get_IdleThread");
        var state = thread.Methods.Single(m => m.Name == "get_State");
        if (currentState.ReturnType.FullName != perCpu.FullName || currentThread.ReturnType.FullName != thread.FullName
            || idleThread.ReturnType.FullName != thread.FullName || state.ReturnType.FullName != states.FullName)
        { throw new InvalidOperationException("Cosmos scheduler state accessors changed"); }

        var strideData = module.GetType("Cosmos.Kernel.Core.Scheduler.Stride.StrideCpuData")
            ?? throw new InvalidOperationException("Cosmos stride CPU data changed");
        var schedulerData = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerExtensible").Methods.Single(m => m.Name == "get_SchedulerData");
        var runQueueGetter = strideData.Methods.Single(m => m.Name == "get_RunQueue");
        var runQueue = runQueueGetter;
        var count = module.ImportReference(new MethodReference("get_Count", module.TypeSystem.Int32, runQueueGetter.ReturnType) { HasThis = true });
        var queued = Instruction.Create(OpCodes.Nop);
        var policy = Native(module, cpu, "_zenithParkPolicy", "zenith_cpu_park_policy", module.TypeSystem.UInt64);

        var park = Native(module, cpu, Park, "zenith_cpu_park_counted", module.TypeSystem.Void);

        // bool _zenithParking(): the current thread is a non-idle thread that is not runnable.
        var parking = new MethodDefinition(Parking, MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Boolean);
        parking.Body.InitLocals = true;
        var cpuLocal = new VariableDefinition(perCpu);
        var threadLocal = new VariableDefinition(thread);
        var stateLocal = new VariableDefinition(module.TypeSystem.Int32);
        parking.Body.Variables.Add(cpuLocal); parking.Body.Variables.Add(threadLocal); parking.Body.Variables.Add(stateLocal);
        var il = parking.Body.GetILProcessor();
        var no = il.Create(OpCodes.Ldc_I4_0);
        var yes = il.Create(OpCodes.Ldc_I4_1);
        var idle = il.Create(OpCodes.Call, policy);
        il.Emit(OpCodes.Call, currentState); il.Emit(OpCodes.Stloc, cpuLocal);
        il.Emit(OpCodes.Ldloc, cpuLocal); il.Emit(OpCodes.Brfalse, no);
        il.Emit(OpCodes.Ldloc, cpuLocal); il.Emit(OpCodes.Callvirt, currentThread); il.Emit(OpCodes.Stloc, threadLocal);
        il.Emit(OpCodes.Ldloc, threadLocal); il.Emit(OpCodes.Brfalse, no);
        il.Emit(OpCodes.Ldloc, threadLocal); il.Emit(OpCodes.Ldloc, cpuLocal); il.Emit(OpCodes.Callvirt, idleThread); il.Emit(OpCodes.Beq, idle);
        il.Emit(OpCodes.Ldloc, threadLocal); il.Emit(OpCodes.Callvirt, state); il.Emit(OpCodes.Stloc, stateLocal);
        il.Emit(OpCodes.Ldloc, stateLocal); il.Emit(OpCodes.Ldc_I4_3); il.Emit(OpCodes.Blt, no);
        il.Emit(OpCodes.Ldloc, stateLocal); il.Emit(OpCodes.Ldc_I4_5); il.Emit(OpCodes.Ble, yes);
        il.Append(no); il.Emit(OpCodes.Ret);
        // The idle thread (the kernel loop) about to halt while threads are queued: hand them the CPU
        // now instead of halting past them until the next interrupt.
        il.Append(idle); il.Emit(OpCodes.Ldc_I4_4); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.And); il.Emit(OpCodes.Brfalse, no);
        il.Emit(OpCodes.Ldloc, cpuLocal); il.Emit(OpCodes.Callvirt, schedulerData); il.Emit(OpCodes.Isinst, strideData);
        il.Emit(OpCodes.Dup); il.Emit(OpCodes.Brtrue, queued); il.Emit(OpCodes.Pop); il.Emit(OpCodes.Br, no);
        il.Append(queued); il.Emit(OpCodes.Callvirt, runQueue); il.Emit(OpCodes.Callvirt, count);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt); il.Emit(OpCodes.Ret);
        il.Append(yes); il.Emit(OpCodes.Ret);
        cpu.Methods.Add(parking);
        ShareDesktop(module, perCpu, currentThread, idleThread, policy);

        var code = halt.Body.GetILProcessor();
        var plain = original[0];
        code.InsertBefore(plain, code.Create(OpCodes.Call, parking));
        code.InsertBefore(plain, code.Create(OpCodes.Brfalse, plain));
        code.InsertBefore(plain, code.Create(OpCodes.Call, park));
        code.InsertBefore(plain, code.Create(OpCodes.Ret));
        halt.Body.MaxStackSize = Math.Max(halt.Body.MaxStackSize, 1);
        Console.WriteLine("Adapted managed halt: a parked thread switches away at once instead of holding the CPU until the next tick");
        return true;
    }
}
