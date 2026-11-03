using System.Reflection;
using System.Runtime.InteropServices;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args[0] == "drift")
{
    using var module = ModuleDefinition.ReadModule(args[1], new ReaderParameters { InMemory = true });
    var method = module.GetType("Cosmos.Kernel.Core.CPU.InterruptManager").Methods.Single(m => m.Name == args[2]);
    method.Body.GetILProcessor().InsertBefore(method.Body.Instructions[0], Instruction.Create(OpCodes.Nop));
    module.Write(args[1]); return;
}
using (var module = ModuleDefinition.ReadModule(args[1], new ReaderParameters { InMemory = true }))
{
    var type = module.GetType("Cosmos.Kernel.Core.CPU.InterruptManager");
    var calls = new FieldDefinition("_testBodyCalls", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, module.TypeSystem.Int32);
    type.Fields.Add(calls);
    foreach (string name in new[] { "SetHandler", "AllocateVector", "FreeVector" })
    {
        var method = type.Methods.Single(m => m.Name == name);
        var code = method.Body.Instructions;
        int call = code.Select((i,n) => (i,n)).Single(v => v.i.OpCode == OpCodes.Call && v.i.Operand is MethodReference f && f.Name == "_zenithIrqRegistrationAllowed").n;
        if (call != 2 || code[call+1].OpCode != OpCodes.Brtrue || code[call+3].OpCode != OpCodes.Newobj || code[call+4].OpCode != OpCodes.Throw)
            throw new Exception("IRQ guard must run before original registry/lock body");
        var first = (Instruction)code[call+1].Operand;
        int begin = code.IndexOf(first);
        // Retain and execute the exact adapted prefix. Replace only its already
        // signature-checked backend, which needs actual kernel IRQ/SpinLock.
        while (code.Count > begin + 1) code.RemoveAt(begin + 1);
        first.OpCode = OpCodes.Ldsfld; first.Operand = calls;
        var il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stsfld, calls);
        if (name == "AllocateVector") il.Emit(OpCodes.Ldc_I4,64);
        il.Emit(OpCodes.Ret); method.Body.ExceptionHandlers.Clear(); method.Body.Variables.Clear();
    }
    module.Write(args[1]);
}
IntPtr native = NativeLibrary.Load(args[2]);
var identity = Marshal.GetDelegateForFunctionPointer<SetIdentity>(NativeLibrary.GetExport(native,"zenith_cpu_host_identity"));
var assembly = Assembly.LoadFrom(args[1]);
NativeLibrary.SetDllImportResolver(assembly,(name,owner,path) => name=="*" ? native : IntPtr.Zero);
var manager = assembly.GetType("Cosmos.Kernel.Core.CPU.InterruptManager")!;
int invoked = 0, denied = 0;
foreach (ulong cpu in new ulong[] { 0, 1, 7, 8, 255 })
{
    identity(cpu);
    foreach (int vector in new[] { 32, 64, 238, 239, 240, 241, 242, 243, 244, 245, 246, 247, 255 })
    {
        bool accepted = cpu == 0 && vector is not (241 or 243 or 244 or 245 or 246);
        foreach (string name in new[] { "SetHandler", "FreeVector" })
        {
            object?[] parameters = name == "SetHandler" ? new object?[] { (byte)vector, null } : new object?[] { (byte)vector };
            Invoke(name,parameters,accepted);
        }
    }
    Invoke("AllocateVector",new object?[] { null },cpu == 0);
}
int bodies = (int)manager.GetField("_testBodyCalls")!.GetValue(null)!;
if (bodies != invoked) throw new Exception("denied call reached IRQ registry/lock");
Console.WriteLine("PASS centralized IRQ leases; accepted="+invoked+" denied="+denied);
void Invoke(string name, object?[] parameters, bool accepted)
{
    try
    {
        manager.GetMethod(name,BindingFlags.Public|BindingFlags.Static)!.Invoke(null,parameters);
        if (!accepted) throw new Exception("forbidden IRQ access accepted");
        invoked++;
    }
    catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
    {
        if (accepted) throw;
        denied++;
    }
}
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void SetIdentity(ulong cpu);
