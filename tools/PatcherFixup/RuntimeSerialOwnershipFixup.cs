using Mono.Cecil;
using Mono.Cecil.Cil;

// Even allocator diagnostics may echo through EarlyGop. Keep both hardware UART
// and early framebuffer writes on the BSP, including managed AP error paths.
internal static class RuntimeSerialOwnershipFixup
{
    internal static bool Apply(ModuleDefinition module)
    {
        var gc = module.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector");
        if (!gc.Methods.Any(m => m.Name == "_zenith_gc_enter")) return false;
        var serial = module.GetType("Cosmos.Kernel.Core.IO.Serial");
        if (serial.Methods.Any(m => m.Name == "_zenithSerialCpu")) return false;
        var targets = serial.Methods.Where(m => m.Name.StartsWith("Write") || m.Name is "ComWrite" or "ComInit").ToArray();
        if (targets.Length != 12 || targets.Any(m => !m.IsStatic || !m.HasBody || m.Body.HasExceptionHandlers
            || m.ReturnType.MetadataType != MetadataType.Void))
            throw new InvalidOperationException("Cosmos early serial/GOP ownership contract changed");
        var current = new MethodDefinition("_zenithSerialCpu", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.PInvokeImpl, module.TypeSystem.UInt32);
        current.PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl, "zenith_cpu_logical_id", module.ModuleReferences.First(r => r.Name == "*"));
        current.ImplAttributes = MethodImplAttributes.PreserveSig;
        current.CustomAttributes.Add(new CustomAttribute(module.ImportReference(typeof(System.Runtime.InteropServices.SuppressGCTransitionAttribute).GetConstructor(Type.EmptyTypes)!)));
        serial.Methods.Add(current);
        foreach (var method in targets)
        {
            var il = method.Body.GetILProcessor(); var first = method.Body.Instructions.First();
            il.InsertBefore(first, il.Create(OpCodes.Call, current));
            il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
            il.InsertBefore(first, il.Create(OpCodes.Ret));
        }
        Console.WriteLine("Kept all twelve early UART/GOP writers BSP-owned, including allocator and exception diagnostics");
        return true;
    }
}
