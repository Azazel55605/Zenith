using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.InteropServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
unsafe class Program
{
 static void Main(string[] args)
 {
  string host=args[0]+".weak-host.dll";
  using(var a=AssemblyDefinition.ReadAssembly(args[0]))
  {
   var m=a.MainModule;var gc=m.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector");
   var mgr=m.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GCHandleManager");
   var clear=mgr.Methods.Single(x=>x.Name=="_zenithClearWeakTarget");
   var sweep=mgr.Methods.Single(x=>x.Name=="FreeWeakHandles");
   if(sweep.Body.Instructions.Count(i=>i.Operand is MethodReference call&&call.Name==clear.Name)!=2
      ||sweep.Body.Instructions.Any(i=>i.Operand is MethodReference call&&call.Name=="FreeHandle"))throw new Exception("sweep recycled an owned slot");
   void Hook(MethodDefinition method,string name)
   {
    method.Body=new Mono.Cecil.Cil.MethodBody(method);var il=method.Body.GetILProcessor();
    il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Conv_I);il.Emit(OpCodes.Call,m.ImportReference(typeof(Hooks).GetMethod(name)!));il.Emit(OpCodes.Ret);
   }
   Hook(gc.Methods.Single(x=>x.Name=="IsInFrozenSegment"),nameof(Hooks.Frozen));
   Hook(m.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GCObject").Methods.Single(x=>x.Name=="get_IsMarked"),nameof(Hooks.Marked));
   a.Write(host);
  }
  var alc=new AssemblyLoadContext("weak-host");alc.Resolving+=(c,n)=>{var p=Path.Combine(args[1],n.Name+".dll");return File.Exists(p)?c.LoadFromAssemblyPath(p):null;};
  var asm=alc.LoadFromAssemblyPath(host);var type=asm.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector")!;
  var live=type.GetMethod("_zenithLiveHandleTarget",BindingFlags.Static|BindingFlags.NonPublic)!;
  object Ptr(nint p,Type t)=>Pointer.Box((void*)p,t);
  bool Alive(nint p)=>(bool)live.Invoke(null,new[]{Ptr(p,live.GetParameters()[0].ParameterType)})!;
  if(Alive(0)||Alive(42)||!Alive(1)||!Alive(2)||Hooks.MarkCalls!=2)throw new Exception("null/frozen/marked/unmarked reachability");
  var manager=asm.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GCHandleManager")!;
  var clearTarget=manager.GetMethod("_zenithClearWeakTarget",BindingFlags.Static|BindingFlags.NonPublic)!;
  var store=Activator.CreateInstance(clearTarget.GetParameters()[0].ParameterType.GetElementType()!)!;
  nint slot=Marshal.AllocHGlobal(24);
  try
  {
   foreach(int kind in new[]{0,6})
   {
    Marshal.WriteIntPtr(slot,42);Marshal.WriteIntPtr(slot,8,99);Marshal.WriteInt32(slot,16,kind);
    var owner=slot;
    for(int i=0;i<3;i++)clearTarget.Invoke(null,new[]{store,Ptr(slot,clearTarget.GetParameters()[1].ParameterType)});
    if(slot!=owner||Marshal.ReadIntPtr(slot)!=0||Marshal.ReadIntPtr(slot,8)!=0||Marshal.ReadInt32(slot,16)!=kind)throw new Exception("weak slot identity/type/target changed");
   }
  }finally{Marshal.FreeHGlobal(slot);}
  Console.WriteLine("PASS actual weak/dependent sweep: frozen roots, dead targets, stable owned slots across repeated cleanup");
 }
}
public static class Hooks
{
 public static int MarkCalls;
 public static bool Frozen(nint p)=>p==1;
 public static bool Marked(nint p){MarkCalls++;return p==2;}
}
