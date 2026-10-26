using Mono.Cecil;
using Mono.Cecil.Cil;

// A GC allocation is not yet an object until its method table and array length
// are initialized. Restoring IRQs in AllocObject alone publishes that gap to the
// scheduler/pressure collector. Keep the outer allocation entry atomic too.
internal static class AllocationPublicationFixup
{
    private const string Prefix = "_zenithPublish_";
    private static readonly string[] Names = {
        "RhAllocateNewArray", "RhpNewArray", "RhpNewPtrArrayFast", "RhpNewArrayFast",
        "RhAllocateNewObject", "RhpNewFast"
    };

    internal static bool Apply(ModuleDefinition module)
    {
        var memory = module.GetType("Cosmos.Kernel.Core.Runtime.Memory")
            ?? throw new InvalidOperationException("Cosmos runtime allocation type changed");
        int adapted = memory.Methods.Count(m => m.Name.StartsWith(Prefix, StringComparison.Ordinal));
        if (adapted != 0)
        {
            if (adapted != Names.Length || Names.Any(n => !memory.Methods.Any(m => m.Name == Prefix + n)))
            { throw new InvalidOperationException("Incomplete allocation publication adaptation"); }
            return false;
        }
        var cpu = module.GetType("Cosmos.Kernel.Core.CPU.InternalCpu");
        var enter = cpu.Methods.Single(m => m.Name == "DisableInterruptsScope");
        var scope = cpu.NestedTypes.Single(t => t.Name == "InterruptScope");
        var dispose = scope.Methods.Single(m => m.Name == "Dispose");
        var targets = Names.Select(n => memory.Methods.Single(m => m.Name == n)).ToArray();
        foreach (var method in targets)
        {
            bool array = method.Name.Contains("Array", StringComparison.Ordinal);
            bool output = method.Name.StartsWith("RhAllocate", StringComparison.Ordinal);
            var instructions = method.Body.Instructions;
            int stores(string name) => instructions.Count(i => i.OpCode == OpCodes.Stfld
                && i.Operand is FieldReference f && f.DeclaringType.FullName == "Cosmos.Kernel.Core.Memory.GarbageCollector.GCObject" && f.Name == name);
            bool pointer(TypeReference type) => type is PointerType p && p.ElementType.FullName == "Internal.Runtime.MethodTable";
            if (!method.IsStatic || !method.HasBody || method.Body.HasExceptionHandlers
                || method.ReturnType.FullName != (output ? "System.Void" : "System.Void*")
                || method.Parameters.Count != (output ? array ? 4 : 3 : array ? 2 : 1)
                || !pointer(method.Parameters[0].ParameterType)
                || (array && method.Parameters[1].ParameterType.FullName != (output ? "System.UInt32" : "System.Int32"))
                || (output && method.Parameters[array ? 2 : 1].ParameterType.Name != "GC_ALLOC_FLAGS")
                || (output && (array
                    ? method.Parameters[^1].ParameterType is not ByReferenceType byref || byref.ElementType.FullName != "System.Void*"
                    : method.Parameters[^1].ParameterType.FullName != "System.Void*"))
                || stores("MethodTable") != 1 || stores("Length") != (array ? 1 : 0)
                || instructions.Count(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference call
                    && call.DeclaringType.FullName == memory.FullName && call.Name == "AllocObject") != 1)
            { throw new InvalidOperationException("Cosmos allocation publication contract changed: " + method.Name); }
        }
        if (!enter.IsStatic || enter.Parameters.Count != 0 || enter.ReturnType.FullName != scope.FullName
            || dispose.IsStatic || dispose.Parameters.Count != 0 || dispose.ReturnType.FullName != "System.Void")
        { throw new InvalidOperationException("Cosmos allocation IRQ scope contract changed"); }

        foreach (var method in targets)
        {
            var core = new MethodDefinition(Prefix + method.Name, MethodAttributes.Private | MethodAttributes.Static, method.ReturnType);
            foreach (var parameter in method.Parameters)
            { core.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType)); }
            core.Body = new MethodBody(core) { InitLocals = method.Body.InitLocals, MaxStackSize = method.Body.MaxStackSize };
            foreach (var variable in method.Body.Variables) { core.Body.Variables.Add(variable); }
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is ParameterDefinition parameter) { instruction.Operand = core.Parameters[parameter.Index]; }
                core.Body.Instructions.Add(instruction);
            }
            memory.Methods.Add(core);
            method.ImplAttributes &= ~MethodImplAttributes.AggressiveInlining;
            method.ImplAttributes |= MethodImplAttributes.NoInlining;
            method.Body = new MethodBody(method) { InitLocals = true, MaxStackSize = 8 };
            var irq = new VariableDefinition(scope); method.Body.Variables.Add(irq);
            VariableDefinition result = null;
            if (method.ReturnType.FullName != "System.Void")
            { result = new VariableDefinition(method.ReturnType); method.Body.Variables.Add(result); }
            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Call, enter); il.Emit(OpCodes.Stloc, irq);
            var begin = il.Create(OpCodes.Nop); il.Append(begin);
            foreach (var parameter in method.Parameters) { il.Emit(OpCodes.Ldarg, parameter); }
            il.Emit(OpCodes.Call, core);
            if (result is not null) { il.Emit(OpCodes.Stloc, result); }
            il.Emit(OpCodes.Ldloca, irq); il.Emit(OpCodes.Call, dispose);
            var done = il.Create(OpCodes.Nop); il.Emit(OpCodes.Leave, done);
            var failed = il.Create(OpCodes.Pop); il.Append(failed);
            // Cosmos does not reliably unwind finally blocks. Restore explicitly
            // on both ordinary returns and caught allocation failures.
            il.Emit(OpCodes.Ldloca, irq); il.Emit(OpCodes.Call, dispose); il.Emit(OpCodes.Rethrow);
            il.Append(done);
            if (result is not null) { il.Emit(OpCodes.Ldloc, result); }
            il.Emit(OpCodes.Ret);
            method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) {
                CatchType = module.ImportReference(typeof(Exception)), TryStart = begin, TryEnd = failed,
                HandlerStart = failed, HandlerEnd = done
            });
        }
        Console.WriteLine("Adapted allocation publication: retain IRQ state through object headers and array lengths");
        return true;
    }
}
