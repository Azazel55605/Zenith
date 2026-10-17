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
using Zenith.Core.IO;

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
        public ulong Resume, Call, Arg0, Arg1, Arg2;
        public long Result;
        public byte* Frame;
        public byte* UserFp;
    }
    [DllImport("*", EntryPoint = "zenith_process_run")]
    [SuppressGCTransition]
    private static extern long RunNative(Context* context);
    [DllImport("*", EntryPoint = "_native_cpu_read_cr3")]
    [SuppressGCTransition]
    private static extern ulong ReadCr3();
    private readonly List<nint> _allocations = new(1100);
    private readonly Dictionary<ulong, nint> _pages = new();
    private readonly List<ProcessMemoryRange> _ranges = new();
    private readonly ulong _hhdm;
    private ulong* _root;
    private static int s_busy, s_nextPid;
    private const int MaximumProcesses = 8;
    private static readonly object s_rawGate = new(), s_rootsGate = new(), s_serviceGate = new();
    private static readonly Dictionary<int, object[]> s_roots = new();
    private static void Release(int pid, ref bool released)
    {
        if (released) { return; }
        released = true;
        lock (s_rootsGate) { s_roots.Remove(pid); }
        Interlocked.Decrement(ref s_busy);
    }
    private static void CloseFiles(ProcessFiles? files)
    {
        Monitor.Enter(s_serviceGate);
        try { files?.Dispose(); Monitor.Exit(s_serviceGate); }
        catch { Monitor.Exit(s_serviceGate); throw; }
    }
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
        _pages.Add(address, (nint)frame);
        int leaf = (int)((address >> 12) & 511);
        if (table[leaf] != 0) { throw new IOException("duplicate native virtual page"); }
        table[leaf] = Physical(frame) | 5UL | (writable ? 2UL : 0) | (executable ? 0 : 1UL << 63);
    }
    private void Dispose()
    {
        foreach (nint raw in _allocations) { NativeMemory.Free((void*)raw); }
        _allocations.Clear();
    }
    private bool Valid(ulong address, int count, bool write) => ProcessMemoryRange.Allows(_ranges, address, (ulong)count, write);
    private byte[] ReadUser(ulong address, int count)
    {
        var buffer = new byte[count];
        Transfer(address, buffer, false); return buffer;
    }
    private void Transfer(ulong address, byte[] buffer, bool write)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int within = (int)(address & 4095), count = Math.Min(4096 - within, buffer.Length - offset);
            var page = new Span<byte>((byte*)_pages[address & ~4095UL] + within, count);
            if (write) { buffer.AsSpan(offset, count).CopyTo(page); } else { page.CopyTo(buffer.AsSpan(offset, count)); }
            address += (ulong)count; offset += count;
        }
    }
    private long Service(ref Context c, ProcessFiles files, ref int readBytes)
    {
        try
        {
            if (c.Call == 400) { return c.Arg0 > int.MaxValue ? -9 : files.IsTerminal((int)c.Arg0); }
            if (c.Call == 3) { return c.Arg0 > int.MaxValue ? -9 : files.Close((int)c.Arg0); }
            if (c.Call == 74) { return c.Arg0 > int.MaxValue ? -9 : files.Flush((int)c.Arg0); }
            if (c.Call == 1)
            {
                if (c.Arg0 > int.MaxValue) { return -9; }
                if (c.Arg2 > ProcessFiles.MaximumIo) { return -22; }
                if (!Valid(c.Arg1, (int)c.Arg2, false)) { return -14; }
                return files.Write((int)c.Arg0, ReadUser(c.Arg1, (int)c.Arg2));
            }
            if (c.Call == 79)
            {
                byte[] cwd = Encoding.UTF8.GetBytes(files.Cwd + "\0");
                if (c.Arg1 < (ulong)cwd.Length) { return -34; }
                if (!Valid(c.Arg0, cwd.Length, true)) { return -14; }
                Transfer(c.Arg0, cwd, true); return cwd.Length;
            }
            if (c.Call == 2 || c.Call == 4)
            {
                if (c.Arg1 == 0 || c.Arg1 > 1024) { return -36; }
                if (!Valid(c.Arg0, (int)c.Arg1, false)) { return -14; }
                string path = new UTF8Encoding(false, true).GetString(ReadUser(c.Arg0, (int)c.Arg1));
                if (c.Call == 2) { return files.Open(path, c.Arg2); }
                if (!Valid(c.Arg2, ProcessFiles.StatBytes, true)) { return -14; }
                byte[] stat = new byte[ProcessFiles.StatBytes]; files.Stat(path, stat); Transfer(c.Arg2, stat, true); return 0;
            }
            if (c.Call == 0 || c.Call == 217)
            {
                if (c.Arg0 > int.MaxValue) { return -9; }
                if (c.Arg2 > ProcessFiles.MaximumIo) { return -22; }
                int count = (int)c.Arg2;
                if (!Valid(c.Arg1, count, true)) { return -14; }
                bool stdin = c.Call == 0 && c.Arg0 == 0;
                if (!stdin && readBytes + count > ProcessFiles.MaximumTransfer) { return -28; }
                byte[] buffer = new byte[count];
                int result = c.Call == 0 ? files.Read((int)c.Arg0, buffer) : files.Next((int)c.Arg0, buffer);
                if (result > 0) { if (!stdin) { readBytes += result; } Transfer(c.Arg1, buffer.AsSpan(0, result).ToArray(), true); }
                return result;
            }
            return -38;
        }
        catch (Exception error)
        {
            // Cosmos NativeAOT typed catches are unreliable; classify explicitly.
            if (error is OperationCanceledException) { throw; }
            if (error is BrokenPipeException) { return -32; }
            if (error is UnauthorizedAccessException) { return -13; }
            if (error is FileNotFoundException || error is DirectoryNotFoundException) { return -2; }
            if (error is ArgumentException) { return -22; }
            return -5;
        }
    }
    public static (int Status, string Error, ulong Fault) Run(string name, IReadOnlyList<string> arguments, Func<bool> cancelled, object? lifetimeRoot = null, string cwd = "/", string? input = null, ProcessStreams? streams = null)
    {
#if ARCH_ARM64
        throw new IOException("native ELF process execution is currently x86_64 only");
#else
        // Cosmos exception dispatch is not safe for overlapping managed unwinds.
        // Serialize expected setup/VFS errors, converting them before releasing the
        // gate. Never hold this gate across blocking standard-stream operations.
        byte[] bytes = Array.Empty<byte>(); ElfImage image = null!; ProcessArguments stack = null!;
        string? setupError = null;
        Monitor.Enter(s_serviceGate);
        try
        {
            if (!PackageManifest.ValidName(name)) { throw new IOException("invalid native program name"); }
            string path = "/usr/share/zenith/native/" + name + ".elf";
            var store = new NativePackageStore(); var manager = new PackageManager(store);
            var owned = manager.List().SelectMany(package => package.Manifest.Files).FirstOrDefault(file => file.Path == path)
                ?? throw new IOException("native executable must be installed by a signed package");
            if (owned.Mode != 0x1ed) { throw new IOException("native executable requires signed executable permissions"); }
            bytes = store.Read(path, ElfImage.MaximumBytes);
            if (bytes.Length != owned.Length || PackagePlanner.Digest(bytes) != owned.Digest || store.Mode(path) != owned.Mode)
            { throw new IOException("native executable differs from its installed package"); }
            image = ElfImage.Read(bytes); stack = ProcessArguments.Create(arguments);
        }
        catch (Exception error) { setupError = error.Message; }
        Monitor.Exit(s_serviceGate);
        if (setupError is not null) { return (1, setupError, 0); }
        bool admitted; int pid = 0;
        lock (Installer.MaintenanceGate)
        {
            admitted = !Installer.IsRunning && !Zenith.Core.Shell.Commands.PackageCommands.IsRunning
                && !Zenith.Core.Boot.NativeKernelUpdates.IsRunning && Volatile.Read(ref s_busy) < MaximumProcesses;
            if (admitted) { Interlocked.Increment(ref s_busy); pid = Interlocked.Increment(ref s_nextPid); }
        }
        if (!admitted) { return (1, "native process/system maintenance busy (maximum 8 contexts)", 0); }
        bool released = false;
        NativeProcess? process = null; ProcessFiles? files = null;
        var identity = Zenith.Core.Security.CredentialContext.Current;
        try
        {
            if (cancelled()) { Release(pid, ref released); return (130, "", 0); }
            process = new NativeProcess(); process.Initialize(); var regions = new Region[image.Segments.Count + 1]; int region = 0;
            foreach (var segment in image.Segments)
            {
                process._ranges.Add(new(segment.Address, segment.Address + (ulong)(segment.Pages * 4096), (segment.Flags & 2) != 0));
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
            process._ranges.Add(new(stackBase, ProcessArguments.Top, true));
            files = new ProcessFiles(new NativeProcessFiles(), cwd, input, streams);
            // The current managed call stack is suspended while the CPU runs the
            // freestanding image. Keep its owner and allocations explicitly rooted.
            lock (s_rootsGate) { s_roots.Add(pid, new object[] { process, regions, arguments, bytes, image, stack, lifetimeRoot ?? arguments, files }); }
            fixed (Region* list = regions)
            {
                var context = new Context { Cr3 = process.Physical(process._root), Entry = image.Entry, Stack = stack.StackPointer,
                    Regions = list, Count = (ulong)regions.Length, Ticks = 1000,
                    InterruptStack = 0, Hhdm = process._hhdm, Uid = Zenith.Core.Security.CredentialContext.Current.Uid,
                    Gid = Zenith.Core.Security.CredentialContext.Current.Gid, Pid = (ulong)pid,
                    Frame = process.Allocate(176), UserFp = process.Page() };
                int status; int calls = 0, readBytes = 0;
                do
                {
                    if (cancelled()) { status = 130; break; }
                    Monitor.Enter(s_rawGate);
                    try { status = (int)RunNative(&context); Monitor.Exit(s_rawGate); }
                    catch { Monitor.Exit(s_rawGate); throw; }
                    if (status != 4096) { break; }
                    if (cancelled()) { status = 130; break; }
                    if (++calls > 4096) { status = 124; break; }
                    var caller = Zenith.Core.Security.CredentialContext.Enter(identity);
                    bool standard = context.Call == 400 || ((context.Call is 0 or 1 or 3) && context.Arg0 <= 2);
                    if (!standard) { Monitor.Enter(s_serviceGate); }
                    try
                    {
                        context.Result = process.Service(ref context, files, ref readBytes);
                        if (!standard) { Monitor.Exit(s_serviceGate); }
                        caller.Dispose();
                    }
                    catch
                    {
                        if (!standard) { Monitor.Exit(s_serviceGate); }
                        caller.Dispose(); throw;
                    }
                    // Give console/cancellation workers a scheduling point during
                    // syscall-heavy loops; Thread.Yield is a stub in Cosmos 3.0.89.
                    if ((calls & 15) == 0) { Thread.Sleep(1); }
                } while (true);
                GC.KeepAlive(s_roots);
                ulong fault = context.Fault;
                CloseFiles(files); process.Dispose(); Release(pid, ref released); return (cancelled() ? 130 : status, "", fault);
            }
        }
        catch { CloseFiles(files); process?.Dispose(); Release(pid, ref released); throw; }
#endif
    }
}
