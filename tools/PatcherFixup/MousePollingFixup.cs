using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class MousePollingFixup
{
    internal static bool System(ModuleDefinition module, IAssemblyResolver resolver)
    {
        var manager = module.GetType("Cosmos.Kernel.System.Mouse.MouseManager")
            ?? throw new InvalidOperationException("Cosmos mouse manager changed");
        var poll = manager.Methods.Single(m => m.Name == "Poll");
        const string name = "_zenithMousePollCore";
        if (manager.Methods.Any(m => m.Name == name))
        {
            if (!poll.IsPublic || poll.Body.Instructions.Count(i => i.Operand is MethodReference m && m.Name == name) != 1
                || poll.Body.Instructions.Count(i => i.Operand is MethodReference m && m.Name == "DisableInterruptsScope") != 1
                || poll.Body.Instructions.Count(i => i.Operand is MethodReference m && m.Name == "Dispose") != 2)
            { throw new InvalidOperationException("Incomplete mouse polling adaptation"); }
            return false;
        }
        if (!poll.IsStatic || !poll.HasBody || poll.Parameters.Count != 0 || poll.ReturnType.MetadataType != MetadataType.Void
            || poll.Body.Instructions.Count(i => i.Operand is MethodReference m && m.Name == "Poll" && m.DeclaringType.Name == "IMouseDevice") != 1)
        { throw new InvalidOperationException("Cosmos mouse polling contract changed"); }
        using var core = resolver.Resolve(new AssemblyNameReference("Cosmos.Kernel.Core", new Version(3, 0, 89, 0)));
        var cpu = core.MainModule.GetType("Cosmos.Kernel.Core.CPU.InternalCpu");
        var enter = module.ImportReference(cpu.Methods.Single(m => m.Name == "DisableInterruptsScope"));
        var scope = cpu.NestedTypes.Single(t => t.Name == "InterruptScope");
        var dispose = module.ImportReference(scope.Methods.Single(m => m.Name == "Dispose"));
        var body = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Void);
        body.Body = new MethodBody(body) { InitLocals = poll.Body.InitLocals, MaxStackSize = poll.Body.MaxStackSize };
        foreach (var v in poll.Body.Variables) { body.Body.Variables.Add(v); }
        foreach (var i in poll.Body.Instructions) { body.Body.Instructions.Add(i); }
        foreach (var e in poll.Body.ExceptionHandlers) { body.Body.ExceptionHandlers.Add(e); }
        manager.Methods.Add(body);
        poll.IsAssembly = false; poll.IsPublic = true;
        poll.ImplAttributes &= ~MethodImplAttributes.AggressiveInlining;
        poll.ImplAttributes |= MethodImplAttributes.NoInlining;
        poll.Body = new MethodBody(poll) { InitLocals = true, MaxStackSize = 4 };
        var irq = new VariableDefinition(module.ImportReference(scope)); poll.Body.Variables.Add(irq);
        var il = poll.Body.GetILProcessor();
        il.Emit(OpCodes.Call, enter); il.Emit(OpCodes.Stloc, irq);
        var begin = il.Create(OpCodes.Call, body); il.Append(begin);
        il.Emit(OpCodes.Ldloca, irq); il.Emit(OpCodes.Call, dispose);
        var done = il.Create(OpCodes.Ret); il.Emit(OpCodes.Leave, done);
        var failed = il.Create(OpCodes.Pop); il.Append(failed);
        // Cosmos does not reliably unwind finally; restore on both paths.
        il.Emit(OpCodes.Ldloca, irq); il.Emit(OpCodes.Call, dispose); il.Emit(OpCodes.Rethrow); il.Append(done);
        poll.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) {
            CatchType = module.ImportReference(typeof(Exception)), TryStart = begin, TryEnd = failed,
            HandlerStart = failed, HandlerEnd = done
        });
        Console.WriteLine("Adapted mouse polling: IRQ-scoped periodic drain for lost device interrupts");
        return true;
    }

    internal static bool BindMain(ModuleDefinition module, IAssemblyResolver resolver)
    {
        var type = module.GetType("Zenith.Core.Input.MousePolling");
        if (type is null) { return false; }
        var method = type.Methods.Single(m => m.Name == "Drain");
        if (method.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "Poll" && m.DeclaringType.Name == "MouseManager"))
        {
            if (method.Body.Instructions.Count != 2 || method.Body.Instructions[^1].OpCode != OpCodes.Ret)
            { throw new InvalidOperationException("Incomplete Zenith mouse polling bridge"); }
            return false;
        }
        if (!method.IsStatic || method.Parameters.Count != 0 || method.ReturnType.MetadataType != MetadataType.Void
            || method.Body.Instructions.Count(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "Zenith mouse polling adapter missing") != 1
            || method.Body.Instructions.Count(i => i.OpCode == OpCodes.Throw) != 1)
        { throw new InvalidOperationException("Zenith mouse polling bridge changed"); }
        using var system = resolver.Resolve(new AssemblyNameReference("Cosmos.Kernel.System", new Version(3, 0, 89, 0)));
        var poll = system.MainModule.GetType("Cosmos.Kernel.System.Mouse.MouseManager").Methods.Single(m => m.Name == "Poll" && m.Parameters.Count == 0);
        method.Body = new MethodBody(method);
        var il = method.Body.GetILProcessor(); il.Emit(OpCodes.Call, module.ImportReference(poll)); il.Emit(OpCodes.Ret);
        Console.WriteLine("Bound Zenith periodic mouse drain to copied Cosmos manager");
        return true;
    }

    internal static bool Hal(ModuleDefinition module)
    {
        var mouse = module.GetType("Cosmos.Kernel.HAL.Devices.Input.VirtioMouse")
            ?? throw new InvalidOperationException("Cosmos virtio mouse changed");
        const string name = "_zenithTryMouseUsedBuffer";
        var process = mouse.Methods.Single(m => m.Name == "ProcessEvents");
        if (mouse.Methods.Any(m => m.Name == name))
        {
            if (process.Body.Instructions.Count(i => i.Operand is MethodReference m && m.Name == name) != 1)
            { throw new InvalidOperationException("Incomplete bounded mouse drain"); }
            return false;
        }
        var calls = process.Body.Instructions.Where(i => i.Operand is MethodReference m && m.Name == "GetUsedBuffer").ToArray();
        if (process.IsStatic || process.Parameters.Count != 0 || calls.Length != 1 || calls[0].OpCode != OpCodes.Callvirt)
        { throw new InvalidOperationException("Cosmos mouse event drain changed"); }
        var used = (MethodReference)calls[0].Operand;
        if (used.DeclaringType.Name != "Virtqueue" || used.ReturnType.MetadataType != MetadataType.Boolean
            || used.Parameters.Count != 2 || used.Parameters.Any(p => p.ParameterType.FullName != "System.UInt32&"))
        { throw new InvalidOperationException("Cosmos mouse queue contract changed"); }
        var helper = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Boolean);
        helper.Parameters.Add(new ParameterDefinition(used.DeclaringType));
        foreach (var p in used.Parameters) { helper.Parameters.Add(new ParameterDefinition(p.ParameterType)); }
        helper.Parameters.Add(new ParameterDefinition(new ByReferenceType(module.TypeSystem.Int32)));
        var il = helper.Body.GetILProcessor(); var exhausted = il.Create(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Ldind_I4); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ble, exhausted);
        il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldind_I4); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Stind_I4);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Callvirt, used); il.Emit(OpCodes.Ret);
        il.Append(exhausted); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stind_I4);
        il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stind_I4); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
        mouse.Methods.Add(helper);
        var budget = new VariableDefinition(module.TypeSystem.Int32); process.Body.Variables.Add(budget);
        process.Body.InitLocals = true;
        var code = process.Body.GetILProcessor(); var first = process.Body.Instructions[0];
        code.InsertBefore(first, code.Create(OpCodes.Ldc_I4, 64)); code.InsertBefore(first, code.Create(OpCodes.Stloc, budget));
        calls[0].OpCode = OpCodes.Ldloca; calls[0].Operand = budget;
        code.InsertAfter(calls[0], code.Create(OpCodes.Call, helper));
        process.Body.MaxStackSize = Math.Max(process.Body.MaxStackSize, 4);
        Console.WriteLine("Bounded virtio mouse drain to 64 events per IRQ/poll");
        return true;
    }
}
