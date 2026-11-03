using Mono.Cecil;
using Mono.Cecil.Cil;

// Exception dispatch is per thread; its shared name cache must not expose an
// occupied key before the resolver constructor finishes, or race another CPU.
internal static class RuntimeMetadataFixup
{
    internal static bool Apply(ModuleDefinition module)
    {
        var gc = module.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector");
        if (!gc.Methods.Any(m => m.Name == "_zenith_gc_enter")) return false;
        var type = module.GetType("Cosmos.Kernel.Core.Runtime.StackTraceMetadata");
        if (type.Methods.Any(m => m.Name == "_zenithMetadataNames")) return false;
        var cache = type.NestedTypes.Single(t => t.Name == "PerModuleMethodNameResolverCache");
        var lookup = cache.Methods.Single(m => m.Name == "GetOrCreate");
        var code = lookup.Body.Instructions;
        var keyStore = code.Single(i => i.OpCode == OpCodes.Stelem_I);
        var valueStore = code.Single(i => i.OpCode == OpCodes.Stelem_Ref);
        var first = keyStore.Previous.Previous.Previous.Previous;
        var block = code.Skip(code.IndexOf(first)).Take(5).ToArray();
        if (first.OpCode != OpCodes.Ldarg_0 || first.Next.Operand is not FieldReference field || field.Name != "_keys"
            || block[^1] != keyStore || code.IndexOf(keyStore) >= code.IndexOf(valueStore)
            || !code.Skip(code.IndexOf(keyStore)).Take(code.IndexOf(valueStore) - code.IndexOf(keyStore))
                .Any(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference ctor
                    && ctor.DeclaringType.Name == "PerModuleMethodNameResolver")
            || lookup.Body.HasExceptionHandlers)
            throw new InvalidOperationException("Cosmos metadata cache publication contract changed");
        var bodyStart = keyStore.Next;
        foreach (var i in code)
        {
            if (i.Operand is Instruction target && block.Contains(target))
            {
                if (target != first) throw new InvalidOperationException("Branch into metadata publication");
                i.Operand = bodyStart;
            }
        }
        var patch = lookup.Body.GetILProcessor();
        foreach (var i in block) patch.Remove(i);
        var after = valueStore;
        foreach (var i in block) { patch.InsertAfter(after, i); after = i; }

        var method = type.Methods.Single(m => m.Name == "GetMethodNameFromStartAddressIfAvailable" && m.HasBody && m.Parameters.Count == 2);
        if (!method.IsStatic || method.Parameters.Count != 2 || method.ReturnType.MetadataType != MetadataType.String)
            throw new InvalidOperationException("Cosmos metadata resolver entry changed");
        var core = new MethodDefinition("_zenithMetadataNames", MethodAttributes.Private | MethodAttributes.Static, method.ReturnType);
        foreach (var p in method.Parameters) core.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
        var old = method.Body; core.Body = new MethodBody(core) { InitLocals = old.InitLocals, MaxStackSize = old.MaxStackSize };
        foreach (var v in old.Variables) core.Body.Variables.Add(v);
        foreach (var i in old.Instructions)
        { if (i.Operand is ParameterDefinition p) i.Operand = core.Parameters[p.Index]; core.Body.Instructions.Add(i); }
        foreach (var handler in old.ExceptionHandlers) core.Body.ExceptionHandlers.Add(handler);
        type.Methods.Add(core);
        var cpu = module.GetType("Cosmos.Kernel.Core.CPU.InternalCpu");
        var scope = cpu.NestedTypes.Single(t => t.Name == "InterruptScope");
        var mask = cpu.Methods.Single(m => m.Name == "DisableInterruptsScope");
        var dispose = scope.Methods.Single(m => m.Name == "Dispose");
        var enter = gc.Methods.Single(m => m.Name == "_zenith_gc_enter");
        var exit = gc.Methods.Single(m => m.Name == "_zenith_gc_exit");
        var fatal = gc.Methods.Single(m => m.Name == "_zenith_gc_fail_stop");
        method.ImplAttributes = (method.ImplAttributes & ~MethodImplAttributes.AggressiveInlining) | MethodImplAttributes.NoInlining;
        method.Body = new MethodBody(method) { InitLocals = true };
        var irq = new VariableDefinition(scope); var result = new VariableDefinition(module.TypeSystem.String);
        method.Body.Variables.Add(irq); method.Body.Variables.Add(result);
        var il = method.Body.GetILProcessor(); il.Emit(OpCodes.Call, mask); il.Emit(OpCodes.Stloc, irq);
        il.Emit(OpCodes.Call, enter); var begin = il.Create(OpCodes.Nop); il.Emit(OpCodes.Brtrue, begin); il.Emit(OpCodes.Call, fatal); il.Append(begin);
        foreach (var p in method.Parameters) il.Emit(OpCodes.Ldarg, p);
        il.Emit(OpCodes.Call, core); il.Emit(OpCodes.Stloc, result);
        void Finish()
        {
            il.Emit(OpCodes.Call, exit); var ok = il.Create(OpCodes.Ldloca, irq); il.Emit(OpCodes.Brtrue, ok); il.Emit(OpCodes.Call, fatal);
            il.Append(ok); il.Emit(OpCodes.Call, dispose);
        }
        Finish(); var done = il.Create(OpCodes.Ldloc, result); il.Emit(OpCodes.Leave, done);
        var failed = il.Create(OpCodes.Pop); il.Append(failed); Finish(); il.Emit(OpCodes.Rethrow); il.Append(done); il.Emit(OpCodes.Ret);
        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) {
            CatchType = module.ImportReference(typeof(Exception)), TryStart = begin, TryEnd = failed, HandlerStart = failed, HandlerEnd = done });
        Console.WriteLine("Serialized shared exception metadata and publish resolver keys only after complete construction");
        return true;
    }
}
