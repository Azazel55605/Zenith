using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

using (var assembly = AssemblyDefinition.ReadAssembly(args[0]))
{
    var module=assembly.MainModule;
    if (args[1]=="x64")
    {
        foreach (string type in new[] { "X64InterruptController", "LocalApic" })
        {
            var target=module.GetType("Cosmos.Kernel.Core.X64.Cpu."+type);
            var method=target.Methods.Single(m=>m.Name==(type=="LocalApic"?"HandleTimerInterrupt":"Dispatch"));
            var calls=method.Body.Instructions.Where(i=>i.Operand is MethodReference c && c.Name=="_zenithLogicalIrqCpu").Count();
            var area=method.Body.Instructions.Count(i=>i.OpCode==OpCodes.Ldc_I4 && Equals(i.Operand,512));
            Console.WriteLine(type+": logical="+calls+" area512="+area);
        }
        return;
    }
    var manager=module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerManager");
    var current=manager.Methods.Single(m=>m.Name=="GetCurrentCpuId");
    if (args[1]=="drift")
    {
        current.Body.Instructions[0].OpCode=OpCodes.Ldc_I4_1;
        string temp=args[0]+".drift";assembly.Write(temp);File.Move(temp,args[0],true);return;
    }
    if (args[1].StartsWith("identity:"))
    {
        var native=manager.Methods.Single(m=>m.Name=="_zenithCurrentCpuId");
        Console.WriteLine("native="+native.PInvokeInfo.EntryPoint);
        native.PInvokeInfo=null;native.IsPInvokeImpl=false;
        native.ImplAttributes=Mono.Cecil.MethodImplAttributes.IL|Mono.Cecil.MethodImplAttributes.Managed;
        native.Body=new Mono.Cecil.Cil.MethodBody(native);
        native.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4,unchecked((int)uint.Parse(args[1].Split(':')[1]))));
        native.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }
    if (args[1]=="layout")
    {
        var context=module.GetType("Cosmos.Kernel.Core.Scheduler.ThreadContext");
        Console.WriteLine("buffer="+context.Fields.Single(f=>f.Name=="Xmm").FieldType.Resolve().ClassSize);
    }
    assembly.Write(args[0]+".host.dll");
}
AssemblyLoadContext.Default.Resolving+=(context,name)=>
{
    string p=Path.Combine(args[2],name.Name+".dll");return File.Exists(p)?context.LoadFromAssemblyPath(p):null;
};
var loaded=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]+".host.dll"));
if (args[1].StartsWith("identity:"))
{
    var manager=loaded.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerManager")!;
    Console.WriteLine("current="+manager.GetMethod("GetCurrentCpuId",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,null));
}
else
{
    var type=loaded.GetType("Cosmos.Kernel.Core.Scheduler.ThreadContext")!;
    Console.WriteLine("size="+Marshal.SizeOf(type)+" r15="+Marshal.OffsetOf(type,"R15"));
    object context=Activator.CreateInstance(type)!;
    type.GetMethod("Initialize")!.Invoke(context,new object[]{(nuint)0x1234,(ushort)8,(nuint)0,(nuint)0x10000});
    var pin=GCHandle.Alloc(context,GCHandleType.Pinned);
    try
    {
        IntPtr address=pin.AddrOfPinnedObject();
        Console.WriteLine("cw="+Marshal.ReadInt16(address).ToString("x")+" mxcsr="+Marshal.ReadInt32(address,24).ToString("x"));
    }
    finally {pin.Free();}
}
