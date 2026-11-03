using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class ExceptionStateFixup
{
    internal static bool Apply(ModuleDefinition module)
    {
        var manager = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerManager");
        if (manager.Methods.Any(m => m.Name == "_zenithExceptionHead")) return false;
        if (!module.GetType("Cosmos.Kernel.Core.Scheduler.ThreadContext").Fields.Any(f => f.Name == "Xmm")) return false;
        var thread = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerThread");
        var head = new FieldDefinition("_zenithExceptionHead", FieldAttributes.Assembly, module.TypeSystem.IntPtr); thread.Fields.Add(head);
        var boot = new FieldDefinition("_zenithBootExceptionHead", FieldAttributes.Private | FieldAttributes.Static, module.TypeSystem.IntPtr); manager.Fields.Add(boot);
        CustomAttribute Export(string name)
        {
            var export = module.GetType("Cosmos.Kernel.Core.Runtime.Memory").Methods.Single(m => m.Name == "RhpNewFast")
                .CustomAttributes.Single(a => a.AttributeType.Name == "RuntimeExportAttribute");
            var attribute = new CustomAttribute(export.Constructor); attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, name)); return attribute;
        }
        var getter = new MethodDefinition("_zenithExceptionHead", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.UIntPtr);
        getter.CustomAttributes.Add(Export("zenith_exception_head")); getter.Body.InitLocals = true;
        var array = new VariableDefinition(new ArrayType(module.GetType("Cosmos.Kernel.Core.Scheduler.PerCpuState")));
        var id = new VariableDefinition(module.TypeSystem.UInt32); var current = new VariableDefinition(thread);
        foreach (var v in new[] { array, id, current }) getter.Body.Variables.Add(v);
        var il = getter.Body.GetILProcessor(); var fallback = il.Create(OpCodes.Ldsflda, boot);
        il.Emit(OpCodes.Ldsfld, manager.Fields.Single(f => f.Name == "s_cpuStates")); il.Emit(OpCodes.Stloc, array);
        il.Emit(OpCodes.Ldloc, array); il.Emit(OpCodes.Brfalse, fallback);
        il.Emit(OpCodes.Call, manager.Methods.Single(m => m.Name == "GetCurrentCpuId")); il.Emit(OpCodes.Stloc, id);
        il.Emit(OpCodes.Ldloc, id); il.Emit(OpCodes.Ldloc, array); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_U4); il.Emit(OpCodes.Bge_Un, fallback);
        il.Emit(OpCodes.Ldloc, array); il.Emit(OpCodes.Ldloc, id); il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Callvirt, module.GetType("Cosmos.Kernel.Core.Scheduler.PerCpuState").Methods.Single(m => m.Name == "get_CurrentThread")); il.Emit(OpCodes.Stloc, current);
        il.Emit(OpCodes.Ldloc, current); il.Emit(OpCodes.Brfalse, fallback); il.Emit(OpCodes.Ldloc, current); il.Emit(OpCodes.Ldflda, head); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ret);
        il.Append(fallback); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ret); manager.Methods.Add(getter);

        var eh = module.GetType("Internal.Runtime.CompilerHelpers.ExceptionHelper");
        if (eh is null) eh = module.Types.Single(t => t.Name == "ExceptionHelper" && t.Methods.Any(m => m.Name == "ThrowExceptionWithContext"));
        string[] names = { "s_isHandlingException", "s_activeCatchFramePointers", "s_activeCatchHandlers", "s_activeCatchCount", "s_activeCatchException" };
        var tlsCtor = module.ImportReference(typeof(ThreadStaticAttribute).GetConstructor(Type.EmptyTypes)!);
        foreach (string name in names)
        {
            var field = eh.Fields.Single(f => f.Name == name);
            if (!field.IsStatic || field.CustomAttributes.Any(a => a.AttributeType.Name == "ThreadStaticAttribute")) throw new InvalidOperationException("Cosmos exception state changed");
            field.IsInitOnly = false; field.CustomAttributes.Add(new CustomAttribute(tlsCtor));
        }
        var ensure = new MethodDefinition("_zenithPrepareExceptionState", MethodAttributes.Assembly | MethodAttributes.Static, module.TypeSystem.Void);
        ensure.CustomAttributes.Add(Export("zenith_exception_prepare")); var code = ensure.Body.GetILProcessor();
        foreach (string name in new[] { "s_activeCatchFramePointers", "s_activeCatchHandlers" })
        {
            var field = eh.Fields.Single(f => f.Name == name);
            if (field.FieldType is not ArrayType a || a.ElementType.MetadataType != MetadataType.UIntPtr) throw new InvalidOperationException("Cosmos catch frame array changed");
            var next = code.Create(OpCodes.Nop); code.Emit(OpCodes.Ldsfld, field); code.Emit(OpCodes.Brtrue, next);
            code.Emit(OpCodes.Ldc_I4_8); code.Emit(OpCodes.Newarr, a.ElementType); code.Emit(OpCodes.Stsfld, field); code.Append(next);
        }
        code.Emit(OpCodes.Ret); eh.Methods.Add(ensure);
        // Allocate dispatcher scratch before ordinary BSP thread user code,
        // not for the first time while unwinding its allocation failure.
        var start = manager.Methods.Single(m => m.Name == "InvokeCurrentThreadStart");
        if (start.Parameters.Count != 1 || !start.IsStatic || !start.HasBody)
            throw new InvalidOperationException("Cosmos managed thread entry contract changed");
        start.Body.GetILProcessor().InsertBefore(start.Body.Instructions.First(), Instruction.Create(OpCodes.Call, ensure));
        var idle = manager.Methods.Single(m => m.Name == "SetupIdleThread");
        var returns = idle.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
        if (returns.Length != 1) throw new InvalidOperationException("Cosmos idle owner setup contract changed");
        returns[0].OpCode = OpCodes.Call; returns[0].Operand = ensure;
        idle.Body.GetILProcessor().InsertAfter(returns[0], Instruction.Create(OpCodes.Ret));
        var dispatch = eh.Methods.Single(m => m.Name == "ThrowExceptionWithContext");
        dispatch.Body.GetILProcessor().InsertBefore(dispatch.Body.Instructions.First(), Instruction.Create(OpCodes.Call, ensure));
        // Caught AP exceptions must not use UART or mutate BSP panic/UI state.
        // Only fixed, prewarmed dispatch is admitted; an unhandled/recursive AP
        // failure traps onto its native private fault IST and stays quarantined.
        var currentCpu = manager.Methods.Single(m => m.Name == "GetCurrentCpuId");
        var fatal = new MethodDefinition("_zenithApExceptionFailStop", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.PInvokeImpl, module.TypeSystem.Void);
        fatal.PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl, "zenith_cpu_ap_fail_stop", module.ModuleReferences.First(r => r.Name == "*"));
        fatal.ImplAttributes = MethodImplAttributes.PreserveSig;
        fatal.CustomAttributes.Add(new CustomAttribute(module.ImportReference(typeof(System.Runtime.InteropServices.SuppressGCTransitionAttribute).GetConstructor(Type.EmptyTypes)!)));
        eh.Methods.Add(fatal);
        var failFast = eh.Methods.Single(m => m.Name == "FailFast");
        var f = failFast.Body.GetILProcessor(); var original = failFast.Body.Instructions.First();
        foreach (var i in new[] { f.Create(OpCodes.Call, currentCpu), f.Create(OpCodes.Brfalse, original), f.Create(OpCodes.Call, fatal), f.Create(OpCodes.Ret) }) f.InsertBefore(original, i);
        Console.WriteLine("Adapted exception chains and dispatcher scratch to registered thread owners, with lazy/prewarmed TLS catch arrays");
        return true;
    }
}
