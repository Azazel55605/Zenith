using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
string host=args[0]+".metadata-host.dll";
using(var a=AssemblyDefinition.ReadAssembly(args[0]))
{
    var m=a.MainModule;
    void Hook(MethodDefinition method,string name)
    {
        method.PInvokeInfo=null;method.IsPInvokeImpl=false;
        method.ImplAttributes=Mono.Cecil.MethodImplAttributes.IL|Mono.Cecil.MethodImplAttributes.Managed;
        method.Body=new Mono.Cecil.Cil.MethodBody(method);
        var il=method.Body.GetILProcessor();foreach(var p in method.Parameters)il.Emit(OpCodes.Ldarg,p);
        il.Emit(OpCodes.Call,m.ImportReference(typeof(Hooks).GetMethod(name)!));il.Emit(OpCodes.Ret);
    }
    var cpu=m.GetType("Cosmos.Kernel.Core.Bridge.CpuNative");
    Hook(cpu.Methods.Single(x=>x.Name=="SaveIrqAndDisable"),nameof(Hooks.Mask));
    Hook(cpu.Methods.Single(x=>x.Name=="RestoreIrq"),nameof(Hooks.Restore));
    var gc=m.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector");
    Hook(gc.Methods.Single(x=>x.Name=="_zenith_gc_enter"),nameof(Hooks.Enter));
    Hook(gc.Methods.Single(x=>x.Name=="_zenith_gc_exit"),nameof(Hooks.Exit));
    Hook(gc.Methods.Single(x=>x.Name=="_zenith_gc_fail_stop"),nameof(Hooks.Fatal));
    var names=m.GetType("Cosmos.Kernel.Core.Runtime.StackTraceMetadata");
    Hook(names.Methods.Single(x=>x.Name=="_zenithMetadataNames"),nameof(Hooks.MetadataBody));
    var cache=names.NestedTypes.Single(x=>x.Name=="PerModuleMethodNameResolverCache");
    var lookup=cache.Methods.Single(x=>x.Name=="GetOrCreate");
    // Replace only the target-specific TypeManager pointer read with a fixed key.
    // Keep the production lookup, constructor, ordering and publication IL.
    var first=lookup.Body.Instructions;
    if(first[0].OpCode!=OpCodes.Ldarga_S||first[2].OpCode!=OpCodes.Ldfld)throw new Exception("key read contract changed");
    first[0].OpCode=OpCodes.Ldc_I8;first[0].Operand=42L;
    first[1].OpCode=OpCodes.Conv_I;first[1].Operand=null;first[2].OpCode=OpCodes.Nop;first[2].Operand=null;
    var resolver=names.NestedTypes.Single(x=>x.Name=="PerModuleMethodNameResolver");
    var ctor=resolver.Methods.Single(x=>x.IsConstructor&&!x.IsStatic);
    ctor.Body=new Mono.Cecil.Cil.MethodBody(ctor);var c=ctor.Body.GetILProcessor();
    c.Emit(OpCodes.Ldarg_0);c.Emit(OpCodes.Call,m.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
    c.Emit(OpCodes.Call,m.ImportReference(typeof(Hooks).GetMethod(nameof(Hooks.Construct))!));c.Emit(OpCodes.Ret);
    var serial=m.GetType("Cosmos.Kernel.Core.IO.Serial");
    Hook(serial.Methods.Single(x=>x.Name=="_zenithSerialCpu"),nameof(Hooks.Cpu));
    var init=serial.Methods.Single(x=>x.Name=="ComInit");
    var body=init.Body.Instructions;
    if(body[0].Operand is not MethodReference r||r.Name!="_zenithSerialCpu")throw new Exception("missing UART guard");
    body[3].OpCode=OpCodes.Call;body[3].Operand=m.ImportReference(typeof(Hooks).GetMethod(nameof(Hooks.Write))!);
    foreach(var i in body.Skip(4).ToArray())init.Body.GetILProcessor().Remove(i);
    init.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
    a.Write(host);
}
AssemblyLoadContext.Default.Resolving+=(c,n)=>{var p=Path.Combine(args[1],n.Name+".dll");return File.Exists(p)?c.LoadFromAssemblyPath(p):null;};
var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(host));
if(args[2].StartsWith("serial"))
{
    Hooks.LogicalCpu=args[2]=="serial-bsp"?0U:args[2]=="serial-ap"?1U:uint.MaxValue;
    assembly.GetType("Cosmos.Kernel.Core.IO.Serial")!.GetMethod("ComInit",BindingFlags.Static|BindingFlags.Public)!.Invoke(null,null);
    Console.WriteLine("writes="+Hooks.Writes);return;
}
var namesType=assembly.GetType("Cosmos.Kernel.Core.Runtime.StackTraceMetadata")!;
var cacheType=namesType.GetNestedType("PerModuleMethodNameResolverCache",BindingFlags.NonPublic)!;
var resolverType=namesType.GetNestedType("PerModuleMethodNameResolver",BindingFlags.NonPublic)!;
Hooks.Cache=RuntimeHelpers.GetUninitializedObject(cacheType);
Hooks.Keys=cacheType.GetField("_keys",BindingFlags.Instance|BindingFlags.NonPublic)!;
Hooks.Keys.SetValue(Hooks.Cache,new IntPtr[2]);
cacheType.GetField("_resolvers",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(Hooks.Cache,Array.CreateInstance(resolverType,2));
Hooks.Lookup=cacheType.GetMethod("GetOrCreate")!;
Hooks.Handle=Activator.CreateInstance(Hooks.Lookup.GetParameters()[0].ParameterType)!;
var entry=namesType.GetMethods(BindingFlags.Static|BindingFlags.Public).Single(m=>m.Name=="GetMethodNameFromStartAddressIfAvailable");
string Run()
{
    string result=(string)entry.Invoke(null,new object[]{(nuint)42,false})!;
    if(Hooks.Flags!=512)throw new Exception("IRQ state leaked");return result;
}
if(args[2]=="throw")
{
    Hooks.Fail=true;
    try{Run();throw new Exception("expected constructor failure");}
    catch(TargetInvocationException e)when(e.InnerException is InvalidOperationException){}
    if(((IntPtr[])Hooks.Keys.GetValue(Hooks.Cache)!).Any(k=>k!=IntPtr.Zero))throw new Exception("failed constructor poisoned key");
    if(Hooks.Flags!=512||Hooks.Exits!=1)throw new Exception("exception leaked ownership");
    Hooks.Fail=false;if(Run()!="ready")throw new Exception("retry failed");
}
else if(args[2]=="concurrent")
{
    Hooks.Delay=true;
    var first=Task.Run(Run);if(!Hooks.Entered.Wait(5000))throw new Exception("constructor never entered");
    var attempted=new ManualResetEventSlim();var second=Task.Run(()=>{attempted.Set();return Run();});
    if(!attempted.Wait(5000))throw new Exception("reader never entered");
    Thread.Sleep(50);
    if(Hooks.Bodies!=1)throw new Exception("reader entered while cache construction owns publication");
    Hooks.Release.Set();Task.WaitAll(first,second);
    if(first.Result!="ready"||second.Result!="ready"||Hooks.Constructors!=1)throw new Exception("invalid concurrent publication");
}
else {if(Run()!="ready"||Run()!="ready"||Hooks.Constructors!=1)throw new Exception("cache miss/hit failed");}
Console.WriteLine("PASS metadata constructors="+Hooks.Constructors+" bodies="+Hooks.Bodies+" enters="+Hooks.Enters+" exits="+Hooks.Exits+" irq="+Hooks.Flags);
public static class Hooks
{
    [ThreadStatic]private static bool initialized;[ThreadStatic]private static ulong flags;
    public static ulong Flags{get{if(!initialized){flags=512;initialized=true;}return flags;}set{initialized=true;flags=value;}}
    public static object Cache=null!,Handle=null!;public static MethodInfo Lookup=null!;public static FieldInfo Keys=null!;
    public static uint LogicalCpu;public static int Constructors,Bodies,Enters,Exits,Writes;
    public static bool Fail,Delay;public static readonly object Gate=new();
    public static readonly ManualResetEventSlim Entered=new(),Release=new();
    public static ulong Mask(){var value=Flags;Flags=0;return value;}
    public static void Restore(ulong value){Flags=value;}
    public static ulong Enter(){if(Flags!=0)throw new Exception("unmasked metadata");Monitor.Enter(Gate);Interlocked.Increment(ref Enters);return 1;}
    public static ulong Exit(){if(Flags!=0)throw new Exception("unmasked release");Interlocked.Increment(ref Exits);Monitor.Exit(Gate);return 1;}
    public static void Fatal()=>throw new Exception("native ownership invariant");
    public static uint Cpu()=>LogicalCpu;
    public static void Write()=>Writes++;
    public static string MetadataBody(nuint address,out bool hidden)
    {
        Interlocked.Increment(ref Bodies);hidden=false;
        try{return Lookup.Invoke(Cache,new[]{Handle}) is null?"null":"ready";}
        catch(TargetInvocationException e)when(e.InnerException is InvalidOperationException){throw e.InnerException;}
    }
    public static void Construct()
    {
        if(((IntPtr[])Keys.GetValue(Cache)!).Any(k=>k!=IntPtr.Zero))throw new Exception("key visible before complete resolver");
        Interlocked.Increment(ref Constructors);
        if(Fail)throw new InvalidOperationException("constructor");
        if(Delay){Entered.Set();if(!Release.Wait(5000))throw new Exception("constructor release deadline");}
    }
}
