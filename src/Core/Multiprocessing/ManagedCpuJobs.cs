using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Zenith.Core.Security;

namespace Zenith.Core.Multiprocessing;

// Fixed kernel-owned jobs only. No arbitrary delegates, service calls, GUI
// mutation, thread creation, blocking monitors or migration may run on an AP.
internal static unsafe class ManagedCpuJobs
{
    [ThreadStatic] private static ulong t_identity;
    private static bool s_ready;
    private static ulong s_lastDeferred;
    [DllImport("*", EntryPoint = "zenith_managed_cpu_contract"), SuppressGCTransition]
    private static extern ulong Contract();
    [DllImport("*", EntryPoint = "zenith_exception_prepare"), SuppressGCTransition]
    private static extern void PrepareExceptionState();
    [DllImport("*", EntryPoint = "zenith_managed_cpu_attach"), SuppressGCTransition]
    private static extern ulong Attach(ulong count);
    [DllImport("*", EntryPoint = "zenith_cpu_managed_callback"), SuppressGCTransition]
    private static extern ulong Register(ulong callback);
    [DllImport("*", EntryPoint = "zenith_cpu_managed_trace"), SuppressGCTransition]
    private static extern ulong Trace(ulong token, ulong phase);
    [DllImport("*", EntryPoint = "zenith_cpu_managed_checkpoint"), SuppressGCTransition]
    private static extern ulong Checkpoint(ulong token);
    [DllImport("*", EntryPoint = "zenith_gc_state"), SuppressGCTransition]
    internal static extern ulong State(ulong field);
    [DllImport("*", EntryPoint = "zenith_cpu_managed_pixels"), SuppressGCTransition]
    private static extern ulong Pixels(ulong token, uint* buffer, ulong length, ulong publish);
    [DllImport("*", EntryPoint = "zenith_cpu_managed_cancel_word"), SuppressGCTransition]
    private static extern ulong* Cancellation(ulong token);
    private static bool IsCancelled(ulong* word) => word != null && System.Threading.Volatile.Read(ref *word) != 0;
    [DllImport("*", EntryPoint = "zenith_cpu_managed_fault"), SuppressGCTransition]
    private static extern ulong Fault(ulong token);
    internal static bool Ready => s_ready;

    internal static void CopyFrame(ulong token, uint[] frame)
    {
        fixed (uint* p = frame)
            if (Pixels(token, p, (ulong)frame.Length, 0) != 1) throw new InvalidOperationException("managed frame not complete/owned");
    }

    internal static uint[] BuildImage(int count, uint seed, ulong token = 0)
    {
        ulong* cancel = token == 0 ? null : Cancellation(token);
        if (token != 0 && cancel == null) throw new InvalidOperationException("managed cancellation owner unavailable");
        uint[] frame = new uint[count];
        for (uint p = 0; p < (uint)count; p++) frame[p] = 0xff000000 | ((seed * 33 + p * 17) & 0xffffff);
        for (uint round = 0; round < 32; round++)
        {
            uint tint = 0x203040 + ((round + seed) & 15) * 0x010101;
            for (int p = 0; p < count; p++)
            {
                if ((p & 255) == 0 && IsCancelled(cancel)) return frame;
                uint value = frame[p];
                uint r = (((value >> 16) & 255) * 191 + ((tint >> 16) & 255) * 64 + 127) / 255;
                uint g = (((value >> 8) & 255) * 191 + ((tint >> 8) & 255) * 64 + 127) / 255;
                uint b = ((value & 255) * 191 + (tint & 255) * 64 + 127) / 255;
                frame[p] = 0xff000000 | (r << 16) | (g << 8) | b;
            }
        }
        if (token != 0)
        {
            fixed (uint* p = frame)
                if (Pixels(token, p, (ulong)count, 1) != 1) throw new InvalidOperationException("managed frame publication refused");
        }
        return frame;
    }

    internal static void Initialize(ulong count)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new PlatformNotSupportedException("managed AP jobs require x86_64");
        if (s_ready) return;
        if (Contract()!=1) throw new InvalidOperationException("incomplete managed CPU runtime contract");
        // Run all job/type/TLS paths on BSP before any AP admission. No delegate
        // or managed reference is published into the native descriptor.
        PrepareExceptionState();
        _ = Compute(0, 4, 17, 1, 0);
        _ = Compute(0, 4, 17, 4, 0);
        try { _ = Compute(0, 4, 17, 2, 0); } catch { }
        if (Attach(count) != 1) throw new InvalidOperationException("pinned managed CPU owner setup refused");
        if (Register((ulong)(delegate* unmanaged<ulong, ulong, ulong, ulong, ulong, ulong>)&Entry) != 1)
            throw new InvalidOperationException("pinned managed CPU callback setup refused");
        s_ready = true;
    }

    internal static void PumpRequests()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        if (!s_ready) return;
        ulong requests = State(4);
        if (requests == s_lastDeferred) return;
        s_lastDeferred = requests;
        GC.Collect();
    }

    [UnmanagedCallersOnly]
    private static ulong Entry(ulong cpu, ulong count, ulong seed, ulong operation, ulong token)
    {
        try { Trace(token, 10); PrepareExceptionState(); Trace(token, 20); return Compute(cpu, count, seed, operation, token); }
        catch (Exception error) { Trace(token, 1000000UL + (uint)(error.HResult & 0xffff)); return ulong.MaxValue; }
    }

    private sealed class RootNode
    {
        internal RootNode? Next;
        internal byte[] Data;
        internal RootNode(RootNode? next, byte value)
        {
            Next = next; Data = new byte[128]; Data[0] = value; Data[127] = (byte)(value ^ 0x5a);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte* MakeInterior(byte seed)
    {
        byte[] bytes = new byte[1024]; bytes[17] = seed; bytes[1023] = (byte)(seed ^ 0x3c);
        fixed (byte* pointer = bytes) return pointer + 17;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static ulong Compute(ulong cpu, ulong count, ulong seed, ulong operation, ulong token)
    {
        if (cpu != 0 && CredentialContext.Current.IsRoot) return ulong.MaxValue;
        if (token != 0) Trace(token, 30);
        t_identity = seed ^ (cpu << 32);
        if (token != 0) Trace(token, 40);
        if (operation == 2)
        {
            try { throw new InvalidOperationException("contained pinned worker exception"); }
            catch (Exception) { return t_identity; }
        }
        if (operation == 5) throw new InvalidOperationException("reported pinned worker failure");
        if (operation == 4)
        {
            uint[] frame = BuildImage((int)count, (uint)seed, token);
            ulong imageHash = 0xcbf29ce484222325UL;
            foreach (uint pixel in frame) imageHash = (imageHash ^ pixel) * 0x100000001b3UL;
            return imageHash;
        }
        ulong* cancel = token == 0 ? null : Cancellation(token);
        if (token != 0 && cancel == null) return ulong.MaxValue;
        RootNode? root = null;
        for (ulong i = 0; i < 64; i++) root = new RootNode(root, (byte)(seed + i));
        byte* interior = MakeInterior((byte)seed);
        byte[] pinned = GC.AllocateUninitializedArray<byte>(1024, pinned: true);
        pinned[0] = (byte)seed; pinned[1023] = (byte)(seed ^ 0xa5);
        if (operation == 3) GC.Collect(); // BSP must service the deferred request.
        if (token != 0) Trace(token, 50);
        if (token != 0 && Checkpoint(token) != 1) return 0;
        if (operation == 6 && token != 0) return Fault(token);
        ulong checksum = 0xcbf29ce484222325UL;
        for (ulong i = 0; i < count; i++)
        {
            if ((i & 255) == 0 && IsCancelled(cancel)) return 0;
            var temporary = new byte[256]; temporary[0] = (byte)(seed + i);
            checksum = (checksum ^ temporary[0]) * 0x100000001b3UL;
            // Keep a chain and interior array data live across allocation and
            // asynchronously captured stop epochs, then validate every node.
        }
        for (ulong i = 64; i != 0; i--)
        {
            if (root is null || root.Data[0] != (byte)(seed + i - 1) || root.Data[127] != (byte)((seed + i - 1) ^ 0x5a)) return ulong.MaxValue;
            root = root.Next;
        }
        if (*interior != (byte)seed || interior[1006] != (byte)(seed ^ 0x3c)
            || pinned[0] != (byte)seed || pinned[1023] != (byte)(seed ^ 0xa5)) return ulong.MaxValue;
        if (t_identity != (seed ^ (cpu << 32))) return ulong.MaxValue;
        return checksum;
    }
}
