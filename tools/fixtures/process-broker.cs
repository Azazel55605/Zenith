using System;
using System.Runtime.InteropServices;

namespace Zenith.Core
{
    internal static class Log { internal static void Write(string area,string message) { } }
}
namespace Zenith.Core.Multiprocessing
{
    internal static class ManagedCpuJobs
    {
        internal static bool Fail;
        internal static void PumpRequests()
        {
            if (Fail) { Fail=false;throw new InvalidOperationException("collector refused"); }
        }
    }
    internal static partial class CpuWorkers
    {
        private static bool s_stopping=false;
        private static int s_count=2;
        internal static int Run(uint mask,Func<bool> cancelled,out int cpu) => RunProcessSlice(1,mask,cancelled,out cpu);
        // The production wait parks on a monitor pulsed by the kernel loop (CpuWorkers.SliceWait.cs).
        private static void WaitForSlice(ulong token) { if (ProcessWait(token) == 0) System.Threading.Thread.Sleep(1); }
    }
}
internal static class Program
{
    [DllImport("*",EntryPoint="host_reset")] private static extern void Reset();
    [DllImport("*",EntryPoint="host_count")] private static extern int Count(int field);
    private static void Require(bool condition,string message) { if (!condition) throw new Exception(message); }
    public static void Main(string[] args)
    {
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly,(name,assembly,path)=>NativeLibrary.Load(args[0]));
        foreach (string mode in new[]{"normal","cancel","callback","collection","bsp","missing"})
        {
            Reset();int calls=0,cpu=-1,result=0;
            Zenith.Core.Multiprocessing.ManagedCpuJobs.Fail=mode=="collection";
            bool Cancelled()
            {
                calls++;
                if(mode=="callback"&&calls>1)throw new InvalidOperationException("callback failed");
                return mode=="cancel"&&calls>1;
            }
            bool threw=false;
            try { result=Zenith.Core.Multiprocessing.CpuWorkers.Run(mode=="bsp"?1U:mode=="missing"?128U:2U,Cancelled,out cpu); }
            catch (InvalidOperationException) { threw=true; }
            if(mode is "bsp" or "missing")
            {
                Require(result==(mode=="bsp"?-1:95)&&!threw&&Count(0)==0,"strict admission/fallback");continue;
            }
            Require(Count(0)==1&&Count(1)==1&&Count(2)==3,"terminal generation must reap despite contention: "+mode);
            Require(Count(3)==0,"borrowed context still live after broker returns: "+mode);
            Require(threw==(mode is "callback" or "collection"),"exception identity: "+mode);
            if(!threw)Require(cpu==1&&result==(mode=="cancel"?130:4096),"completion: "+mode);
        }
        Console.WriteLine("PASS protected broker: contended reap, callback/GC exception drainage, cancellation and strict affinity");
    }
}
