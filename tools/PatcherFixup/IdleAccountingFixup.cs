using Mono.Cecil;
using Mono.Cecil.Cil;

// Observatory needs real CPU usage. Every managed halt goes through the runtime's
// _native_cpu_halt import; route it to zenith_cpu_halt_counted, which records the
// halted TSC span. The managed interrupt dispatch closes an open span first, so
// a timer switch to another thread never counts that thread's run time as idle.
internal static class IdleAccountingFixup
{
    private static ModuleReference Library(ModuleDefinition module)
    {
        var library = module.ModuleReferences.FirstOrDefault(r => r.Name == "*");
        if (library is null) { library = new ModuleReference("*"); module.ModuleReferences.Add(library); }
        return library;
    }

    internal static bool Core(ModuleDefinition module)
    {
        // x64 only: the ARM64 runtime keeps its own halt.
        if (!module.GetType("Cosmos.Kernel.Core.Scheduler.ThreadContext").Fields.Any(f => f.Name == "Xmm")) { return false; }
        var imports = module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasPInvokeInfo).ToArray();
        var halts = imports.Where(m => m.PInvokeInfo.EntryPoint == "_native_cpu_halt").ToArray();
        if (halts.Length == 0)
        {
            if (!imports.Any(m => m.PInvokeInfo.EntryPoint == "zenith_cpu_halt_counted"))
            { throw new InvalidOperationException("Cosmos halt import contract changed"); }
            return false;
        }
        foreach (var halt in halts)
        {
            if (halt.Parameters.Count != 0 || halt.ReturnType.MetadataType != MetadataType.Void)
            { throw new InvalidOperationException("Cosmos halt import signature changed"); }
            halt.PInvokeInfo.EntryPoint = "zenith_cpu_halt_counted";
        }
        Console.WriteLine("Adapted managed halt: count halted time per CPU for Observatory");
        return true;
    }

    internal static bool X64(ModuleDefinition module)
    {
        var controller = module.GetType("Cosmos.Kernel.Core.X64.Cpu.X64InterruptController");
        const string marker = "_zenithIrqIdleAccount";
        if (controller.Methods.Any(m => m.Name == marker))
        {
            var dispatched = controller.Methods.Single(m => m.Name == "Dispatch");
            if (!(dispatched.Body.Instructions[0].Operand is MethodReference first && first.Name == marker))
            { throw new InvalidOperationException("Incomplete interrupt idle accounting adaptation"); }
            return false;
        }
        var dispatch = controller.Methods.Single(m => m.Name == "Dispatch");
        if (dispatch.IsStatic || dispatch.Parameters.Count != 1 || !dispatch.HasBody)
        { throw new InvalidOperationException("Cosmos interrupt dispatch contract changed"); }
        var native = new MethodDefinition(marker, MethodAttributes.Private | MethodAttributes.Static
            | MethodAttributes.PInvokeImpl, module.TypeSystem.Void);
        native.PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl, "zenith_cpu_irq_account", Library(module));
        native.ImplAttributes = MethodImplAttributes.PreserveSig;
        native.CustomAttributes.Add(new CustomAttribute(module.ImportReference(
            typeof(System.Runtime.InteropServices.SuppressGCTransitionAttribute).GetConstructor(Type.EmptyTypes)!)));
        controller.Methods.Add(native);
        var il = dispatch.Body.GetILProcessor();
        il.InsertBefore(dispatch.Body.Instructions[0], il.Create(OpCodes.Call, native));
        Console.WriteLine("Adapted interrupt dispatch: close a halted span before any handler or switch");
        return true;
    }
}
