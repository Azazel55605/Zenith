using Mono.Cecil;
using Mono.Cecil.Cil;
using var assembly=AssemblyDefinition.ReadAssembly(args[0]);
var type=assembly.MainModule.GetType("Internal.Runtime.CompilerHelpers.LibraryInitializer");
var method=type.Methods.Single(m=>m.Name=="InitializeScheduler");
if(args.Length>1)
{
    method.Parameters[0].ParameterType=assembly.MainModule.TypeSystem.Int32;
    assembly.Write(args[1]);
}
else
{
    if(method.Body.Instructions[0].OpCode!=OpCodes.Ldc_I4_1
        ||method.Body.Instructions[1].OpCode!=OpCodes.Starg
        ||method.Body.Instructions[1].Operand!=method.Parameters[0])throw new Exception("bootstrap is not BSP-only");
    if(!method.Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="SetupIdleThread"))throw new Exception("owner setup lost");
    Console.WriteLine("BSP-only scheduler owner bootstrap preserved");
}
