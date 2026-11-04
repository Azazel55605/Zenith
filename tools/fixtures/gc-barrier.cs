using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
string host=args[0]+".host.dll";
using(var a=AssemblyDefinition.ReadAssembly(args[0]))
{
    var m=a.MainModule;
    var gc=m.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector");
    if(args[2]=="break-contract")
    {
        gc.Methods.Single(x=>x.Name=="_zenith_gc_abort").Name="_missing_gc_abort";
        a.Write(args[0]+".incomplete.dll");return;
    }
    if(args[2]=="break-boot-contract")
    {
        var call=gc.Methods.Single(x=>x.Name=="ScanThreadStack").Body.Instructions.Single(i=>
            i.Operand is MethodReference r&&r.Name=="_zenithScanSuspendedBootStack");
        call.OpCode=OpCodes.Pop;call.Operand=null;
        a.Write(args[0]+".incomplete.dll");return;
    }
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
    Hook(gc.Methods.Single(x=>x.Name=="_zenith_gc_enter"),nameof(Hooks.Enter));
    Hook(gc.Methods.Single(x=>x.Name=="_zenith_gc_exit"),nameof(Hooks.Exit));
    Hook(gc.Methods.Single(x=>x.Name=="_zenith_gc_begin"),nameof(Hooks.Begin));
    Hook(gc.Methods.Single(x=>x.Name=="_zenith_gc_end"),nameof(Hooks.End));
    Hook(gc.Methods.Single(x=>x.Name=="_zenith_gc_abort"),nameof(Hooks.Abort));
    Hook(gc.Methods.Single(x=>x.Name=="_zenith_gc_fail_stop"),nameof(Hooks.Fatal));
    Hook(gc.Methods.Single(x=>x.Name=="_zenith_gc_root"),nameof(Hooks.Root));
    Hook(gc.Methods.Single(x=>x.Name=="_zenithStoppedCollect"),nameof(Hooks.Body));
    if(args[2].StartsWith("boot-"))
    {
        var boot=m.GetType("Cosmos.Kernel.Core.Runtime.BootStack");
        Hook(boot.Methods.Single(x=>x.Name=="get_Top"),nameof(Hooks.BootTop));
        Hook(boot.Methods.Single(x=>x.Name=="get_Size"),nameof(Hooks.BootSize));
        Hook(gc.Methods.Single(x=>x.Name=="ScanMemoryRange"),nameof(Hooks.Range));
    }
    if(args[2].StartsWith("heap"))
        Hook(m.GetType("Cosmos.Kernel.Core.Memory.Heap.Heap").Methods.Single(x=>x.Name=="_zenithHeap_Collect"),nameof(Hooks.Body));
    a.Write(host);
}
AssemblyLoadContext.Default.Resolving+=(c,n)=>{var p=Path.Combine(args[1],n.Name+".dll");return File.Exists(p)?c.LoadFromAssemblyPath(p):null;};
var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(host));
var type=assembly.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector")!;
Hooks.Refuse=args[2]=="refuse";Hooks.Throw=args[2] is "throw" or "heap-throw";
try
{
    if(args[2].StartsWith("heap"))type=assembly.GetType("Cosmos.Kernel.Core.Memory.Heap.Heap")!;
    string method=args[2]=="scan"?"_zenithScanStoppedRoots":"Collect";
    object[]? parameters=null;
    if(args[2].StartsWith("boot-"))
    {
        method="_zenithScanSuspendedBootStack";
        var threadType=assembly.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerThread")!;
        object thread=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(threadType);
        void Set(string name,object value)=>threadType.GetProperty(name)!.SetValue(thread,value);
        Set("Id",args[2]=="boot-other"?1U:0U);Set("CpuId",args[2]=="boot-ap"?1U:0U);
        var state=assembly.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerThreadState")!;
        Set("State",Enum.Parse(state,args[2]=="boot-running"?"Running":"Ready"));
        Set("StackBase",(nuint)0);Set("StackSize",(nuint)0);
        nuint sp=args[2] switch {"boot-low"=>0xff8,"boot-high"=>0x2008,"boot-unaligned"=>0x1021,_=>0x1020};
        Set("StackPointer",sp);parameters=new[]{thread};
    }
    var result=type.GetMethod(method,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,parameters);
    Console.WriteLine("result="+(result??"void"));
}
catch(TargetInvocationException e) when(e.InnerException is InvalidOperationException) {Console.WriteLine("thrown=body");}
catch(TargetInvocationException e) when(e.InnerException is Hooks.CollectorAborted) {Console.WriteLine("fatal=collector");}
catch(TargetInvocationException e) when(e.InnerException is Hooks.RootBoundsRefused) {Console.WriteLine("fatal=boot-bounds");}
Console.WriteLine("body="+Hooks.Bodies+" end="+Hooks.Ends+" irq="+Hooks.Flags+" roots="+Hooks.Reads+" enters="+Hooks.Enters+" exits="+Hooks.Exits+" aborts="+Hooks.Aborts);
Console.WriteLine("ranges="+Hooks.Ranges+" start="+Hooks.RangeStart+" limit="+Hooks.RangeEnd);
public static class Hooks
{
    public static bool Refuse,Throw;public static int Bodies,Ends,Reads,Enters,Exits,Aborts;public static ulong Flags=512;
    public static ulong Mask(){var f=Flags;Flags=0;return f;}
    public static void Restore(ulong flags){Flags=flags;}
    public static ulong Enter(){if(Flags!=0)throw new Exception("unmasked heap");Enters++;return 1;}
    public static ulong Exit(){if(Flags!=0)throw new Exception("unmasked heap release");Exits++;return 1;}
    public static ulong Begin(){if(Flags!=0)throw new Exception("unmasked collection");return Refuse?0UL:42;}
    public static ulong End(ulong epoch){if(epoch!=42||Flags!=0)throw new Exception("invalid collection owner");Ends++;return 1;}
    public static int Body(){Bodies++;if(Throw)throw new InvalidOperationException("body");return 7;}
    public sealed class CollectorAborted : Exception {}
    public static void Abort(ulong epoch){if(epoch!=42||Flags!=0)throw new Exception("invalid failed collector");Aborts++;throw new CollectorAborted();}
    public sealed class RootBoundsRefused : Exception {}
    public static int Ranges;public static nuint RangeStart,RangeEnd;
    public static nuint BootTop()=>0x2000;
    public static nuint BootSize()=>0x1000;
    public static void Range(nuint start,nuint end){Ranges++;RangeStart=start;RangeEnd=end;}
    public static void Fatal()=>throw new RootBoundsRefused();
    public static ulong Root(ulong cpu,ulong field){Reads++;return 0;}
}
