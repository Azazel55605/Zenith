using Mono.Cecil;
using Mono.Cecil.Cil;

internal static unsafe class StoppedRootAlgorithm
{
    internal static void Scan()
    {
        for (ulong cpu = 1; cpu < 8; cpu++)
        {
            nuint frame = (nuint)Root(cpu, 0);
            if (frame == 0) { continue; }
            nuint words = (nuint)Root(cpu, 1), low = (nuint)Root(cpu, 2), high = (nuint)Root(cpu, 3);
            if (words != 79 || high < low || high - low > 1048576) { Fail(); return; }
            for (nuint i = 0; i < words; i++) { Mark(((nint*)frame)[i]); }
            for (nuint p = low & ~(nuint)7; p < high; p += 8) { Mark(*(nint*)p); }
        }
    }
    private static ulong Root(ulong cpu, ulong field) => throw new InvalidOperationException();
    private static void Mark(nint value) => throw new InvalidOperationException();
    private static void Fail() => throw new InvalidOperationException();
}

internal static class GcBarrierFixup
{
    internal static bool Apply(ModuleDefinition module)
    {
        var gc = module.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector");
        const string marker = "_zenithStoppedCollect";
        if (gc.Methods.Any(m => m.Name == marker)) { return false; }
        // ARM64 remains single-CPU; this x64 native protocol is not transplanted there.
        if (!module.GetType("Cosmos.Kernel.Core.Scheduler.ThreadContext").Fields.Any(f => f.Name == "Xmm")) return false;
        var cpu = module.GetType("Cosmos.Kernel.Core.CPU.InternalCpu");
        var scope = cpu.NestedTypes.Single(t => t.Name == "InterruptScope");
        var mask = cpu.Methods.Single(m => m.Name == "DisableInterruptsScope");
        var dispose = scope.Methods.Single(m => m.Name == "Dispose");
        var memory = module.GetType("Cosmos.Kernel.Core.Runtime.Memory");
        var collect = gc.Methods.Single(m => m.Name == "Collect" && m.Parameters.Count == 0);
        if (collect.ReturnType.MetadataType != MetadataType.Int32 || !collect.HasBody)
            throw new InvalidOperationException("Cosmos collector entry changed");
        var mark = gc.Methods.Single(m => m.Name == "_zenithMarkConservativeRoot");
        MethodDefinition Native(string name, TypeReference result, params TypeReference[] args)
        {
            var library = module.ModuleReferences.FirstOrDefault(r => r.Name == "*");
            if (library is null) { library = new ModuleReference("*"); module.ModuleReferences.Add(library); }
            var method = new MethodDefinition("_" + name, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.PInvokeImpl, result);
            foreach (var arg in args) method.Parameters.Add(new ParameterDefinition(arg));
            method.PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl, name, library);
            method.ImplAttributes = MethodImplAttributes.PreserveSig;
            method.CustomAttributes.Add(new CustomAttribute(module.ImportReference(
                typeof(System.Runtime.InteropServices.SuppressGCTransitionAttribute).GetConstructor(Type.EmptyTypes)!)));
            gc.Methods.Add(method); return method;
        }
        var enter = Native("zenith_gc_enter", module.TypeSystem.UInt64);
        var exit = Native("zenith_gc_exit", module.TypeSystem.UInt64);
        var begin = Native("zenith_gc_begin", module.TypeSystem.UInt64);
        var end = Native("zenith_gc_end", module.TypeSystem.UInt64, module.TypeSystem.UInt64);
        var roots = Native("zenith_gc_root", module.TypeSystem.UInt64, module.TypeSystem.UInt64, module.TypeSystem.UInt64);
        var fatal = Native("zenith_gc_fail_stop", module.TypeSystem.Void);
        var abort = Native("zenith_gc_abort", module.TypeSystem.Void, module.TypeSystem.UInt64);
        var wrappers = memory.Methods.Where(m => m.HasBody && m.Body.Instructions.Any(i =>
            i.Operand is MethodReference call && call.Name.StartsWith("_zenithPublish_"))).ToArray();
        if (wrappers.Length != 6) throw new InvalidOperationException("Publication wrapper contract changed");
        foreach (var method in wrappers)
        {
            var il = method.Body.GetILProcessor();
            var region = method.Body.ExceptionHandlers.Single();
            var target = region.TryStart;
            foreach (var i in new[] { il.Create(OpCodes.Call,enter),il.Create(OpCodes.Ldc_I4_0),il.Create(OpCodes.Conv_U8),
                il.Create(OpCodes.Bne_Un,target),il.Create(OpCodes.Call,fatal) }) il.InsertBefore(target, i);
            // Acquisition is outside the catch: only a successful owner releases.
            foreach (var call in method.Body.Instructions.Where(i => i.Operand is MethodReference m && m.FullName == dispose.FullName).ToArray())
            {
                var first = call.Previous;
                if (first.OpCode != OpCodes.Ldloca) throw new InvalidOperationException("Publication dispose changed");
                foreach (var i in new[] { il.Create(OpCodes.Call,exit),il.Create(OpCodes.Ldc_I4_0),il.Create(OpCodes.Conv_U8),
                    il.Create(OpCodes.Bne_Un,first),il.Create(OpCodes.Call,fatal) }) il.InsertBefore(first, i);
            }
        }
        // Move the entire old collector body intact; no part may run on refusal.
        var body = new MethodDefinition(marker, MethodAttributes.Private | MethodAttributes.Static, collect.ReturnType);
        var previous = collect.Body;
        body.Body = new MethodBody(body) { InitLocals = previous.InitLocals, MaxStackSize = previous.MaxStackSize };
        foreach (var variable in previous.Variables) body.Body.Variables.Add(variable);
        foreach (var instruction in previous.Instructions) body.Body.Instructions.Add(instruction);
        foreach (var handler in previous.ExceptionHandlers) body.Body.ExceptionHandlers.Add(handler);
        gc.Methods.Add(body);
        collect.Body = new MethodBody(collect) { InitLocals = true };
        var irq = new VariableDefinition(scope); var epoch = new VariableDefinition(module.TypeSystem.UInt64);
        var freed = new VariableDefinition(module.TypeSystem.Int32);
        collect.Body.Variables.Add(irq); collect.Body.Variables.Add(epoch); collect.Body.Variables.Add(freed);
        var code = collect.Body.GetILProcessor();
        code.Emit(OpCodes.Call, mask); code.Emit(OpCodes.Stloc, irq);
        code.Emit(OpCodes.Call, begin); code.Emit(OpCodes.Stloc, epoch);
        var start = code.Create(OpCodes.Nop); code.Emit(OpCodes.Ldloc, epoch); code.Emit(OpCodes.Brtrue, start);
        code.Emit(OpCodes.Ldloca, irq); code.Emit(OpCodes.Call, dispose); code.Emit(OpCodes.Ldc_I4_0); code.Emit(OpCodes.Ret);
        code.Append(start); code.Emit(OpCodes.Call, body); code.Emit(OpCodes.Stloc, freed);
        void Finish()
        {
            code.Emit(OpCodes.Ldloc, epoch); code.Emit(OpCodes.Call, end);
            var ok = code.Create(OpCodes.Ldloca, irq); code.Emit(OpCodes.Brtrue, ok); code.Emit(OpCodes.Call, fatal);
            code.Append(ok); code.Emit(OpCodes.Call, dispose);
        }
        Finish(); var done = code.Create(OpCodes.Nop); code.Emit(OpCodes.Leave, done);
        var failed = code.Create(OpCodes.Pop); code.Append(failed); code.Emit(OpCodes.Ldloc, epoch); code.Emit(OpCodes.Call, abort); code.Emit(OpCodes.Call, fatal); code.Emit(OpCodes.Rethrow);
        code.Append(done); code.Emit(OpCodes.Ldloc, freed); code.Emit(OpCodes.Ret);
        collect.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = module.ImportReference(typeof(Exception)),
            TryStart = start,
            TryEnd = failed,
            HandlerStart = failed,
            HandlerEnd = done
        });

        using var source = AssemblyDefinition.ReadAssembly(typeof(StoppedRootAlgorithm).Assembly.Location);
        var scanSource = source.MainModule.GetType(nameof(StoppedRootAlgorithm)).Methods.Single(m => m.Name == "Scan");
        var scan = new MethodDefinition("_zenithScanStoppedRoots", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Void);
        scan.Body.InitLocals = true;
        foreach (var variable in scanSource.Body.Variables) scan.Body.Variables.Add(new VariableDefinition(module.ImportReference(variable.VariableType)));
        var copies = scanSource.Body.Instructions.ToDictionary(i => i, i => Instruction.Create(OpCodes.Nop));
        foreach (var original in scanSource.Body.Instructions)
        {
            var i = copies[original]; i.OpCode = original.OpCode;
            i.Operand = original.Operand switch
            {
                Instruction t => copies[t],
                VariableDefinition v => scan.Body.Variables[v.Index],
                MethodReference m when m.DeclaringType.Name == nameof(StoppedRootAlgorithm) && m.Name == "Root" => roots,
                MethodReference m when m.DeclaringType.Name == nameof(StoppedRootAlgorithm) && m.Name == "Mark" => mark,
                MethodReference m when m.DeclaringType.Name == nameof(StoppedRootAlgorithm) && m.Name == "Fail" => fatal,
                MethodReference => throw new InvalidOperationException("Unexpected stopped root dependency"),
                TypeReference t => module.ImportReference(t),
                _ => original.Operand
            };
            scan.Body.Instructions.Add(i);
        }
        gc.Methods.Add(scan);
        var stackRoots = gc.Methods.Single(m => m.Name == "ScanStackRoots");
        foreach (var ret in stackRoots.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        { ret.OpCode = OpCodes.Call; ret.Operand = scan; stackRoots.Body.GetILProcessor().InsertAfter(ret, Instruction.Create(OpCodes.Ret)); }
        // Running APs are scanned from native epoch snapshots, never by reading
        // this CPU's RSP as though it belonged to a different Running thread.
        var thread = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerThread");
        var stackScan = gc.Methods.Single(m => m.Name == "ScanThreadStack");
        var ss = stackScan.Body.GetILProcessor(); var originalStart = stackScan.Body.Instructions.First();
        var afterNull = ss.Create(OpCodes.Ldarg_0); var skip = ss.Create(OpCodes.Ret);
        foreach (var i in new[]{ss.Create(OpCodes.Ldarg_0),ss.Create(OpCodes.Brtrue,afterNull),skip,afterNull,
            ss.Create(OpCodes.Callvirt,thread.Methods.Single(m=>m.Name=="get_CpuId")),ss.Create(OpCodes.Ldc_I4_0),
            ss.Create(OpCodes.Bne_Un,skip)}) ss.InsertBefore(originalStart, i);
        foreach (var method in wrappers.Concat(new[] { collect, body, scan, stackRoots, stackScan }))
            foreach (var instruction in method.Body.Instructions)
            {
                instruction.OpCode = instruction.OpCode.Code switch
                {
                    Code.Br_S => OpCodes.Br,
                    Code.Brfalse_S => OpCodes.Brfalse,
                    Code.Brtrue_S => OpCodes.Brtrue,
                    Code.Beq_S => OpCodes.Beq,
                    Code.Bne_Un_S => OpCodes.Bne_Un,
                    Code.Bge_S => OpCodes.Bge,
                    Code.Bge_Un_S => OpCodes.Bge_Un,
                    Code.Bgt_S => OpCodes.Bgt,
                    Code.Bgt_Un_S => OpCodes.Bgt_Un,
                    Code.Ble_S => OpCodes.Ble,
                    Code.Ble_Un_S => OpCodes.Ble_Un,
                    Code.Blt_S => OpCodes.Blt,
                    Code.Blt_Un_S => OpCodes.Blt_Un,
                    Code.Leave_S => OpCodes.Leave,
                    _ => instruction.OpCode
                };
            }
        Console.WriteLine("Adapted C3 publication gate and fail-closed BSP collection epochs with stopped AP roots");
        return true;
    }
}
