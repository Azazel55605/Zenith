using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;

string source=args[0],host=Path.Combine(Path.GetDirectoryName(source)!,"host-"+Path.GetFileName(source));
using(var assembly=AssemblyDefinition.ReadAssembly(source))
{
    var module=assembly.MainModule;
    var memory=module.GetType("Cosmos.Kernel.Core.Runtime.Memory");
    if(args[2]=="drift")
    {
        module.GetType("Cosmos.Kernel.Core.Memory.GarbageCollector.GCObject").Fields.Single(f=>f.Name=="Length").Name="ChangedLength";
        assembly.Write(host);assembly.Dispose();File.Copy(host,source,true);return;
    }
    var cpu=module.Types.SelectMany(t=>new[]{t}.Concat(t.NestedTypes)).Single(t=>t.Name=="CpuNative");
    void Bridge(MethodDefinition method,string target)
    {
        method.PInvokeInfo=null;
        method.Attributes &= ~Mono.Cecil.MethodAttributes.PInvokeImpl;
        method.ImplAttributes=Mono.Cecil.MethodImplAttributes.IL;
        method.Body=new Mono.Cecil.Cil.MethodBody(method);
        var il=method.Body.GetILProcessor();
        foreach(var parameter in method.Parameters)il.Emit(OpCodes.Ldarg,parameter);
        il.Emit(OpCodes.Call,module.ImportReference(typeof(Hooks).GetMethod(target)!));il.Emit(OpCodes.Ret);
    }
    Bridge(cpu.Methods.Single(m=>m.Name=="SaveIrqAndDisable"),nameof(Hooks.Save));
    Bridge(cpu.Methods.Single(m=>m.Name=="RestoreIrq"),nameof(Hooks.Restore));
    var allocate=memory.Methods.Single(m=>m.Name=="AllocObject");
    allocate.Body=new Mono.Cecil.Cil.MethodBody(allocate);
    var body=allocate.Body.GetILProcessor();body.Emit(OpCodes.Ldarg_0);
    body.Emit(OpCodes.Call,module.ImportReference(typeof(Hooks).GetMethod(nameof(Hooks.Allocate))!));body.Emit(OpCodes.Conv_U);body.Emit(OpCodes.Ret);
    foreach(var method in memory.Methods.Where(m=>m.HasBody))
    {
        var call=method.Body.Instructions.SingleOrDefault(i=>i.OpCode==OpCodes.Call && i.Operand is MethodReference target && target.FullName==allocate.FullName);
        if(call is null)continue;
        var il=method.Body.GetILProcessor();var dup=il.Create(OpCodes.Dup);var convert=il.Create(OpCodes.Conv_I);
        il.InsertAfter(call,dup);il.InsertAfter(dup,convert);
        il.InsertAfter(convert,il.Create(OpCodes.Call,module.ImportReference(typeof(Hooks).GetMethod(nameof(Hooks.Probe))!)));
    }
    string selected=args[2] switch {"object"=>"RhpNewFast","object-out"=>"RhAllocateNewObject","array-out"=>"RhAllocateNewArray","checked"=>"RhpNewArray","ptr"=>"RhpNewPtrArrayFast",_=>"RhpNewArrayFast"};
    var target=memory.Methods.Single(m=>m.Name==selected);
    var proxy=new MethodDefinition("_hostInvoke",Mono.Cecil.MethodAttributes.Public|Mono.Cecil.MethodAttributes.Static,module.TypeSystem.IntPtr);
    proxy.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));
    proxy.Body=new Mono.Cecil.Cil.MethodBody(proxy){InitLocals=true};
    var code=proxy.Body.GetILProcessor();code.Emit(OpCodes.Ldarg_0);code.Emit(OpCodes.Conv_U);
    bool isArray=selected.Contains("Array",StringComparison.Ordinal),output=selected.StartsWith("RhAllocate",StringComparison.Ordinal);
    if(isArray)code.Emit(OpCodes.Ldc_I4,args[2]=="negative"?-1:10);
    VariableDefinition slot=null;
    if(output)
    {
        code.Emit(OpCodes.Ldc_I4_0);
        slot=new VariableDefinition(new PointerType(module.TypeSystem.Void));proxy.Body.Variables.Add(slot);
        code.Emit(OpCodes.Ldloca,slot);
        if(target.Parameters[^1].ParameterType is PointerType)code.Emit(OpCodes.Conv_U);
    }
    code.Emit(OpCodes.Call,target);
    if(slot!=null)code.Emit(OpCodes.Ldloc,slot);
    code.Emit(OpCodes.Conv_I);code.Emit(OpCodes.Ret);memory.Methods.Add(proxy);
    assembly.Write(host);
}
AssemblyLoadContext.Default.Resolving+=(context,name)=>
{
    var file=Path.Combine(args[1],name.Name+".dll");return File.Exists(file)?context.LoadFromAssemblyPath(file):null;
};
var loaded=AssemblyLoadContext.Default.LoadFromAssemblyPath(host);
var runtime=loaded.GetType("Cosmos.Kernel.Core.Runtime.Memory",true)!;
unsafe
{
    void* table=NativeMemory.AllocZeroed(64);
    try
    {
        bool obj=args[2].StartsWith("object",StringComparison.Ordinal);
        *(uint*)table=obj?0u:0x80000001u;*((uint*)table+1)=obj?64u:16u;
        Hooks.Expected=(nint)table;Hooks.Array=!obj;Hooks.Enabled=args[2]!="disabled";Hooks.Throw=args[2]=="throw";
        var entry=runtime.GetMethod("_hostInvoke",BindingFlags.Static|BindingFlags.Public)!;
        object?[] values={(nint)table};
        bool threw=false;
        try { if((nint)entry.Invoke(null,values)!=Hooks.Allocated)throw new InvalidOperationException("incorrect allocation result"); }
        catch(TargetInvocationException e) when(e.InnerException is InvalidOperationException) { threw=true; }
        Console.WriteLine($"uninitializedVisible={Hooks.Exposed};publishedBeforeRestore={Hooks.Published};irqEnabled={Hooks.Enabled};threw={threw}");
    }
    finally
    {
        if(Hooks.Allocated!=0)NativeMemory.Free((void*)Hooks.Allocated);
        NativeMemory.Free(table);
    }
}

public static unsafe class Hooks
{
    public static bool Enabled=true,Exposed,Published,Array,Throw;
    public static nint Expected,Allocated;
    public static ulong Save() { ulong previous=Enabled?0x200UL:0;Enabled=false;return previous; }
    public static void Restore(ulong flags)
    {
        if(Allocated!=0)Published=*(nint*)Allocated==Expected&&(!Array||*(int*)(Allocated+8)==10);
        Enabled=(flags&0x200)!=0;
    }
    public static nint Allocate(uint size)
    {
        if(Throw)throw new InvalidOperationException("fixture allocation failed");
        Allocated=(nint)NativeMemory.AllocZeroed(size);return Allocated;
    }
    public static void Probe(nint value)
    {
        if(Enabled&&(*(nint*)value!=Expected||(Array&&*(int*)(value+8)!=10)))Exposed=true;
    }
}
