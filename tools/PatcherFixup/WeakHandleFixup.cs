using Mono.Cecil;
using Mono.Cecil.Cil;

// Clearing a dead weak target must not recycle a slot still owned by a live
// WeakReference/DependentHandle. Its eventual explicit Free releases that slot.
internal static class WeakHandleFixup
{
    internal static bool Apply(ModuleDefinition module)
    {
        var gc = module.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector");
        if (!gc.Methods.Any(m => m.Name == "_zenith_gc_enter")) return false;
        const string name = "_zenithLiveHandleTarget";
        if (gc.Methods.Any(m => m.Name == name)) return false;
        var manager = module.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GCHandleManager");
        var cleanup = manager.Methods.Single(m => m.Name == "FreeWeakHandles");
        var scan = gc.Methods.Single(m => m.Name == "ScanGCHandles");
        var handle = module.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GCHandle");
        var target = handle.Fields.Single(f => f.Name == "Object");
        var extra = handle.Fields.Single(f => f.Name == "ExtraInfo");
        var marked = module.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GCObject").Methods.Single(m => m.Name == "get_IsMarked");
        var frees = cleanup.Body.Instructions.Where(i => i.Operand is MethodReference m && m.Name == "FreeHandle").ToArray();
        if (frees.Length != 2 || frees.Any(i => ((MethodReference)i.Operand).DeclaringType.Name != "GCHandleSegmentStore")
            || new[] { cleanup, scan }.Any(m => m.Body.Instructions.Count(i => i.Operand is MethodReference call && call.FullName == marked.FullName) != 2))
            throw new InvalidOperationException("Cosmos weak/dependent handle sweep changed");
        var live = new MethodDefinition(name, MethodAttributes.Assembly | MethodAttributes.Static, module.TypeSystem.Boolean);
        live.Parameters.Add(new ParameterDefinition(target.FieldType));
        var il = live.Body.GetILProcessor();
        var no = il.Create(OpCodes.Ldc_I4_0); var yes = il.Create(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Brfalse, no);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Call, gc.Methods.Single(m => m.Name == "IsInFrozenSegment")); il.Emit(OpCodes.Brtrue, yes);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, marked); il.Emit(OpCodes.Ret);
        il.Append(no); il.Emit(OpCodes.Ret); il.Append(yes); il.Emit(OpCodes.Ret); gc.Methods.Add(live);
        var clear = new MethodDefinition("_zenithClearWeakTarget", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Void);
        var oldFree = (MethodReference)frees[0].Operand;
        clear.Parameters.Add(new ParameterDefinition(new ByReferenceType(oldFree.DeclaringType)));
        clear.Parameters.Add(new ParameterDefinition(oldFree.Parameters[0].ParameterType));
        il = clear.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Stfld, target);
        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Conv_I); il.Emit(OpCodes.Stfld, extra); il.Emit(OpCodes.Ret);
        manager.Methods.Add(clear);
        foreach (var i in frees) i.Operand = clear;
        foreach (var method in new[] { cleanup, scan })
            foreach (var i in method.Body.Instructions.Where(i => i.Operand is MethodReference call && call.FullName == marked.FullName)) i.Operand = live;
        Console.WriteLine("Preserved weak/dependent handle ownership and immutable frozen-key reachability");
        return true;
    }
}
