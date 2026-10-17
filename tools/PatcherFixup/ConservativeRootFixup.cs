using Mono.Cecil;
using Mono.Cecil.Cil;

// Only this small, allocation-free body is transplanted into the copied kernel.
// Its two intrinsics are rebound to the pinned collector's actual methods.
internal static unsafe class ConservativeRootAlgorithm
{
    internal static nint Find(nint value, nint segments, nint freeTable)
    {
        for (nint segment = segments; segment != 0; segment = *(nint*)segment)
        {
            nint start = *(nint*)(segment + 8);
            nint end = *(nint*)(segment + 16);
            nint bump = *(nint*)(segment + 24);
            if ((nuint)value < (nuint)start || (nuint)value >= (nuint)bump) { continue; }
            for (nint cursor = start; (nuint)cursor < (nuint)bump;)
            {
                nint table = *(nint*)cursor & ~1;
                bool isObject = false;
                uint size;
                if (table == freeTable) { size = *(uint*)(cursor + 8); }
                else if (table == 0 || (ulong)table < 0xffff800000000000UL || IsInHeap(table)) { size = 8; }
                else { size = (ComputeSize(cursor) + 7) & ~7u; isObject = true; }
                // Match the sweep's strides; never interpret an interior word,
                // a stale brick-table hint, or unused TLAB space as a header.
                if (size == 0 || (nuint)size > (nuint)(end - cursor)) { return 0; }
                if ((nuint)value < (nuint)cursor + size) { return isObject ? cursor : 0; }
                cursor += (nint)size;
            }
            return 0;
        }
        return 0;
    }

    private static bool IsInHeap(nint value) => throw new InvalidOperationException("Collector intrinsic");
    private static uint ComputeSize(nint value) => throw new InvalidOperationException("Collector intrinsic");
}

internal static class ConservativeRootFixup
{
    internal static bool Apply(ModuleDefinition module)
    {
        var collector = module.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector");
        const string safeName = "_zenithMarkConservativeRoot";
        if (collector.Methods.Any(m => m.Name == safeName)) { return false; }
        var mark = collector.Methods.Single(m => m.Name == "TryMarkRoot");
        var contains = collector.Methods.Single(m => m.Name == "IsInGCHeap");
        var segment = module.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GCSegment");
        var obj = module.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GCObject");
        var size = obj.Methods.Single(m => m.Name == "ComputeSize");
        var manager = module.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GCSegmentManager");
        var getSegments = manager.Methods.Single(m => m.Name == "get_Segments");
        var free = collector.Fields.Single(f => f.Name == "s_freeMethodTable");
        var heaps = new[] { "s_segmentManager", "s_pinnedSegmentManager" }.Select(n => collector.Fields.Single(f => f.Name == n)).ToArray();
        var scanners = new[] { "ScanThreadStack", "ScanMemoryRange" }.Select(n => collector.Methods.Single(m => m.Name == n)).ToArray();
        var calls = scanners.Select(m => m.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.FullName == mark.FullName).ToArray()).ToArray();
        var freeBlock = collector.NestedTypes.Single(t => t.Name == "FreeBlock");
        var context = module.GetType("Cosmos.Kernel.Core.Scheduler.ThreadContext");
        int registerRoots = context.Fields.Any(f => f.Name == "Rax") ? 15
            : context.Fields.Any(f => f.Name == "X0") ? 33 : -1;
        if (!segment.IsSequentialLayout || segment.PackingSize > 8
            || !segment.Fields.Where(f => !f.IsStatic).Take(4).Select(f => f.Name).SequenceEqual(new[] { "Next", "Start", "End", "Bump" })
            || segment.Fields.Where(f => !f.IsStatic).Take(4).Any(f => f.FieldType is not PointerType)
            || !freeBlock.IsSequentialLayout || freeBlock.Fields.First(f => f.Name == "Size").FieldType.FullName != "System.Int32"
            || !freeBlock.Fields.Where(f => !f.IsStatic).Take(2).Select(f => f.Name).SequenceEqual(new[] { "MethodTable", "Size" })
            || mark.FullName != "System.Void Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector::TryMarkRoot(System.IntPtr)"
            || contains.ReturnType.FullName != "System.Boolean" || contains.Parameters.Count != 1
            || size.ReturnType.FullName != "System.UInt32" || size.IsStatic || size.Parameters.Count != 0
            || calls[0].Length != registerRoots || calls[1].Length != 1)
        { throw new InvalidOperationException("Cosmos conservative GC layout/scanner contract changed"); }

        using var source = AssemblyDefinition.ReadAssembly(typeof(ConservativeRootAlgorithm).Assembly.Location);
        var algorithm = source.MainModule.GetType(nameof(ConservativeRootAlgorithm)).Methods.Single(m => m.Name == "Find");
        var find = new MethodDefinition("_zenithFindConservativeRoot", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.IntPtr);
        for (int i = 0; i < 3; i++) { find.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr)); }
        find.Body.InitLocals = algorithm.Body.InitLocals;
        foreach (var variable in algorithm.Body.Variables) { find.Body.Variables.Add(new VariableDefinition(module.ImportReference(variable.VariableType))); }
        var copied = algorithm.Body.Instructions.ToDictionary(i => i, i => Instruction.Create(OpCodes.Nop));
        foreach (var original in algorithm.Body.Instructions)
        {
            var instruction = copied[original]; instruction.OpCode = original.OpCode;
            instruction.Operand = original.Operand switch
            {
                Instruction target => copied[target],
                Instruction[] targets => targets.Select(t => copied[t]).ToArray(),
                VariableDefinition variable => find.Body.Variables[variable.Index],
                ParameterDefinition parameter => find.Parameters[parameter.Index],
                MethodReference method when method.DeclaringType.Name == nameof(ConservativeRootAlgorithm) && method.Name == "IsInHeap" => contains,
                MethodReference method when method.DeclaringType.Name == nameof(ConservativeRootAlgorithm) && method.Name == "ComputeSize" => size,
                MethodReference => throw new InvalidOperationException("Unexpected collector algorithm dependency"),
                TypeReference type => module.ImportReference(type),
                _ => original.Operand
            };
            find.Body.Instructions.Add(instruction);
        }
        collector.Methods.Add(find);
        var safe = new MethodDefinition(safeName, MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Void);
        safe.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));
        safe.Body.Variables.Add(new VariableDefinition(module.TypeSystem.IntPtr)); safe.Body.InitLocals = true;
        var il = safe.Body.GetILProcessor(); var done = il.Create(OpCodes.Ret); var markRoot = il.Create(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, contains); il.Emit(OpCodes.Brfalse, done);
        foreach (var heap in heaps)
        {
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldsflda, heap); il.Emit(OpCodes.Call, getSegments);
            il.Emit(OpCodes.Conv_I); il.Emit(OpCodes.Ldsfld, free); il.Emit(OpCodes.Conv_I); il.Emit(OpCodes.Call, find);
            il.Emit(OpCodes.Stloc_0); il.Emit(OpCodes.Ldloc_0); il.Emit(OpCodes.Brtrue, markRoot);
        }
        il.Emit(OpCodes.Br, done); il.Append(markRoot); il.Emit(OpCodes.Call, mark); il.Append(done);
        collector.Methods.Add(safe);
        foreach (var call in calls.SelectMany(c => c)) { call.Operand = safe; }
        Console.WriteLine("Adapted conservative GC roots: mark owning objects without modifying interior fields");
        return true;
    }
}
