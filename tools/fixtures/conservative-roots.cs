using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
string host=Path.Combine(Path.GetDirectoryName(args[0])!,"host-"+Path.GetFileName(args[0]));
using(var a=AssemblyDefinition.ReadAssembly(args[0]))
{
 var gc=a.MainModule.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector");
 foreach(var m in gc.Methods.Where(m=>m.Name is "TryMarkRoot" or "FindObjectContaining" or "_zenithFindConservativeRoot"))
 foreach(var i in m.Body.Instructions)
 if(i.OpCode==OpCodes.Ldc_I8 && (long)i.Operand==unchecked((long)0xffff800000000000UL)) i.Operand=0L;
 a.Write(host);
}
AssemblyLoadContext.Default.Resolving+=(c,n)=>{var p=Path.Combine(args[1],n.Name+".dll");return File.Exists(p)?c.LoadFromAssemblyPath(p):null;};
var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(host);
var type=assembly.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector",true)!;
unsafe {
 byte* owner=(byte*)NativeMemory.AllocZeroed(128), segment=(byte*)NativeMemory.AllocZeroed(64), mt=(byte*)NativeMemory.AllocZeroed(64), opaque=(byte*)NativeMemory.AllocZeroed(64), marks=(byte*)NativeMemory.AllocZeroed(256), free=(byte*)NativeMemory.AllocZeroed(64);
 try {
  bool array=args[2]=="array", isFree=args[2]=="free";
  *(uint*)mt=array?0x80000001u:0;*(uint*)(mt+4)=array?16u:64u;
  *(uint*)(free+4)=64;
  *(nint*)owner=(nint)(isFree?free:mt);*(uint*)(owner+8)=array?24u:64u;
  *(nint*)(owner+16)=(nint)opaque;
  *(nint*)(segment+8)=(nint)owner;*(nint*)(segment+16)=(nint)(owner+128);*(nint*)(segment+24)=(nint)(owner+(array?40:64));
  void Field(string n,object value)=>type.GetField(n,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.SetValue(null,value);
  void Ptr(string n,void* p){var f=type.GetField(n,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!;f.SetValue(null,Pointer.Box(p,f.FieldType));}
  var manager=type.GetField("s_segmentManager",BindingFlags.Static|BindingFlags.NonPublic)!.FieldType;
  var boxed=Activator.CreateInstance(manager)!;var prop=manager.GetProperty("Segments")!;
  prop.SetValue(boxed,Pointer.Box(segment,prop.PropertyType));Field(args[2]=="pinned"?"s_pinnedSegmentManager":"s_segmentManager",boxed);
  Ptr("s_gcHeapMin",owner);Ptr("s_gcHeapMax",owner+128);Field("s_heapRangeDirty",false);
  Ptr("s_markStack",marks);Field("s_markStackCapacity",32);Field("s_markStackCount",0);Ptr("s_freeMethodTable",free);
  nint value=(nint)(owner+(args[2]=="canonical"?0:args[2]=="padding"?80:args[2]=="outside"?128:args[2]=="unaligned"?17:16));
  string method=type.GetMethod("_zenithMarkConservativeRoot",BindingFlags.Static|BindingFlags.NonPublic)!=null?"_zenithMarkConservativeRoot":"TryMarkRoot";
  if(args[2]=="stack-range") {
   var scan=type.GetMethod("ScanMemoryRange",BindingFlags.Static|BindingFlags.NonPublic)!;
   nint root=value;var pointer=scan.GetParameters()[0].ParameterType;
   scan.Invoke(null,new object[]{Pointer.Box(&root,pointer),Pointer.Box(&root+1,pointer)});
  } else { type.GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{value}); }
  Console.WriteLine("ownerMarked="+((*(nuint*)owner&1)!=0)+";handleChanged="+(*(nint*)(owner+16)!=(nint)opaque));
 }finally{NativeMemory.Free(owner);NativeMemory.Free(segment);NativeMemory.Free(mt);NativeMemory.Free(opaque);NativeMemory.Free(marks);NativeMemory.Free(free);}
}
