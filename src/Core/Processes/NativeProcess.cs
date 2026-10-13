using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Zenith.Core.Packages;
using Zenith.Core.Storage;
using Cosmos.Kernel.Boot.Limine;

namespace Zenith.Core.Processes;

internal sealed unsafe class NativeProcess
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Region { public ulong Start, End; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Context
    {
        public ulong Cr3, Entry, Stack;
        public Region* Regions;
        public ulong Count;
        public byte* Output;
        public ulong Capacity, Length, Status, Ticks, InterruptStack, Hhdm, Fault, Uid, Pid, Gid;
    }
    [DllImport("*", EntryPoint = "zenith_process_run")]
    [SuppressGCTransition]
    private static extern long RunNative(Context* context);
    [DllImport("*", EntryPoint = "_native_cpu_read_cr3")]
    [SuppressGCTransition]
    private static extern ulong ReadCr3();
    private readonly List<nint> _allocations = new(1100);
    private readonly ulong _hhdm;
    private ulong* _root;
    private static int s_busy, s_nextPid;
    private static object[]? s_roots;
    public static bool IsRunning => Volatile.Read(ref s_busy) != 0;
    private NativeProcess()
    {
        _hhdm = Limine.HHDM.Response == null ? 0 : Limine.HHDM.Response->Offset;
        if (_hhdm == 0) { throw new IOException("native process needs an HHDM mapping"); }
    }
    private void Initialize()
    {
        _root = (ulong*)Page();
        var original = (ulong*)((ReadCr3() & 0x000fffffffffF000UL) + _hhdm);
        // Retain supervisor kernel/HHDM mappings; no inherited lower-half mappings.
        for (int i = 256; i < 512; i++) { _root[i] = original[i] & ~4UL; }
    }
    private byte* Allocate(int bytes)
    {
        var raw = (byte*)NativeMemory.Alloc((nuint)bytes);
        if (raw == null) { throw new OutOfMemoryException(); }
        _allocations.Add((nint)raw); new Span<byte>(raw, bytes).Clear(); return raw;
    }
    private byte* Page() => (byte*)(((ulong)Allocate(8192) + 4095) & ~4095UL);
    private ulong Physical(void* address)
    {
        ulong value = (ulong)address;
        if (value < _hhdm || value >= 0xffffffff80000000UL) { throw new IOException("native page is not an HHDM allocation"); }
        return value - _hhdm;
    }
    private void Map(ulong address, byte* frame, bool writable, bool executable)
    {
        var table = _root;
        foreach (int shift in new[] { 39, 30, 21 })
        {
            int index = (int)((address >> shift) & 511);
            if (table[index] == 0) { table[index] = Physical(Page()) | 7; }
            table = (ulong*)((table[index] & 0x000fffffffffF000UL) + _hhdm);
        }
        int leaf = (int)((address >> 12) & 511);
        if (table[leaf] != 0) { throw new IOException("duplicate native virtual page"); }
        table[leaf] = Physical(frame) | 5UL | (writable ? 2UL : 0) | (executable ? 0 : 1UL << 63);
    }
    private void Dispose()
    {
        foreach (nint raw in _allocations) { NativeMemory.Free((void*)raw); }
        _allocations.Clear();
    }
    public static (int Status, string Output, ulong Fault) Run(string name, IReadOnlyList<string> arguments, Action cancel, object? lifetimeRoot = null)
    {
#if ARCH_ARM64
        throw new IOException("native ELF process execution is currently x86_64 only");
#else
        if (!PackageManifest.ValidName(name)) { throw new IOException("invalid native program name"); }
        string path = "/usr/share/zenith/native/" + name + ".elf";
        var store = new NativePackageStore(); var manager = new PackageManager(store);
        var owned = manager.List().SelectMany(package => package.Manifest.Files).FirstOrDefault(file => file.Path == path)
            ?? throw new IOException("native executable must be installed by a signed package");
        if (owned.Mode != 0x1ed) { throw new IOException("native executable requires signed executable permissions"); }
        byte[] bytes = store.Read(path, ElfImage.MaximumBytes);
        if (bytes.Length != owned.Length || PackagePlanner.Digest(bytes) != owned.Digest || store.Mode(path) != owned.Mode)
        { throw new IOException("native executable differs from its installed package"); }
        var image = ElfImage.Read(bytes); var stack = ProcessArguments.Create(arguments);
        lock (Installer.MaintenanceGate)
        {
            if (Installer.IsRunning || Zenith.Core.Shell.Commands.PackageCommands.IsRunning || Zenith.Core.Boot.NativeKernelUpdates.IsRunning
                || Interlocked.CompareExchange(ref s_busy, 1, 0) != 0) { throw new IOException("native process/system maintenance busy"); }
        }
        NativeProcess? process = null;
        try
        {
            cancel(); process = new NativeProcess(); process.Initialize(); var regions = new Region[image.Segments.Count + 1]; int region = 0;
            foreach (var segment in image.Segments)
            {
                for (int page = 0; page < segment.Pages; page++)
                {
                    byte* frame = process.Page(); int offset = page * 4096, count = Math.Min(4096, Math.Max(0, segment.FileBytes - offset));
                    if (count > 0) { bytes.AsSpan(segment.Offset + offset, count).CopyTo(new Span<byte>(frame, 4096)); }
                    process.Map(segment.Address + (ulong)offset, frame, (segment.Flags & 2) != 0, (segment.Flags & 1) != 0);
                }
                regions[region++] = new() { Start = segment.Address, End = segment.Address + (ulong)(segment.Pages * 4096) };
            }
            ulong stackBase = ProcessArguments.Top - ProcessArguments.StackBytes;
            for (int offset = 0; offset < stack.Bytes.Length; offset += 4096)
            {
                byte* frame = process.Page(); stack.Bytes.AsSpan(offset, 4096).CopyTo(new Span<byte>(frame, 4096));
                process.Map(stackBase + (ulong)offset, frame, true, false);
            }
            regions[region] = new() { Start = stackBase, End = ProcessArguments.Top };
            byte* output = process.Allocate(65536);
            // The current managed call stack is suspended while the CPU runs the
            // freestanding image. Keep its owner and allocations explicitly rooted.
            s_roots = new object[] { process, regions, arguments, bytes, image, stack, lifetimeRoot ?? arguments };
            fixed (Region* list = regions)
            {
                var context = new Context { Cr3 = process.Physical(process._root), Entry = image.Entry, Stack = stack.StackPointer,
                    Regions = list, Count = (ulong)regions.Length, Output = output, Capacity = 65536, Ticks = 1000,
                    InterruptStack = 0, Hhdm = process._hhdm, Uid = Zenith.Core.Security.CredentialContext.Current.Uid,
                    Gid = Zenith.Core.Security.CredentialContext.Current.Gid, Pid = (ulong)Interlocked.Increment(ref s_nextPid) };
                int status = (int)RunNative(&context);
                GC.KeepAlive(s_roots);
                string text = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(output, (int)context.Length)); ulong fault = context.Fault;
                process.Dispose(); s_roots = null; Interlocked.Exchange(ref s_busy, 0); cancel(); return (status, text, fault);
            }
        }
        catch { process?.Dispose(); s_roots = null; Interlocked.Exchange(ref s_busy, 0); throw; }
#endif
    }
}
