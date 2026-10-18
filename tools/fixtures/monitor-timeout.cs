// Execute the actual adapted interop branch/return IL. Only monitor acquisition
// and the kernel wait operations are replaced; their results are controlled.
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;

string destination = Path.Combine(Path.GetDirectoryName(args[0])!, "host-monitor.dll");
using (var assembly = AssemblyDefinition.ReadAssembly(args[0]))
{
    var module = assembly.MainModule;
    var type = module.GetType(args[1]);
    var method = type.Methods.Single(m => m.Name == "LowLevelMonitor_TimedWait");
    var code = method.Body.Instructions;
    // The prefix builds tracing arguments and obtains the opaque monitor handle.
    // Bypass that setup, retaining the real timeout dispatch and both returns.
    var branch = code.Single(i => i.OpCode == OpCodes.Bge_S);
    var first = branch.Previous.Previous;
    foreach (var i in code.Take(code.IndexOf(first))) { i.OpCode = OpCodes.Nop; i.Operand = null; }
    var result = new FieldDefinition("TestSignal", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, module.TypeSystem.Boolean);
    var calls = new FieldDefinition("TestCalls", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, module.TypeSystem.Int32);
    type.Fields.Add(result); type.Fields.Add(calls);
    foreach (var call in code.Where(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference m && m.Name == "Wait").ToArray())
    {
        var original = (MethodReference)call.Operand;
        var stub = new MethodDefinition("TestWait" + original.Parameters.Count,
            Mono.Cecil.MethodAttributes.Private | Mono.Cecil.MethodAttributes.Static, original.ReturnType);
        stub.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        foreach (var p in original.Parameters) stub.Parameters.Add(new ParameterDefinition(p.ParameterType));
        stub.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld,calls));
        stub.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
        stub.Body.Instructions.Add(Instruction.Create(OpCodes.Add));
        stub.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld,calls));
        if (original.ReturnType.FullName == "System.Boolean") stub.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld,result));
        stub.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(stub); call.OpCode = OpCodes.Call; call.Operand = stub;
    }
    // Use a neutral host assembly, so the exact transplanted CoreLib method can
    // also execute without attempting to load a second System.Private.CoreLib.
    using var host = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("MonitorProbe",new Version(1,0)),"MonitorProbe",ModuleKind.Dll);
    var hostType = new TypeDefinition("Probe","Monitor",Mono.Cecil.TypeAttributes.Public,host.MainModule.TypeSystem.Object);
    host.MainModule.Types.Add(hostType);
    foreach (var f in new[] {result,calls}) { type.Fields.Remove(f); hostType.Fields.Add(f); }
    result.FieldType=host.MainModule.TypeSystem.Boolean;calls.FieldType=host.MainModule.TypeSystem.Int32;
    foreach (var m in type.Methods.Where(m => m == method || m.Name.StartsWith("TestWait")).ToArray())
    {
        type.Methods.Remove(m);hostType.Methods.Add(m);
        m.ReturnType=m.ReturnType.FullName == "System.Void" ? host.MainModule.TypeSystem.Void : host.MainModule.TypeSystem.Boolean;
        foreach (var p in m.Parameters) p.ParameterType=p.ParameterType.FullName switch {
            "System.IntPtr" => host.MainModule.TypeSystem.IntPtr,
            "System.Int32" => host.MainModule.TypeSystem.Int32,
            _ => host.MainModule.TypeSystem.Object };
        foreach (var v in m.Body.Variables) v.VariableType=host.MainModule.TypeSystem.Object;
    }
    host.Write(destination);
}
AssemblyLoadContext.Default.Resolving += (context,name) => {
    string path = Path.Combine(args[2],name.Name+".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
var loaded = AssemblyLoadContext.Default.LoadFromAssemblyPath(destination);
var runtimeType = loaded.GetType("Probe.Monitor",true)!;
var wait = runtimeType.GetMethod("LowLevelMonitor_TimedWait",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public)!;
foreach (var (timeout,signal) in new[] { (1,false),(1,true),(0,false),(-1,false) })
{
    runtimeType.GetField("TestSignal")!.SetValue(null,signal);
    runtimeType.GetField("TestCalls")!.SetValue(null,0);
    Console.WriteLine($"timeout={timeout};signal={signal};result={wait.Invoke(null,new object[]{IntPtr.Zero,timeout})};calls={runtimeType.GetField("TestCalls")!.GetValue(null)}");
}
