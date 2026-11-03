using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text;

/// <summary>The BSP's central device registry must respect the native CPU
/// vectors even before an opt-in worker has installed its private IDT.</summary>
internal static class InterruptLeaseFixup
{
    internal static bool Apply(ModuleDefinition module)
    {
        if (!module.GetType("Cosmos.Kernel.Core.Scheduler.ThreadContext").Fields.Any(f => f.Name == "Xmm")) return false;
        var type = module.GetType("Cosmos.Kernel.Core.CPU.InterruptManager");
        const string marker = "_zenithIrqRegistrationAllowed";
        if (type.Methods.Any(m => m.Name == marker)) return false;
        var hashes = new Dictionary<string, string>
        {
            // Pinned method bodies, including the MSI range [0x40..0xEE].
            ["SetHandler"] = "ED7B31D1FBE1FC3CDD826B93359F9FBDCB3E2EF7C3535BA45CAFBDACF8FA030E",
            ["AllocateVector"] = "94A15C91B2DAE42E19DE97317711F5FEAEDA9D5786A1B5822392D32675C0DE8C",
            ["FreeVector"] = "E5B2747A0885C5E168A34B3392D18A0BA60B6B5627BCF70088641A19EC98DBDC",
        };
        var targets = hashes.Select(pair => type.Methods.Single(m => m.Name == pair.Key)).ToArray();
        foreach (var method in targets)
        {
            string body = string.Join("\n", method.Body.Instructions.Select(i => i.OpCode.Name + " " + i.Operand));
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
            if (!method.IsStatic || hash != hashes[method.Name])
                throw new InvalidOperationException("Cosmos centralized IRQ lease contract changed: " + method.Name);
        }
        var allowed = new MethodDefinition(marker, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.PInvokeImpl, module.TypeSystem.UInt64);
        allowed.Parameters.Add(new ParameterDefinition(module.TypeSystem.UInt64));
        allowed.PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl, "zenith_cpu_irq_registration_allowed", module.ModuleReferences.First(r => r.Name == "*"));
        allowed.ImplAttributes = MethodImplAttributes.PreserveSig;
        allowed.CustomAttributes.Add(new CustomAttribute(module.ImportReference(typeof(System.Runtime.InteropServices.SuppressGCTransitionAttribute).GetConstructor(Type.EmptyTypes)!)));
        type.Methods.Add(allowed);
        foreach (var method in targets)
        {
            var il = method.Body.GetILProcessor(); var first = method.Body.Instructions.First();
            // Allocation has no vector yet; native sentinel 256 means BSP-only
            // registry access. Its pinned allocator never enters the high range.
            il.InsertBefore(first, method.Name == "AllocateVector" ? il.Create(OpCodes.Ldc_I4, 256) : il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Conv_U8));
            il.InsertBefore(first, il.Create(OpCodes.Call, allowed));
            il.InsertBefore(first, il.Create(OpCodes.Brtrue, first));
            il.InsertBefore(first, il.Create(OpCodes.Ldstr, "IRQ registration is BSP-owned; native CPU vectors are reserved"));
            il.InsertBefore(first, il.Create(OpCodes.Newobj, module.ImportReference(typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!)));
            il.InsertBefore(first, il.Create(OpCodes.Throw));
        }
        Console.WriteLine("Centralized IRQ registry ownership: BSP-only allocation/free/registration; native F1/F3/F4/F5/F6 vectors reserved");
        return true;
    }
}
