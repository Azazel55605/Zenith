using Mono.Cecil;
using Mono.Cecil.Cil;

// GC refill, native stack/handle allocation and BSP drivers ultimately share the
// RAT/unmanaged heap. Protect those entry points with the same recursive gate;
// protecting GC allocation alone would still race BSP unmanaged allocations.
internal static class RuntimeHeapFixup
{
    internal static bool Apply(ModuleDefinition module)
    {
        var gc = module.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector");
        var enter = gc.Methods.SingleOrDefault(m => m.Name == "_zenith_gc_enter");
        if (enter is null) return false;
        var heap = module.GetType("Cosmos.Kernel.Core.Memory.Heap.Heap");
        if (heap.Methods.Any(m => m.Name.StartsWith("_zenithHeap_"))) return false;
        var pages = module.GetType("Cosmos.Kernel.Core.Memory.PageAllocator");
        var targets = heap.Methods.Where(m => m.Name is "Alloc" or "Free" or "Realloc" or "Collect")
            .Concat(pages.Methods.Where(m => m.Name is "AllocPages" or "Free")).ToArray();
        if (targets.Length != 7 || targets.Any(m => !m.IsStatic || !m.HasBody)) throw new InvalidOperationException("Cosmos heap/page ownership entries changed");
        var exit = gc.Methods.Single(m => m.Name == "_zenith_gc_exit"); var fatal = gc.Methods.Single(m => m.Name == "_zenith_gc_fail_stop");
        var cpu = module.GetType("Cosmos.Kernel.Core.CPU.InternalCpu"); var scope = cpu.NestedTypes.Single(t => t.Name == "InterruptScope");
        var mask = cpu.Methods.Single(m => m.Name == "DisableInterruptsScope"); var dispose = scope.Methods.Single(m => m.Name == "Dispose");
        foreach (var method in targets)
        {
            var core = new MethodDefinition("_zenithHeap_" + method.Name, MethodAttributes.Private | MethodAttributes.Static, method.ReturnType);
            foreach (var p in method.Parameters) core.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
            var old = method.Body; core.Body = new MethodBody(core) { InitLocals = old.InitLocals, MaxStackSize = old.MaxStackSize };
            foreach (var v in old.Variables) core.Body.Variables.Add(v);
            foreach (var i in old.Instructions)
            { if (i.Operand is ParameterDefinition p) i.Operand = core.Parameters[p.Index]; core.Body.Instructions.Add(i); }
            foreach (var handler in old.ExceptionHandlers) core.Body.ExceptionHandlers.Add(handler);
            method.DeclaringType.Methods.Add(core);
            method.ImplAttributes = (method.ImplAttributes & ~MethodImplAttributes.AggressiveInlining) | MethodImplAttributes.NoInlining;
            method.Body = new MethodBody(method) { InitLocals = true };
            var irq = new VariableDefinition(scope); method.Body.Variables.Add(irq);
            VariableDefinition result = null;
            if (method.ReturnType.MetadataType != MetadataType.Void) { result = new VariableDefinition(method.ReturnType); method.Body.Variables.Add(result); }
            var il = method.Body.GetILProcessor(); il.Emit(OpCodes.Call, mask); il.Emit(OpCodes.Stloc, irq);
            il.Emit(OpCodes.Call, enter); var begin = il.Create(OpCodes.Nop); il.Emit(OpCodes.Brtrue, begin); il.Emit(OpCodes.Call, fatal); il.Append(begin);
            foreach (var p in method.Parameters) il.Emit(OpCodes.Ldarg, p); il.Emit(OpCodes.Call, core);
            if (result is not null) il.Emit(OpCodes.Stloc, result);
            void Finish()
            {
                il.Emit(OpCodes.Call, exit); var ok = il.Create(OpCodes.Ldloca, irq); il.Emit(OpCodes.Brtrue, ok); il.Emit(OpCodes.Call, fatal);
                il.Append(ok); il.Emit(OpCodes.Call, dispose);
            }
            Finish(); var done = il.Create(OpCodes.Nop); il.Emit(OpCodes.Leave, done);
            var failed = il.Create(OpCodes.Pop); il.Append(failed); Finish(); il.Emit(OpCodes.Rethrow); il.Append(done);
            if (result is not null) il.Emit(OpCodes.Ldloc, result); il.Emit(OpCodes.Ret);
            method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) { CatchType = module.ImportReference(typeof(Exception)), TryStart = begin, TryEnd = failed, HandlerStart = failed, HandlerEnd = done });
        }
        Console.WriteLine("Serialized all seven unmanaged heap/page entries with GC publication and collection ownership"); return true;
    }
}
