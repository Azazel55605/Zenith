using Mono.Cecil;
using Mono.Cecil.Cil;

// Preserve the complete baseline x87/MMX/SSE environment, not just XMM values.
// The managed layout, initial context and both IRQ RSP derivations must change
// together with the signature-checked native IRQ source.
internal static class FpContextFixup
{
    internal static bool Core(ModuleDefinition module)
    {
        var context = module.GetType("Cosmos.Kernel.Core.Scheduler.ThreadContext");
        var xmm = context.Fields.SingleOrDefault(f => f.Name == "Xmm");
        if (xmm is null) { return false; } // ARM64 has a different context contract.
        var buffer = xmm.FieldType.Resolve();
        if (buffer.ClassSize == 512) { return false; }
        var size = context.Fields.Single(f => f.Name == "Size");
        var init = context.Methods.Single(m => m.Name == "Initialize");
        var bounds = init.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldc_I4 && Equals(i.Operand,256)).ToArray();
        if (buffer.ClassSize != 256 || Convert.ToInt32(size.Constant) != 448 || bounds.Length != 1
            || init.Body.Instructions.Last().OpCode != OpCodes.Ret)
        { throw new InvalidOperationException("Cosmos x64 FP context layout changed"); }
        buffer.ClassSize = 512; size.Constant = 704; bounds[0].Operand = 512;
        var fixedBuffer = xmm.CustomAttributes.Single(a => a.AttributeType.Name == "FixedBufferAttribute");
        fixedBuffer.ConstructorArguments[1] = new CustomAttributeArgument(module.TypeSystem.Int32,512);
        var il = init.Body.GetILProcessor(); var ret = init.Body.Instructions.Last();
        foreach (var instruction in new[] {
            il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldflda,xmm), il.Create(OpCodes.Ldc_I4,0x37f), il.Create(OpCodes.Stind_I2),
            il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldflda,xmm), il.Create(OpCodes.Ldc_I4,24), il.Create(OpCodes.Conv_I),
            il.Create(OpCodes.Add), il.Create(OpCodes.Ldc_I4,0x1f80), il.Create(OpCodes.Stind_I4) })
        { il.InsertBefore(ret,instruction); }
        Console.WriteLine("Adapted x64 thread FP context: 512-byte FXSAVE, initialized x87 control and MXCSR");
        return true;
    }

    internal static bool X64(ModuleDefinition module)
    {
        var controller = module.GetType("Cosmos.Kernel.Core.X64.Cpu.X64InterruptController");
        var size = controller.Fields.Single(f => f.Name == "XmmSaveAreaSizeBytes");
        if (Convert.ToInt32(size.Constant) == 512) { return false; }
        if (Convert.ToInt32(size.Constant) != 256) { throw new InvalidOperationException("Cosmos IRQ FP area changed"); }
        var methods = module.Types.SelectMany(t => t.Methods).Where(m => m.HasBody && m.Body.Instructions.Any(i =>
            i.Operand is MethodReference call && call.DeclaringType.FullName == "Cosmos.Kernel.Core.Scheduler.SchedulerManager"
            && (call.Name == "OnTimerInterrupt" || call.Name == "ReschedulePendingFromIrq"))).ToArray();
        if (methods.Length != 2) { throw new InvalidOperationException("Cosmos scheduler IRQ consumers changed"); }
        foreach (var method in methods)
        {
            var constants = method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldc_I4 && Equals(i.Operand,256)).ToArray();
            if (constants.Length != 1) { throw new InvalidOperationException("Cosmos IRQ RSP derivation changed"); }
            constants[0].Operand = 512;
        }
        size.Constant = 512;
        Console.WriteLine("Adapted both x64 IRQ scheduling RSP derivations for full FP state");
        return true;
    }
}
