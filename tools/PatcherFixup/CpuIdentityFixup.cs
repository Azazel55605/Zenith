using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class CpuIdentityFixup
{
    internal static bool Apply(ModuleDefinition module)
    {
        var scheduler = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerManager");
        const string marker = "_zenithCurrentCpuId";
        if (scheduler.Methods.Any(m => m.Name == marker)) { return false; }
        var current = scheduler.Methods.Single(m => m.Name == "GetCurrentCpuId");
        var code = current.Body.Instructions;
        if (!current.IsStatic || current.Parameters.Count != 0
            || current.ReturnType.MetadataType != MetadataType.UInt32
            || code.Count != 2 || code[0].OpCode != OpCodes.Ldc_I4_0 || code[1].OpCode != OpCodes.Ret)
        { throw new InvalidOperationException("Cosmos current-CPU identity contract changed"); }
        var library = module.ModuleReferences.FirstOrDefault(r => r.Name == "*");
        if (library is null) { library = new ModuleReference("*"); module.ModuleReferences.Add(library); }
        var native = new MethodDefinition(marker, MethodAttributes.Private | MethodAttributes.Static
            | MethodAttributes.PInvokeImpl, module.TypeSystem.UInt32);
        native.PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl, "zenith_cpu_logical_id", library);
        native.ImplAttributes = MethodImplAttributes.PreserveSig;
        native.CustomAttributes.Add(new CustomAttribute(module.ImportReference(
            typeof(System.Runtime.InteropServices.SuppressGCTransitionAttribute).GetConstructor(Type.EmptyTypes)!)));
        scheduler.Methods.Add(native);
        current.Body = new MethodBody(current);
        current.Body.Instructions.Add(Instruction.Create(OpCodes.Call, native));
        current.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        Console.WriteLine("Adapted current CPU identity: hardware BSP and registered native AP identities; managed AP entry remains disabled");
        return true;
    }

    internal static bool X64(ModuleDefinition module)
    {
        var controller = module.GetType("Cosmos.Kernel.Core.X64.Cpu.X64InterruptController");
        const string marker = "_zenithLogicalIrqCpu";
        if (controller.Methods.Any(m => m.Name == marker)) { return false; }
        var methods = new[] { controller.Methods.Single(m => m.Name == "Dispatch"),
            module.GetType("Cosmos.Kernel.Core.X64.Cpu.LocalApic").Methods.Single(m => m.Name == "HandleTimerInterrupt") };
        var calls = methods.Select(m => m.Body.Instructions.Single(i => i.OpCode == OpCodes.Call
            && i.Operand is MethodReference call && call.Name == "GetId"
            && call.DeclaringType.FullName == "Cosmos.Kernel.Core.X64.Cpu.LocalApic")).ToArray();
        if (calls.Any(i => ((MethodReference)i.Operand).ReturnType.MetadataType != MetadataType.Byte))
        { throw new InvalidOperationException("Cosmos physical IRQ CPU identity changed"); }
        var library = module.ModuleReferences.FirstOrDefault(r => r.Name == "*");
        if (library is null) { library = new ModuleReference("*"); module.ModuleReferences.Add(library); }
        var native = new MethodDefinition(marker, MethodAttributes.Assembly | MethodAttributes.Static
            | MethodAttributes.PInvokeImpl, module.TypeSystem.UInt32);
        native.PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl,"zenith_cpu_logical_id",library);
        native.ImplAttributes = MethodImplAttributes.PreserveSig;
        native.CustomAttributes.Add(new CustomAttribute(module.ImportReference(
            typeof(System.Runtime.InteropServices.SuppressGCTransitionAttribute).GetConstructor(Type.EmptyTypes)!)));
        controller.Methods.Add(native);
        foreach (var call in calls) { call.Operand = native; }
        Console.WriteLine("Adapted timer and device-IRQ rescheduling: logical CPU ownership, not physical APIC array index");
        return true;
    }
}
