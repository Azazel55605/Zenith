using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;

string source=args[0],folder=Path.GetDirectoryName(source)!;bool hal=args[2]=="budget";
if(args[2]=="create-main")
{
    using var fake=AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Zenith",new Version(1,0,0,0)),"Zenith",ModuleKind.Dll);
    var module=fake.MainModule;var type=new TypeDefinition("Zenith.Core.Input","MousePolling",Mono.Cecil.TypeAttributes.Public|Mono.Cecil.TypeAttributes.Abstract|Mono.Cecil.TypeAttributes.Sealed,module.TypeSystem.Object);module.Types.Add(type);
    var method=new MethodDefinition("Drain",Mono.Cecil.MethodAttributes.Public|Mono.Cecil.MethodAttributes.Static,module.TypeSystem.Void);type.Methods.Add(method);
    var il=method.Body.GetILProcessor();il.Emit(OpCodes.Ldstr,"Zenith mouse polling adapter missing");il.Emit(OpCodes.Newobj,module.ImportReference(typeof(PlatformNotSupportedException).GetConstructor(new[]{typeof(string)})!));il.Emit(OpCodes.Throw);
    fake.Write(source);return;
}
using var resolver=new DefaultAssemblyResolver();resolver.AddSearchDirectory(folder);resolver.AddSearchDirectory(args[1]);
using(var core=AssemblyDefinition.ReadAssembly(Path.Combine(folder,"Cosmos.Kernel.Core.dll"),new ReaderParameters{AssemblyResolver=resolver}))
{
    var module=core.MainModule;var cpu=module.GetType("Cosmos.Kernel.Core.Bridge.CpuNative");
    foreach(var pair in new[]{("SaveIrqAndDisable",nameof(Hooks.Save)),("RestoreIrq",nameof(Hooks.Restore))})
    {
        var method=cpu.Methods.Single(m=>m.Name==pair.Item1);method.PInvokeInfo=null;
        method.Attributes&=~Mono.Cecil.MethodAttributes.PInvokeImpl;method.ImplAttributes=Mono.Cecil.MethodImplAttributes.IL;
        method.Body=new Mono.Cecil.Cil.MethodBody(method);var il=method.Body.GetILProcessor();
        foreach(var p in method.Parameters)il.Emit(OpCodes.Ldarg,p);
        il.Emit(OpCodes.Call,module.ImportReference(typeof(Hooks).GetMethod(pair.Item2)!));il.Emit(OpCodes.Ret);
    }
    core.Write(Path.Combine(folder,"host-core.dll"));
}
File.Move(Path.Combine(folder,"host-core.dll"),Path.Combine(folder,"Cosmos.Kernel.Core.dll"),true);
string host=Path.Combine(folder,"host-"+Path.GetFileName(source));
void StubPoll(ModuleDefinition module)
{
    var manager=module.GetType("Cosmos.Kernel.System.Mouse.MouseManager");
    var method=manager.Methods.Single(m=>m.Name==(manager.Methods.Any(m=>m.Name=="_zenithMousePollCore")?"_zenithMousePollCore":"Poll"));
    method.Body=new Mono.Cecil.Cil.MethodBody(method);var il=method.Body.GetILProcessor();
    il.Emit(OpCodes.Call,module.ImportReference(typeof(Hooks).GetMethod(nameof(Hooks.Drain))!));il.Emit(OpCodes.Ret);
}
if(args[2]=="bind")
{
    string systemPath=Path.Combine(folder,"Cosmos.Kernel.System.dll");
    using(var system=AssemblyDefinition.ReadAssembly(systemPath,new ReaderParameters{AssemblyResolver=resolver}))
    { StubPoll(system.MainModule);system.Write(systemPath+".host"); }
    File.Move(systemPath+".host",systemPath,true);
}
using(var assembly=AssemblyDefinition.ReadAssembly(source,new ReaderParameters{AssemblyResolver=resolver}))
{
    var module=assembly.MainModule;
    if(args[2]=="drift")
    {
        var method=module.GetType("Cosmos.Kernel.System.Mouse.MouseManager").Methods.Single(m=>m.Name=="Poll");
        method.Body.Instructions.Single(i=>i.Operand is MethodReference m&&m.Name=="Poll"&&m.DeclaringType.Name=="IMouseDevice").OpCode=OpCodes.Nop;
        method.Body.Instructions.Single(i=>i.OpCode==OpCodes.Nop&&i.Operand is MethodReference).Operand=null;
        assembly.Write(host);assembly.Dispose();File.Copy(host,source,true);return;
    }
    MethodDefinition target;
    if(hal)
    {
        target=module.GetType("Cosmos.Kernel.HAL.Devices.Virtio.Virtqueue").Methods.Single(m=>m.Name=="GetUsedBuffer");
        target.Body=new Mono.Cecil.Cil.MethodBody(target);var il=target.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call,module.ImportReference(typeof(Hooks).GetMethod(nameof(Hooks.Next))!));il.Emit(OpCodes.Ret);
    }
    else if(args[2]!="bind") { StubPoll(module); }
    assembly.Write(host);
}
AssemblyLoadContext.Default.Resolving+=(context,name)=>
{
    var local=Path.Combine(folder,name.Name+".dll");if(File.Exists(local))return context.LoadFromAssemblyPath(local);
    var file=Path.Combine(args[1],name.Name+".dll");return File.Exists(file)?context.LoadFromAssemblyPath(file):null;
};
var loaded=AssemblyLoadContext.Default.LoadFromAssemblyPath(host);
if(hal)
{
    var queue=RuntimeHelpers.GetUninitializedObject(loaded.GetType("Cosmos.Kernel.HAL.Devices.Virtio.Virtqueue",true)!);
    var helper=loaded.GetType("Cosmos.Kernel.HAL.Devices.Input.VirtioMouse",true)!.GetMethod("_zenithTryMouseUsedBuffer",BindingFlags.Static|BindingFlags.NonPublic)!;
    Hooks.Available=100;int total=0,first=0;
    for(int pass=0;pass<2;pass++)
    {
        object?[] values={queue,0u,0u,64};int drained=0;
        while((bool)helper.Invoke(null,values)!){drained++;total++;}
        if(pass==0)first=drained;
    }
    int before=Hooks.Calls;object?[] empty={queue,123u,456u,0};bool result=(bool)helper.Invoke(null,empty)!;
    Console.WriteLine($"first={first};total={total};available={Hooks.Available};zeroBudgetCalls={Hooks.Calls-before};zeroResult={result};zeroId={empty[1]};zeroLength={empty[2]}");
}
else
{
    Hooks.Flags=args[2]=="disabled"?0x46UL:0x246UL;Hooks.Throw=args[2]=="throw";
    var method=loaded.GetType(args[2]=="bind"?"Zenith.Core.Input.MousePolling":"Cosmos.Kernel.System.Mouse.MouseManager",true)!.GetMethod(args[2]=="bind"?"Drain":"Poll",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!;
    bool threw=false;try{method.Invoke(null,null);}catch(TargetInvocationException e)when(e.InnerException is InvalidOperationException){threw=true;}
    Console.WriteLine($"insideEnabled={Hooks.Inside};flags={Hooks.Flags:x};save={Hooks.Saves};restore={Hooks.Restores};public={method.IsPublic};threw={threw}");
}

public static class Hooks
{
    public static ulong Flags=0x246;public static int Saves,Restores,Available,Calls;public static bool Inside,Throw;
    public static ulong Save(){Saves++;ulong flags=Flags;Flags&=~0x200UL;return flags;}
    public static void Restore(ulong flags){Restores++;Flags=flags;}
    public static void Drain(){Inside=(Flags&0x200)!=0;if(Throw)throw new InvalidOperationException("simulated driver failure");}
    public static bool Next(out uint id,out uint length){Calls++;id=(uint)Calls;length=8;if(Available==0)return false;Available--;return true;}
}
