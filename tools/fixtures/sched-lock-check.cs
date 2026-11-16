// Inspects an adapted Cosmos.Kernel.Core.dll for C5.1 scheduler locking (tools/test_sched_lock_fixup.py).
using Mono.Cecil;
using Mono.Cecil.Cil;

var module = ModuleDefinition.ReadModule(args[0]);
var scheduler = module.GetType("Cosmos.Kernel.Core.Scheduler.SchedulerManager");
int sections = 0, whole = 0, failures = 0;
bool IsCall(Instruction i, string name) => (i.OpCode == OpCodes.Call) && i.Operand is MethodReference m && m.Name == name;
void Check(MethodDefinition method)
{
    var code = method.Body.Instructions;
    foreach (var mask in code.Where(i => IsCall(i, "DisableInterruptsScope")))
    {
        if (!IsCall(mask.Next.Next, "_zenithSchedLock")) { Console.WriteLine("unlocked mask in " + method.Name); failures++; } else { sections++; }
    }
    foreach (var dispose in code.Where(i => IsCall(i, "Dispose") && i.Operand is MethodReference m && m.DeclaringType.Name == "InterruptScope"))
    {
        var load = dispose.Previous;
        if (!(load.Previous?.OpCode == OpCodes.Pop && IsCall(load.Previous.Previous, "_zenithSchedUnlock"))) { Console.WriteLine("restore without unlock in " + method.Name); failures++; }
    }
}
foreach (var name in new[] { "GetBusyCpuTimeNs", "RegisterThread", "CreateThread", "ReadyThread", "BlockThread", "ExitThread", "YieldThread", "MarkSleeping" })
{ Check(scheduler.Methods.Single(m => m.Name == name && m.HasBody)); }
foreach (var name in new[] { "OnTimerInterrupt", "ReschedulePendingFromIrq", "ScheduleFromInterrupt" })
{
    var method = scheduler.Methods.Single(m => m.Name == name && m.HasBody);
    var code = method.Body.Instructions;
    bool ok = IsCall(code[0], "_zenithSchedLock") && code.Where(i => i.OpCode == OpCodes.Ret).All(r => r.Previous.OpCode == OpCodes.Pop && IsCall(r.Previous.Previous, "_zenithSchedUnlock"));
    // No branch may skip the release on its way out.
    ok &= !code.Any(i => i.Operand is Instruction target && target.OpCode == OpCodes.Ret);
    if (ok) { whole++; } else { Console.WriteLine("interrupt path not wrapped: " + name); failures++; }
}
{
    var id = scheduler.Methods.Single(m => m.Name == "AllocateThreadId" && m.HasBody).Body.Instructions;
    if (IsCall(id[0], "_zenithSchedLock")) { whole++; } else { Console.WriteLine("thread id counter not locked"); failures++; }
}
Console.WriteLine("sections=" + sections + " whole=" + whole + " failures=" + failures);
return failures == 0 ? 0 : 1;
