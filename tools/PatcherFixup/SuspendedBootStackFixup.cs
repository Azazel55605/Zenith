using Mono.Cecil;
using Mono.Cecil.Cil;

// The UI runs on the boot/idle thread, which has no allocated StackBase/Size.
// The pinned collector skips that stack when another BSP thread calls Collect.
// Keep its existing register scan and add the real suspended boot-stack range.
internal static class SuspendedBootStackFixup
{
    internal static bool Apply(ModuleDefinition module)
    {
        // The fail-stop/epoch contract below is the x64 C3 adapter. Leave the
        // separate ARM64 runtime unchanged rather than importing its intrinsics.
        if (!module.GetType("Cosmos.Kernel.Core.Scheduler.ThreadContext").Fields.Any(f => f.Name == "Xmm")) { return false; }
        var gc = module.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector");
        const string name = "_zenithScanSuspendedBootStack";
        if (gc.Methods.Any(m => m.Name == name))
        {
            var existing = gc.Methods.Single(m => m.Name == "ScanThreadStack");
            if (existing.Body.Instructions.Count(i => i.Operand is MethodReference m && m.Name == name) != 1)
            { throw new InvalidOperationException("Incomplete suspended boot-stack root adaptation"); }
            return false;
        }
        var thread = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerThread");
        var boot = module.GetType("Cosmos.Kernel.Core.Runtime.BootStack");
        var scan = gc.Methods.Single(m => m.Name == "ScanThreadStack");
        var range = gc.Methods.Single(m => m.Name == "ScanMemoryRange");
        var fatal = gc.Methods.Single(m => m.Name == "_zenith_gc_fail_stop");
        MethodDefinition Get(TypeDefinition t, string n) => t.Methods.Single(m => m.Name == "get_" + n);
        var top = Get(boot,"Top"); var size = Get(boot,"Size");
        var pointer = Get(thread,"StackPointer");
        var state = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerThreadState");
        int running = Convert.ToInt32(state.Fields.Single(f => f.Name == "Running").Constant);
        if (!scan.IsStatic || !scan.HasBody || scan.Parameters.Count != 1 || scan.Parameters[0].ParameterType.FullName != thread.FullName
            || range.Parameters.Count != 2 || range.ReturnType.MetadataType != MetadataType.Void
            || top.ReturnType.MetadataType != MetadataType.UIntPtr || size.ReturnType.MetadataType != MetadataType.UIntPtr
            || pointer.ReturnType.MetadataType != MetadataType.UIntPtr
            || !scan.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "get_StackBase")
            || !scan.Body.Instructions.Any(i => i.Operand is MethodReference m && m.FullName == range.FullName))
        { throw new InvalidOperationException("Cosmos suspended boot-stack collector contract changed"); }
        var helper = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Void);
        helper.Parameters.Add(new ParameterDefinition(thread));
        helper.Body.InitLocals = true;
        for (int i = 0; i < 3; i++) { helper.Body.Variables.Add(new VariableDefinition(module.TypeSystem.UIntPtr)); }
        var il = helper.Body.GetILProcessor(); var done = il.Create(OpCodes.Ret); var fail = il.Create(OpCodes.Call, fatal);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Brfalse,done);
        foreach (string n in new[] { "CpuId", "Id", "StackBase", "StackSize" })
        { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt,Get(thread,n)); il.Emit(OpCodes.Brtrue,done); }
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt,Get(thread,"State")); il.Emit(OpCodes.Ldc_I4,running); il.Emit(OpCodes.Beq,done);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt,pointer); il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Call,top); il.Emit(OpCodes.Stloc_1);
        il.Emit(OpCodes.Call,size); il.Emit(OpCodes.Stloc_2);
        // Refuse malformed/missing saved bounds; never sweep with missing roots.
        il.Emit(OpCodes.Ldloc_2); il.Emit(OpCodes.Brfalse,fail);
        il.Emit(OpCodes.Ldloc_1); il.Emit(OpCodes.Ldloc_2); il.Emit(OpCodes.Blt_Un,fail);
        il.Emit(OpCodes.Ldloc_0); il.Emit(OpCodes.Ldloc_1); il.Emit(OpCodes.Ldloc_2); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Blt_Un,fail);
        il.Emit(OpCodes.Ldloc_0); il.Emit(OpCodes.Ldloc_1); il.Emit(OpCodes.Bgt_Un,fail);
        il.Emit(OpCodes.Ldloc_0); il.Emit(OpCodes.Ldc_I4_7); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.And); il.Emit(OpCodes.Brtrue,fail);
        il.Emit(OpCodes.Ldloc_0); il.Emit(OpCodes.Ldloc_1); il.Emit(OpCodes.Call,range); il.Emit(OpCodes.Br,done);
        il.Append(fail); il.Append(done); gc.Methods.Add(helper);
        var entry = scan.Body.GetILProcessor(); var first = scan.Body.Instructions[0];
        entry.InsertBefore(first,entry.Create(OpCodes.Ldarg_0)); entry.InsertBefore(first,entry.Create(OpCodes.Call,helper));
        Console.WriteLine("Adapted suspended BSP boot-stack roots: preserve UI locals during worker collection");
        return true;
    }
}
