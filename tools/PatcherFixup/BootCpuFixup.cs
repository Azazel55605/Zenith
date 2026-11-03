using Mono.Cecil;
using Mono.Cecil.Cil;

// Physical topology remains available to the native worker layer. The general
// managed scheduler starts only its real BSP owner; C3 registers AP owners later.
internal static class BootCpuFixup
{
    internal static bool Apply(ModuleDefinition module)
    {
        var type = module.GetType("Internal.Runtime.CompilerHelpers.LibraryInitializer")
            ?? throw new InvalidOperationException("Cosmos scheduler initializer type changed");
        var method = type.Methods.Single(m => m.Name == "InitializeScheduler");
        if (method.Parameters.Count != 1 || method.Parameters[0].ParameterType.MetadataType != MetadataType.UInt32
            || !method.IsStatic || !method.HasBody || method.ReturnType.MetadataType != MetadataType.Void
            || method.Body.Instructions.Count(i => i.Operand is MethodReference r
                && r.DeclaringType.FullName == "Cosmos.Kernel.Core.Scheduler.SchedulerManager"
                && r.Name == "Initialize") != 1)
            throw new InvalidOperationException("Cosmos scheduler bootstrap contract changed");
        if (type.Fields.Any(f => f.Name == "_zenithBspSchedulerBootstrap")) return false;
        var il = method.Body.GetILProcessor();
        var first = method.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(first, il.Create(OpCodes.Starg, method.Parameters[0]));
        type.Fields.Add(new FieldDefinition("_zenithBspSchedulerBootstrap", FieldAttributes.Private | FieldAttributes.Static,
            module.TypeSystem.Boolean));
        Console.WriteLine("Restricted general scheduler bootstrap to its real BSP owner; physical topology retained");
        return true;
    }
}
