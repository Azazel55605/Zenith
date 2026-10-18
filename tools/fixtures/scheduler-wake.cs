// Run the real pinned wake/scheduler code. Only privileged IRQ and UART I/O
// are replaced in a separate host-only copy; queue/state/accounting stay real.
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;

string host = Path.Combine(Path.GetDirectoryName(args[0])!, "host-" + Path.GetFileName(args[0]));
using (var source = AssemblyDefinition.ReadAssembly(args[0]))
{
    var module = source.MainModule;
    var native = module.GetType("Cosmos.Kernel.Core.Bridge.CpuNative");
    var flags = new FieldDefinition("_hostIrqFlags", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, module.TypeSystem.UInt64);
    native.Fields.Add(flags);
    foreach (string name in new[] { "SaveIrqAndDisable", "RestoreIrq" })
    {
        var counter = new FieldDefinition("_host" + name, Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, module.TypeSystem.Int32);
        native.Fields.Add(counter);
        var method = native.Methods.Single(m => m.Name == name);
        method.PInvokeInfo = null; method.IsPInvokeImpl = false;
        method.ImplAttributes = Mono.Cecil.MethodImplAttributes.IL | Mono.Cecil.MethodImplAttributes.Managed;
        method.Body = new Mono.Cecil.Cil.MethodBody(method);
        foreach (var instruction in new[] { Instruction.Create(OpCodes.Ldsfld, counter), Instruction.Create(OpCodes.Ldc_I4_1),
            Instruction.Create(OpCodes.Add), Instruction.Create(OpCodes.Stsfld, counter) }) { method.Body.Instructions.Add(instruction); }
        if (name == "SaveIrqAndDisable")
        {
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, flags));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I8, 0L));
        }
        else { method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); }
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, flags));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }
    var context = module.GetType("Cosmos.Kernel.Core.Bridge.ContextSwitchNative");
    foreach (string name in new[] { "SetContextSwitchSp", "SetContextSwitchNewThread" })
    {
        var method = context.Methods.Single(m => m.Name == name);
        var value = new FieldDefinition("_host" + name, Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, method.Parameters[0].ParameterType);
        context.Fields.Add(value);
        method.PInvokeInfo = null; method.IsPInvokeImpl = false;
        method.ImplAttributes = Mono.Cecil.MethodImplAttributes.IL | Mono.Cecil.MethodImplAttributes.Managed;
        method.Body = new Mono.Cecil.Cil.MethodBody(method);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, value));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }
    if (args[2].StartsWith("condition-"))
    {
        var cpuType = module.GetType("Cosmos.Kernel.Core.CPU.InternalCpu");
        var counter = new FieldDefinition("TestHalts", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, module.TypeSystem.Int32);
        cpuType.Fields.Add(counter);
        var testThread = new FieldDefinition("TestThread", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static,
            module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerThread"));
        cpuType.Fields.Add(testThread);
        var testCondition = new FieldDefinition("TestCondition", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static,
            module.GetType("Cosmos.Kernel.Core.Scheduler.ConditionVariable"));
        cpuType.Fields.Add(testCondition);
        var halt = cpuType.Methods.Single(m => m.Name == "Halt");
        halt.Body = new Mono.Cecil.Cil.MethodBody(halt);
        var il = halt.Body.GetILProcessor();
        var done = Instruction.Create(OpCodes.Ret);
        il.Append(Instruction.Create(OpCodes.Ldsfld,counter)); il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Add)); il.Append(Instruction.Create(OpCodes.Stsfld,counter));
        il.Append(Instruction.Create(OpCodes.Ldsfld,counter)); il.Append(Instruction.Create(OpCodes.Ldc_I4_2));
        il.Append(Instruction.Create(OpCodes.Blt,done));
        if (args[2] == "condition-signal")
        {
            il.Append(Instruction.Create(OpCodes.Ldsfld,testCondition));
            il.Append(Instruction.Create(OpCodes.Callvirt,module.GetType("Cosmos.Kernel.Core.Scheduler.ConditionVariable").Methods.Single(m => m.Name == "Signal")));
        }
        else
        {
            il.Append(Instruction.Create(OpCodes.Ldc_I4_0)); il.Append(Instruction.Create(OpCodes.Ldsfld,testThread));
            il.Append(Instruction.Create(OpCodes.Call,module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerManager").Methods.Single(m => m.Name == "ReadyThread")));
        }
        il.Append(done);
    }
    foreach (var method in module.GetType("Cosmos.Kernel.Core.IO.Serial").Methods.Where(m => m.Name.StartsWith("Write") && m.ReturnType.FullName == "System.Void"))
    {
        method.Body = new Mono.Cecil.Cil.MethodBody(method);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }
    source.Write(host);
}
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    string path = Path.Combine(args[1], name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(host);
var nativeType = assembly.GetType("Cosmos.Kernel.Core.Bridge.CpuNative", true)!;
nativeType.GetField("_hostIrqFlags")!.SetValue(null, 512UL);
Type TypeOf(string name) => assembly.GetType("Cosmos.Kernel.Core.Scheduler." + name, true)!;
object Blank(string name) => RuntimeHelpers.GetUninitializedObject(TypeOf(name));
void Set(object target, string property, object value) => target.GetType()
    .GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
object idle = Blank("SchedulerThread"), thread = Blank("SchedulerThread"), cpu = Blank("PerCpuState");
var currentData = Activator.CreateInstance(TypeOf("Stride.StrideThreadData"), true)!;
Set(currentData, "Tickets", 100UL); Set(currentData, "Stride", 10485UL);
Set(thread, "SchedulerData", currentData);
int initial = args[2] switch { "created" => 0, "ready" => 1, "running" => 2, "blocked" => 3, "dead" => 5, _ => 4 };
Set(thread, "State", Enum.ToObject(TypeOf("SchedulerThreadState"), initial));
Set(idle, "State", Enum.ToObject(TypeOf("SchedulerThreadState"), 2));
var data = Activator.CreateInstance(TypeOf("Stride.StrideCpuData"), true)!;
var queue = (System.Collections.IList)data.GetType().GetProperty("RunQueue")!.GetValue(data)!;
Set(data, "TotalTickets", initial == 2 ? 200UL : 100UL);
Set(data, "LastPassUpdate", (ulong)Stopwatch.GetTimestamp());
if (initial == 1) { queue.Add(thread); Set(data, "TotalTickets", 200UL); }
Set(cpu, "IdleThread", idle); Set(cpu, "CurrentThread", initial == 2 || (args[2].StartsWith("wake-current") || args[2].StartsWith("dispatch-")) ? thread : idle); Set(cpu, "SchedulerData", data);
var stride = TypeOf("Stride.StrideScheduler");
stride.GetField("s_ticksPerQuantum", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, 10000000UL);
object scheduler = Activator.CreateInstance(stride, true)!;
var manager = TypeOf("SchedulerManager");
void Static(string field, object value) => manager.GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, value);
var cpus = Array.CreateInstance(TypeOf("PerCpuState"), 1); cpus.SetValue(cpu, 0);
Static("s_cpuStates", cpus); Static("s_cpuCount", 1U); Static("s_enabled", true); Static("s_currentScheduler", scheduler);
var wake = manager.GetMethod("ReadyThread", BindingFlags.Static | BindingFlags.NonPublic)!;
object? condition = null;
if (args[2] == "late-signal")
{
    condition = Activator.CreateInstance(TypeOf("ConditionVariable"), true)!;
    var waiters = (System.Collections.IList)condition.GetType().GetField("_waitingThreads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(condition)!;
    waiters.Add(thread);
}
if (args[2].StartsWith("condition-"))
{
    Set(cpu,"CurrentThread",thread);
    Set(thread,"State",Enum.ToObject(TypeOf("SchedulerThreadState"),2));
    Set(data,"TotalTickets",200UL);
    condition = Activator.CreateInstance(TypeOf("ConditionVariable"),true)!;
    object mutex = Activator.CreateInstance(TypeOf("Mutex"),true)!;
    var cpuType = assembly.GetType("Cosmos.Kernel.Core.CPU.InternalCpu",true)!;
    cpuType.GetField("TestThread")!.SetValue(null,thread);
    cpuType.GetField("TestCondition")!.SetValue(null,condition);
    mutex.GetType().GetMethod("Acquire")!.Invoke(mutex,null);
    object? signaled = null;
    int repetitions = args[2] == "condition-repeat" ? 100 : args[2] == "condition-double" ? 2 : 1;
    for (int iteration = 0; iteration < repetitions; iteration++)
    {
        cpuType.GetField("TestHalts")!.SetValue(null,0);
        signaled = condition.GetType().GetMethod(args[2] == "condition-signal" ? "Wait" : "WaitTimeout")!.Invoke(condition,
            args[2] == "condition-signal" ? new[] {mutex} : new object[] {mutex,1U});
        if (repetitions == 1) break;
        if (Convert.ToInt32(thread.GetType().GetProperty("State")!.GetValue(thread)) != 1)
        {
            if (args[2] == "condition-double") continue;
            Console.WriteLine("not-woken-at="+iteration); return;
        }
        manager.GetMethod("ScheduleFromInterrupt",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{0U,(nuint)0x2000});
    }
    Console.WriteLine("halts="+cpuType.GetField("TestHalts")!.GetValue(null)
        +";state="+Convert.ToInt32(thread.GetType().GetProperty("State")!.GetValue(thread))
        +";result="+signaled+";tickets="+data.GetType().GetProperty("TotalTickets")!.GetValue(data)
        +";irqFlags="+nativeType.GetField("_hostIrqFlags")!.GetValue(null));
    return;
}
wake.Invoke(null, new object[] { 0U, thread });
if (args[2].StartsWith("dispatch-"))
{
    Set(thread, "StackPointer", (nuint)0x1000);
    if (args[2] == "dispatch-repeat")
    {
        for (int iteration = 0; iteration < 100; iteration++)
        {
            manager.GetMethod("ScheduleFromInterrupt", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { 0U, (nuint)0x2000 });
            if (Convert.ToInt32(thread.GetType().GetProperty("State")!.GetValue(thread)) != 2)
            { Console.WriteLine("not-running-at=" + iteration); return; }
            if (iteration < 99)
            {
                manager.GetMethod("MarkSleeping", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { 0U, thread, 1U });
                wake.Invoke(null, new object[] { 0U, thread });
            }
        }
    }
    else if (args[2] is "dispatch-other" or "dispatch-new")
    {
        object other = Blank("SchedulerThread");
        var otherData = Activator.CreateInstance(TypeOf("Stride.StrideThreadData"), true)!;
        Set(otherData,"Tickets",100UL); Set(otherData,"Stride",10485UL); Set(otherData,"Pass",long.MinValue+100);
        Set(other,"SchedulerData",otherData); Set(other,"State",Enum.ToObject(TypeOf("SchedulerThreadState"), args[2] == "dispatch-new" ? 0 : 1));
        Set(other,"StackPointer",(nuint)0x3000); queue.Insert(0,other); Set(data,"TotalTickets",300UL);
    }
    else if (args[2].StartsWith("dispatch-preserve-"))
    { Set(thread,"State",Enum.ToObject(TypeOf("SchedulerThreadState"),int.Parse(args[2].Split('-')[2]))); }
    var name = args[2] == "dispatch-voluntary" ? "Schedule" : "ScheduleFromInterrupt";
    if (args[2] != "dispatch-repeat")
    {
        manager.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
            name == "Schedule" ? new object[] { 0U } : new object[] { 0U, (nuint)0x2000 });
    }
    var context = assembly.GetType("Cosmos.Kernel.Core.Bridge.ContextSwitchNative", true)!;
    Console.WriteLine("state=" + Convert.ToInt32(thread.GetType().GetProperty("State")!.GetValue(thread))
        + ";queue=" + queue.Count + ";tickets=" + data.GetType().GetProperty("TotalTickets")!.GetValue(data)
        + ";stack=" + thread.GetType().GetProperty("StackPointer")!.GetValue(thread)
        + ";switchSp=" + context.GetField("_hostSetContextSwitchSp")!.GetValue(null)
        + ";new=" + context.GetField("_hostSetContextSwitchNewThread")!.GetValue(null));
    return;
}
long firstPass = (long)currentData.GetType().GetProperty("Pass")!.GetValue(currentData)!;
if (args[2].StartsWith("wake-current"))
{
    // A one-tick sleep expires while this is still CurrentThread. ReadyThread
    // queues it; switching to another worker then yields the already queued one.
    if (args[2] == "wake-current-order")
    {
        object competitor = Blank("SchedulerThread");
        var competitorData = Activator.CreateInstance(TypeOf("Stride.StrideThreadData"), true)!;
        Set(competitorData, "Pass", firstPass + 20);
        Set(competitor, "SchedulerData", competitorData); queue.Add(competitor);
        Set(currentData, "Pass", firstPass + 40);
    }
    stride.GetMethod("OnThreadYield")!.Invoke(scheduler, new object[] { cpu, thread });
}
else if (condition is null) { wake.Invoke(null, new object[] { 0U, thread }); }
else { condition.GetType().GetMethod("Signal")!.Invoke(condition, null); }
long lastPass = (long)currentData.GetType().GetProperty("Pass")!.GetValue(currentData)!;
Console.WriteLine("queue=" + queue.Count + ";tickets=" + data.GetType().GetProperty("TotalTickets")!.GetValue(data)
    + ";state=" + Convert.ToInt32(thread.GetType().GetProperty("State")!.GetValue(thread))
    + ";irq=" + nativeType.GetField("_hostSaveIrqAndDisable")!.GetValue(null) + "/" + nativeType.GetField("_hostRestoreIrq")!.GetValue(null)
    + ";irqFlags=" + nativeType.GetField("_hostIrqFlags")!.GetValue(null)
    + ";passPreserved=" + (firstPass == lastPass));

if (args[2] == "wake-current-order")
{
    long[] passes = queue.Cast<object>().Select(t => (long)currentData.GetType().GetProperty("Pass")!.GetValue(t.GetType().GetProperty("SchedulerData")!.GetValue(t))!).ToArray();
    Console.WriteLine("sorted=" + passes.SequenceEqual(passes.OrderBy(p => p)));
}
