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
using Zenith.Core.Security;

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
    private readonly List<ProcessMemoryRange> _ranges = new();
    private readonly ulong _hhdm;
    private ulong* _root;
    private byte[]? _processSnapshot;
    private byte[]? _graphicsTransfer;
    // X5's 256 MiB image window crosses the legacy framebuffer base.
    private ulong FramebufferAddress => _largeNative ? 0x20000000UL : 0x10000000UL;
    private bool _largeNative;
    private int _framebufferPages, _framebufferRange = -1;
    private long Framebuffer(ref Context c, ulong bytes)
    {
        if (bytes == 0 || bytes > (ulong)ProcessSurface.MaximumPixels * 4) { return -22; }
        int pages = (int)((bytes + 4095) / 4096);
        if (pages > _framebufferPages && Cosmos.Kernel.System.Diagnostics.MemoryInfo.FreePages < (ulong)(pages - _framebufferPages) + 8192) { return -12; }
        for (; _framebufferPages < pages; _framebufferPages++)
        { Map(FramebufferAddress + (ulong)_framebufferPages * 4096, Page(), true, false); }
        var range = new ProcessMemoryRange(FramebufferAddress, FramebufferAddress + (ulong)_framebufferPages * 4096, true);
        if (_framebufferRange < 0) { _framebufferRange = _ranges.Count; _ranges.Add(range); }
        else { _ranges[_framebufferRange] = range; }
        c.Regions[c.Count - 1] = new Region { Start = range.Start, End = range.End };
        return (long)FramebufferAddress;
    }
    private byte[] ReadGraphics(ulong address, int size)
    {
        if (_graphicsTransfer is null || _graphicsTransfer.Length != size) { _graphicsTransfer = new byte[size]; }
        Transfer(address, _graphicsTransfer, false); return _graphicsTransfer;
    }
    // Process.s: a user-window device IRQ was forwarded to its original handler.
    private const int DeviceInterruptYield = 4097;
    internal static readonly ProcessTable Processes = new();
    private static readonly object s_rawGate = new(), s_rootsGate = new(), s_serviceGate = new();
    private static readonly Dictionary<int, object[]> s_roots = new();
    private static void Release(int pid, ref bool released, int status)
    {
        if (released) { return; }
        released = true;
        lock (s_rootsGate) { s_roots.Remove(pid); }
        Zenith.Core.Portal.PortalBroker.ProcessExited(pid);
        Zenith.Core.Portal.ProcessPrivileges.Drop(pid);
        Zenith.Core.Files.FileJobs.ProcessExited(pid);
        Zenith.Core.Configuration.SystemConfiguration.Display.Abandon(pid);
        Zenith.Core.Audio.AudioService.Release(pid);
        Processes.Complete(pid, status);
    }
    internal static string ProgramNameOf(int pid) => ProgramName(pid);
    private static string ProgramName(int pid)
    {
        foreach (var p in Processes.List(0)) { if (p.Pid == pid) { return p.Name; } }
        return "pid " + pid;
    }
    private static void CloseFiles(ProcessFiles? files)
    {
        Monitor.Enter(s_serviceGate);
        try { files?.Dispose(); Monitor.Exit(s_serviceGate); }
        catch { Monitor.Exit(s_serviceGate); throw; }
    }
    public static bool IsRunning => Processes.BlockingCount != 0;
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
    // Pages come from aligned runs: aligning each one alone wasted as much memory as it used.
    private const int ArenaPages = 64;
    private byte* _arena;
    private int _arenaLeft;
    private byte* Page()
    {
        if (_arenaLeft == 0)
        {
            if (_largeNative && Cosmos.Kernel.System.Diagnostics.MemoryInfo.FreePages < ArenaPages + 8192UL)
            { throw new IOException("insufficient memory for large native image"); }
            _arena = (byte*)(((ulong)Allocate((ArenaPages + 1) * 4096) + 4095) & ~4095UL);
            _arenaLeft = ArenaPages;
        }
        byte* page = _arena;
        _arena += 4096;
        _arenaLeft--;
        return page;
    }
    private ulong Physical(void* address)
    {
        ulong value = (ulong)address;
        if (value < _hhdm || value >= 0xffffffff80000000UL) { throw new IOException("native page is not an HHDM allocation"); }
        return value - _hhdm;
    }
    private void Map(ulong address, byte* frame, bool writable, bool executable)
    {
        var table = LeafTable(address, create: true);
        int leaf = (int)((address >> 12) & 511);
        if (table[leaf] != 0) { throw new IOException("duplicate native virtual page"); }
        table[leaf] = Physical(frame) | 5UL | (writable ? 2UL : 0) | (executable ? 0 : 1UL << 63);
    }
    private ulong* LeafTable(ulong address, bool create)
    {
        var table = _root;
        for (int shift = 39; shift >= 21; shift -= 9)
        {
            int index = (int)((address >> shift) & 511);
            if (table[index] == 0)
            {
                if (!create) { throw new IOException("unmapped native page"); }
                table[index] = Physical(Page()) | 7;
            }
            table = (ulong*)((table[index] & 0x000fffffffffF000UL) + _hhdm);
        }
        return table;
    }
    private byte* MappedPage(ulong address)
    {
        ulong entry = LeafTable(address, create: false)[(address >> 12) & 511];
        if ((entry & 1) == 0) { throw new IOException("unmapped native page"); }
        return (byte*)((entry & 0x000fffffffffF000UL) + _hhdm);
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
    private void Transfer(ulong address, Span<byte> buffer, bool write)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int within = (int)(address & 4095), count = Math.Min(4096 - within, buffer.Length - offset);
            var page = new Span<byte>(MappedPage(address) + within, count);
            if (write) { buffer.Slice(offset, count).CopyTo(page); } else { page.CopyTo(buffer.Slice(offset, count)); }
            address += (ulong)count; offset += count;
        }
    }
    private readonly byte[] _audioTransfer = new byte[8192];
    private readonly ProcessInteractive _interactive = new();
    private static long MonotonicMilliseconds => System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000);
    private static void Pace(int milliseconds) { if (milliseconds > 0) { Thread.Sleep(milliseconds); } }
    private long Service(ref Context c, ProcessFiles files, ProcessNetwork network, ProcessGraphics graphics, ref int readBytes, ref int directoryBytes)
    {
        try
        {
            if (c.Call == 661)
            {
                if (c.Arg1 != Audio.AudioService.SnapshotBytes || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, Audio.AudioService.SnapshotBytes, true)) { return -14; }
                Transfer(c.Arg0, Audio.AudioService.Snapshot((uint)c.Uid), true); return 0;
            }
            if (c.Call == 662)
            {
                if (c.Arg1 != 32 || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, 32, false)) { return -14; }
                return Audio.AudioService.Control((uint)c.Uid, ReadUser(c.Arg0, 32));
            }
            if (c.Call == 663)
            {
                if (c.Arg0 > 1 || c.Arg1 > uint.MaxValue || c.Arg2 != 0) { return -22; }
                return Audio.AudioService.Open((int)c.Pid, (uint)c.Uid, ProgramName((int)c.Pid), (int)c.Arg0, (uint)c.Arg1);
            }
            if (c.Call is 664 or 665)
            {
                if (c.Arg0 > long.MaxValue || c.Arg2 is < 4 or > 8192 || c.Arg2 % 4 != 0) { return -22; }
                bool write = c.Call == 664;
                if (!Valid(c.Arg1, (int)c.Arg2, !write)) { return -14; }
                var data = _audioTransfer.AsSpan(0, (int)c.Arg2);
                if (write) { Transfer(c.Arg1, data, false); }
                int n = Audio.AudioService.Pcm((int)c.Pid, (uint)c.Uid, (long)c.Arg0, data, write);
                if (!write && n > 0) { Transfer(c.Arg1, data[..n], true); }
                if (n == -11) { Thread.Sleep(4); }
                return n;
            }
            if (c.Call == 666)
            {
                return c.Arg0 > long.MaxValue || c.Arg1 != 0 || c.Arg2 != 0 ? -22 : Audio.AudioService.Close((int)c.Pid, (long)c.Arg0);
            }
            // One invocation-owned NX framebuffer arena, grown on demand and
            // freed with every other native allocation on exit/fault/cancel.
            if (c.Call == 645)
            {
                if (c.Arg1 != 0 || c.Arg2 != 0 || !graphics.Live) { return -22; }
                long address = Framebuffer(ref c, c.Arg0);
                if (address > 0) { graphics.EnableFramebuffer(); }
                return address;
            }
            if (c.Call == 646) { return c.Arg0 > int.MaxValue || c.Arg1 != 0 || c.Arg2 != 0 ? -22 : graphics.RefuseResize((int)c.Arg0); }
            if (c.Call == 638)
            {
                if (c.Arg0 > int.MaxValue || c.Arg2 is < 1 or > 320) { return -22; }
                if (!Valid(c.Arg1, (int)c.Arg2, false)) { return -14; }
                string title;
                try { title = new System.Text.UTF8Encoding(false, true).GetString(ReadUser(c.Arg1, (int)c.Arg2)); }
                catch (ArgumentException) { return -22; }
                return graphics.SetTitle((int)c.Arg0, title);
            }
            if (c.Call == 636)
            {
                if (c.Arg2 is < 1 or > ProcessTerminals.MaximumRead) { return -22; }
                if (!Valid(c.Arg1, (int)c.Arg2, false)) { return -14; }
                return graphics.Terminals.Copy((long)c.Arg0, System.Text.Encoding.UTF8.GetString(ReadUser(c.Arg1, (int)c.Arg2)));
            }
            if (c.Call == 637) { return c.Arg1 != 0 || c.Arg2 != 0 ? -22 : graphics.Terminals.Paste((long)c.Arg0); }
            // Clipboard for windows (kernel 0.0.64): 639 sets it from the focused window; 659 reads it once, right
            // after the person pressed Ctrl+V in that window, so no program reads the clipboard behind their back.
            if (c.Call == 639)
            {
                if (c.Arg0 > int.MaxValue || c.Arg2 is < 1 or > ProcessGraphics.ClipboardBytes) { return -22; }
                if (!Valid(c.Arg1, (int)c.Arg2, false)) { return -14; }
                string text;
                try { text = new System.Text.UTF8Encoding(false, true).GetString(ReadUser(c.Arg1, (int)c.Arg2)); }
                catch (ArgumentException) { return -22; }
                return graphics.CopyText((int)c.Arg0, text);
            }
            if (c.Call == 659)
            {
                if (c.Arg0 > int.MaxValue || c.Arg2 is < 1 or > ProcessGraphics.ClipboardBytes) { return -22; }
                if (!Valid(c.Arg1, (int)c.Arg2, true)) { return -14; }
                byte[]? pasted = graphics.PasteText((int)c.Arg0, out int error);
                if (pasted is null) { return error; }
                if (pasted.Length > (int)c.Arg2) { return -27; }
                if (pasted.Length > 0) { Transfer(c.Arg1, pasted, true); }
                return pasted.Length;
            }
            if (c.Call == 630)
            {
                if (c.Arg0 > int.MaxValue || c.Arg1 != 0 || c.Arg2 != 0) { return -22; }
                if (!graphics.Live) { return -19; }
                int terminal = graphics.Terminals.Open(graphics.TerminalHost, (int)c.Arg0, (int)c.Pid);
                if (terminal > 0) { Processes.MarkClient((int)c.Pid); }
                return terminal;
            }
            if (c.Call == 631)
            {
                if (c.Arg2 != ProcessTerminals.KeyBytes) { return -22; }
                if (!Valid(c.Arg1, ProcessTerminals.KeyBytes, false)) { return -14; }
                return graphics.Terminals.Key((long)c.Arg0, ReadUser(c.Arg1, ProcessTerminals.KeyBytes));
            }
            if (c.Call == 632)
            {
                if (c.Arg2 is < 1 or > ProcessTerminals.MaximumRead) { return -22; }
                if (!Valid(c.Arg1, (int)c.Arg2, true)) { return -14; }
                byte[] output = new byte[(int)c.Arg2];
                int read = graphics.Terminals.Read((long)c.Arg0, output);
                if (read > 0) { Transfer(c.Arg1, output.AsSpan(0, read).ToArray(), true); }
                return read;
            }
            if (c.Call == 633)
            {
                if (c.Arg2 != ProcessTerminals.StateBytes) { return -22; }
                if (!Valid(c.Arg1, ProcessTerminals.StateBytes, true)) { return -14; }
                byte[]? state = graphics.Terminals.State((long)c.Arg0);
                if (state is null) { return -9; }
                Transfer(c.Arg1, state, true); return state.Length;
            }
            if (c.Call == 634) { return c.Arg1 > int.MaxValue || c.Arg2 > 512 ? -22 : graphics.Terminals.Resize((long)c.Arg0, (int)c.Arg1, (int)c.Arg2); }
            if (c.Call == 635) { return c.Arg1 != 0 || c.Arg2 != 0 ? -22 : graphics.Terminals.Close((long)c.Arg0); }
            if (c.Call == 629)
            {
                if (c.Arg0 > int.MaxValue || c.Arg2 != 24) { return -22; }
                if (!Valid(c.Arg1, 24, false)) { return -14; }
                return graphics.RequestFade((int)c.Arg0, ReadUser(c.Arg1, 24));
            }
            if (c.Call == 628)
            { return c.Arg0 != 0 || c.Arg1 != 0 || c.Arg2 != 0 ? -22 : MonotonicMilliseconds; }
            if (c.Call == 627)
            { return c.Arg0 > int.MaxValue || c.Arg1 > 15 || (c.Arg1 & 1) == 0 || c.Arg2 != 0 ? -22 : graphics.EnablePointerMotion((int)c.Arg0, (int)c.Arg1); }
            if (c.Call == 671)
            { return c.Arg0 > int.MaxValue || c.Arg1 != 0 || c.Arg2 != 0 ? -22 : graphics.EnableKeyEdges((int)c.Arg0); }
            if (c.Call == 614)
            { return c.Arg0 > int.MaxValue || c.Arg1 != 1 || c.Arg2 != 0 ? -22 : graphics.EnableResize((int)c.Arg0); }
            if (c.Call == 613)
            {
                if (c.Arg0 > int.MaxValue || c.Arg2 != 32) { return -22; }
                if (!Valid(c.Arg1, 32, true)) { return -14; }
                var viewport = graphics.Viewport((int)c.Arg0); if (viewport is null) { return -9; }
                Transfer(c.Arg1, viewport, true); return 0;
            }
            if (c.Call == 615)
            {
                if (c.Arg0 > int.MaxValue || c.Arg2 != 24) { return -22; }
                if (!Valid(c.Arg1, 24, false)) { return -14; }
                byte[] request = ReadUser(c.Arg1, 24);
                int w = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(8));
                int h = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(12));
                if (w < 600 || h < 480 || w > ProcessSurface.MaximumWidth || h > ProcessSurface.MaximumHeight
                    || (long)w * h > ProcessSurface.MaximumPixels) { return -22; }
                // New surface + host frame + transfer buffer, retaining the old
                // ones until the frame handoff. Refuse before changing the viewport.
                if (Cosmos.Kernel.System.Diagnostics.MemoryInfo.FreePages < (ulong)w * (ulong)h * 12 / 4096 + 8192) { return -12; }
                return graphics.AcceptResize((int)c.Arg0, request);
            }
            if (c.Call == 612)
            {
                if (c.Arg1 is < 1 or > 4096 || c.Arg2 != 1) { return -22; }
                if (!Valid(c.Arg0, (int)c.Arg1, true)) { return -14; }
                byte[] info = NativeSystemInformation.Snapshot();
                if ((ulong)info.Length > c.Arg1) { return -34; }
                Transfer(c.Arg0, info, true); return info.Length;
            }
            if (c.Call == 610)
            {
                if (c.Arg1 != ProcessAppearance.SnapshotBytes || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, ProcessAppearance.SnapshotBytes, true)) { return -14; }
                Transfer(c.Arg0, ProcessAppearance.Snapshot(Zenith.Core.Configuration.SystemConfiguration.Appearance), true); return 0;
            }
            if (c.Call == 611)
            {
                if (c.Arg1 != ProcessAppearance.RequestBytes || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, ProcessAppearance.RequestBytes, false)) { return -14; }
                return ProcessAppearance.Save(Zenith.Core.Configuration.SystemConfiguration.Appearance, ReadUser(c.Arg0, ProcessAppearance.RequestBytes));
            }
            if (c.Call == 605)
            {
                if (c.Arg0 > int.MaxValue || c.Arg1 != 1 || c.Arg2 != 0) { return -22; }
                int enabled = _interactive.Enable(graphics.OwnsLive((int)c.Arg0), MonotonicMilliseconds);
                if (enabled == 0) { c.Ticks = Math.Max(c.Ticks, (ulong)ProcessInteractive.LiveQuantaPerWindow); }
                return enabled;
            }
            if (c.Call == 604)
            { return c.Arg0 > int.MaxValue || c.Arg1 > 1 || c.Arg2 != 0 ? -22 : graphics.Dirty((int)c.Arg0, c.Arg1 != 0); }
            if (c.Call == 600)
            {
                if (c.Arg1 != ProcessGraphicsRequest.Bytes || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, ProcessGraphicsRequest.Bytes, false)) { return -14; }
                if (!ProcessGraphicsRequest.TryRead(ReadUser(c.Arg0, ProcessGraphicsRequest.Bytes), out var r)) { return -22; }
                if (!Valid(r.Title, r.Length, false)) { return -14; }
                byte[] title = ReadUser(r.Title, r.Length);
                foreach (byte value in title) { if (value < 32 || value > 126) { return -22; } }
                return graphics.Open(r.Width, r.Height, Encoding.ASCII.GetString(title), r.Format, (int)c.Pid);
            }
            if (c.Call == 624)
            {
                if(c.Arg0 is < 1 or > 2 || c.Arg2 != 4096) { return -22; }
                if(!Valid(c.Arg1,4096,true)) { return -14; }
                try { Transfer(c.Arg1,NativeSettingsInformation.Snapshot((uint)c.Arg0),true);return 0; } catch { return -5; }
            }
            // Observatory: system sample, the caller's processes (root: all), and a polite stop.
            if (c.Call == 642)
            {
                if (c.Arg1 != Zenith.Core.Monitoring.SystemMonitor.SnapshotBytes || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, Zenith.Core.Monitoring.SystemMonitor.SnapshotBytes, true)) { return -14; }
                Transfer(c.Arg0, Zenith.Core.Monitoring.KernelMonitor.Instance.Snapshot(Processes.ActiveCount), true); return 0;
            }
            if (c.Call == 643)
            {
                if (c.Arg1 != Zenith.Core.Monitoring.ProcessMonitor.Bytes || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, Zenith.Core.Monitoring.ProcessMonitor.Bytes, true)) { return -14; }
                var accounts = Zenith.Core.Security.Accounts.Service;
                Transfer(c.Arg0, Zenith.Core.Monitoring.ProcessMonitor.Snapshot(Processes.List((uint)c.Uid),
                    uid => accounts?.Name(uid) ?? (uid == 0 ? "root" : uid.ToString())), true);
                return 0;
            }
            if (c.Call == 644)
            {
                if (c.Arg0 is 0 or > int.MaxValue || c.Arg1 != 0 || c.Arg2 != 0) { return -22; }
                if (c.Arg0 == c.Pid) { return -22; }
                return Processes.RequestStop((int)c.Arg0, (uint)c.Uid);
            }
            // Beacon and the network sections (kernel 0.0.69): status, traffic history, events, diagnostics, ping,
            // safe apply, connect/disconnect and the programs using the network. Request: 64 bytes (zenith_network.h).
            if (c.Call == 667)
            {
                if (c.Arg1 != 64 || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, 64, false)) { return -14; }
                byte[] q = ReadUser(c.Arg0, 64);
                uint version = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(q), op = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(4));
                ulong output = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(q.AsSpan(8)), input = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(q.AsSpan(24));
                uint capacity = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(16)), value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(20));
                uint inputBytes = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(32));
                for (int i = 36; i < 64; i++) { if (q[i] != 0) { return -22; } }
                if (version != 1 || capacity > 16384 || inputBytes > 256) { return -22; }
                if ((capacity > 0 && !Valid(output, (int)capacity, true)) || (inputBytes > 0 && !Valid(input, (int)inputBytes, false))) { return -14; }
                byte[] given = inputBytes > 0 ? ReadUser(input, (int)inputBytes) : Array.Empty<byte>();
                Action stop = network.ThrowIfCancelled;
                byte[] Reply(byte[] data) => data;
                long Send(byte[] data) { if (data.Length > capacity) { return -34; } Transfer(output, data, true); return data.Length; }
                // One catch clause decides by type: Cosmos's handler matching confused IOException with
                // UnauthorizedAccessException in sibling catch clauses (a refused gateway came back as -13).
                long Failure(Exception e) => e is UnauthorizedAccessException ? -13 : e is ArgumentException ? -22 : Message(-5, e.Message);
                long Message(long code, string text)
                {
                    byte[] t = Encoding.UTF8.GetBytes(text.Length > 240 ? text[..240] : text);
                    if (capacity >= t.Length + 1) { var buffer = new byte[t.Length + 1]; t.CopyTo(buffer, 0); Transfer(output, buffer, true); }
                    return code;
                }
                try
                {
                    switch (op)
                    {
                        case 1: return Send(Zenith.Core.Network.NetworkCenter.Status());
                        case 2: return value > 3 ? -22 : Send(Zenith.Core.Network.NetworkCenter.History.Encode((int)value));
                        case 3: return Send(Zenith.Core.Network.NetworkCenter.History.EncodeEvents());
                        case 4: return value is < 1 or > 6 ? -22 : Send(Zenith.Core.Network.NetworkCenter.Check((int)value, stop));
                        case 5:
                        {
                            string host = Encoding.UTF8.GetString(given);
                            if (host.Length is < 1 or > 253 || host.Contains('\0') || host.Contains(' ')) { return -22; }
                            try { return Zenith.Core.Network.NetworkService.Ping(host, stop); }
                            catch (Exception e) { if (e is OperationCanceledException) { throw; } return Failure(e); }
                        }
                        case 6:
                        {
                            if (given.Length != 48 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(given) != 1) { return -22; }
                            uint mode = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(given.AsSpan(4));
                            string Ip(int at) => given[at] + "." + given[at + 1] + "." + given[at + 2] + "." + given[at + 3];
                            if (mode > 2) { return -22; }
                            var next = Zenith.Core.Network.NetworkService.Configuration with
                            {
                                Mode = mode == 0 ? "dhcp" : mode == 1 ? "static" : "off",
                                Address = mode == 1 ? Ip(8) : "0.0.0.0", Mask = mode == 1 ? Ip(12) : "0.0.0.0",
                                Gateway = mode == 1 ? Ip(16) : "0.0.0.0", Dns = mode == 1 ? Ip(20) : "0.0.0.0",
                            };
                            try { Zenith.Core.Network.NetworkCenter.Apply(next, stop); return Message(0, "applied"); }
                            catch (Exception e) { if (e is OperationCanceledException) { throw; } return Failure(e); }
                        }
                        case 7:
                            try
                            {
                                if (value == 0) { Zenith.Core.Network.NetworkService.Disconnect(); }
                                else { Zenith.Core.Network.NetworkService.Apply(stop); }
                                return Message(0, value == 0 ? "disconnected" : "connected");
                            }
                            catch (Exception e) { if (e is OperationCanceledException) { throw; } return Failure(e); }
                        case 8:
                        {
                            var visible = new System.Collections.Generic.HashSet<int>();
                            foreach (var p in Processes.List((uint)c.Uid)) { visible.Add(p.Pid); }
                            var data = new byte[16 + 16 * 64]; int n = 0;
                            foreach (var u in ProcessNetwork.Programs())
                            {
                                if (n >= 16 || (c.Uid != 0 && !visible.Contains(u.Pid))) { continue; }
                                var slot = data.AsSpan(16 + n * 64, 64);
                                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(slot, u.Pid);
                                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(slot[4..], (uint)u.Connections);
                                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(slot[8..], u.Received);
                                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(slot[16..], u.Sent);
                                byte[] peer = Encoding.ASCII.GetBytes(u.Peer.Length > 39 ? u.Peer[..39] : u.Peer); peer.CopyTo(slot[24..]);
                                n++;
                            }
                            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data, 1); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)n);
                            return Send(Reply(data));
                        }
                        default: return -22;
                    }
                }
                catch (Exception e) { if (e is OperationCanceledException) { throw; } return Failure(e); }
            }
            // Orbit (kernel 0.0.70, zenith-native-package-center-abi 1.0, zenith_packages.h): overview, packages, details,
            // plans, apply and refresh jobs, history and sources over the same pkg engine. Request: 64 bytes like 667.
            if (c.Call == 668)
            {
                if (c.Arg1 != 64 || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, 64, false)) { return -14; }
                byte[] q = ReadUser(c.Arg0, 64);
                uint version = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(q), op = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(4));
                ulong output = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(q.AsSpan(8)), input = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(q.AsSpan(24));
                uint capacity = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(16)), value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(20));
                uint inputBytes = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(32));
                for (int i = 36; i < 64; i++) { if (q[i] != 0) { return -22; } }
                if (version != 1 || capacity > 65536 || inputBytes > 4096) { return -22; }
                if ((capacity > 0 && !Valid(output, (int)capacity, true)) || (inputBytes > 0 && !Valid(input, (int)inputBytes, false))) { return -14; }
                byte[] given = inputBytes > 0 ? ReadUser(input, (int)inputBytes) : Array.Empty<byte>();
                // Orbit holds no package files: like a terminal client, it does not keep pkg from running.
                Processes.MarkClient((int)c.Pid);
                Action stop = network.ThrowIfCancelled;
                long Send(byte[] data) { if (data.Length > capacity) { return -34; } Transfer(output, data, true); return data.Length; }
                long Message(long code, string text)
                {
                    byte[] t = Encoding.UTF8.GetBytes(text.Length > 400 ? text[..400] : text);
                    if (capacity >= t.Length + 1) { var buffer = new byte[t.Length + 1]; t.CopyTo(buffer, 0); Transfer(output, buffer, true); }
                    return code;
                }
                long Failure(Exception e) => e is UnauthorizedAccessException ? -13 : e is ArgumentException ? -22
                    : e is InvalidOperationException && e.Message == "busy" ? -16 : Message(-5, Zenith.Core.Packages.PackageCenter.Words(e.Message));
                uint realUid = 0;
                foreach (var p in Processes.List(0)) { if (p.Pid == (int)c.Pid) { realUid = p.Uid; } }
                bool mayApply = Zenith.Core.Security.CredentialContext.Current.IsRoot;
                Zenith.Core.Packages.PackageCenter.Bind();
                try
                {
                    switch (op)
                    {
                        case 1: return Send(Zenith.Core.Packages.PackageCenter.Overview(mayApply, stop));
                        case 2: return Send(Zenith.Core.Packages.PackageCenter.List(stop));
                        case 3:
                        {
                            string name = Encoding.UTF8.GetString(given);
                            if (!Zenith.Core.Packages.PackageManifest.ValidName(name) || value > 1) { return -22; }
                            return Send(Zenith.Core.Packages.PackageCenter.Details(name, value == 1, stop));
                        }
                        case 4: return Send(Zenith.Core.Packages.PackageCenter.Plan(Encoding.UTF8.GetString(given), stop));
                        case 5: case 10:
                        {
                            long job = Zenith.Core.Packages.PackageCenter.Start(op == 5 ? "apply" : "refresh", Encoding.UTF8.GetString(given), (int)c.Pid, realUid);
                            return job;
                        }
                        case 6: return Send(Zenith.Core.Packages.PackageCenter.Job((int)c.Pid));
                        case 7: return Zenith.Core.Packages.PackageCenter.Cancel((int)c.Pid, mayApply);
                        case 8: return Send(Zenith.Core.Packages.PackageCenter.History());
                        case 9: return Send(Zenith.Core.Packages.PackageCenter.Sources());
                        case 11: return Zenith.Core.Packages.PackageCenter.Wait(value, stop);
                        default: return -22;
                    }
                }
                catch (Exception e) { if (e is OperationCanceledException) { throw; } return Failure(e); }
            }
            // Observatory: disks, network and the windows of the caller's programs (and the desktop's).
            if (c.Call == 649)
            {
                if (c.Arg1 != Zenith.Core.Monitoring.ActivityMonitor.SnapshotBytes || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, Zenith.Core.Monitoring.ActivityMonitor.SnapshotBytes, true)) { return -14; }
                var visible = new System.Collections.Generic.HashSet<int>();
                foreach (var p in Processes.List((uint)c.Uid)) { visible.Add(p.Pid); }
                var windows = new System.Collections.Generic.List<Zenith.Core.Monitoring.ActivityWindow>();
                foreach (var w in Zenith.Core.Monitoring.KernelMonitor.Windows) { if (w.Pid == 0 || visible.Contains(w.Pid)) { windows.Add(w); } }
                Transfer(c.Arg0, Zenith.Core.Monitoring.KernelMonitor.Activity.Snapshot(windows), true); return 0;
            }
            // Desktop Portal (zenith-native-portal-abi 1.0): open, poll, close, list interfaces.
            if (c.Call == 650)
            {
                if (c.Arg1 != ProcessPortal.RequestBytes || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, ProcessPortal.RequestBytes, false)) { return -14; }
                var caller = new Zenith.Core.Portal.PortalCaller((int)c.Pid, (uint)c.Uid, (uint)c.Gid, ProgramName((int)c.Pid));
                return ProcessPortal.Open(ReadUser(c.Arg0, ProcessPortal.RequestBytes), caller, (address, bytes) => Valid(address, bytes, false) ? ReadUser(address, bytes) : null);
            }
            if (c.Call == 651)
            {
                if (c.Arg0 is 0 or > long.MaxValue || c.Arg2 != ProcessPortal.ResponseBytes) { return -22; }
                if (!Valid(c.Arg1, ProcessPortal.ResponseBytes, true)) { return -14; }
                var request = Zenith.Core.Portal.PortalBroker.Find((long)c.Arg0, (int)c.Pid);
                if (request is null) { return -9; }
                Transfer(c.Arg1, ProcessPortal.Poll(request), true); return (long)request.State;
            }
            if (c.Call == 652)
            { return c.Arg0 is 0 or > long.MaxValue || c.Arg1 != 0 || c.Arg2 != 0 ? -22 : Zenith.Core.Portal.PortalBroker.Close((long)c.Arg0, (int)c.Pid); }
            if (c.Call == 653)
            {
                if (c.Arg1 != ProcessPortal.InfoBytes || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, ProcessPortal.InfoBytes, true)) { return -14; }
                Transfer(c.Arg0, ProcessPortal.Info(), true); return 0;
            }
            // Horizon: listing, free space and transfer jobs (zenith-native-files-abi 1.0).
            if (c.Call == 654)
            {
                if (c.Arg1 != ProcessFileService.ListRequestBytes) { return -22; }
                if (!Valid(c.Arg0, ProcessFileService.ListRequestBytes, false) || !Valid(c.Arg2, ProcessFileService.PageBytes, true)) { return -14; }
                if (!ProcessFileService.ParseList(ReadUser(c.Arg0, ProcessFileService.ListRequestBytes), out string listed, out uint listFlags, out int offset)) { return -22; }
                long count = NativeFileService.List(files.Resolve(listed), listFlags, offset, out byte[] page);
                if (count >= 0) { Transfer(c.Arg2, page, true); }
                return count;
            }
            // Horizon HZ4 (kernel 0.0.65): a thumbnail of an image file. Request (32 bytes): version 1, max side
            // 16..256, u64 path, u32 path bytes, u32 capacity, u64 pixels. Result: width << 16 | height, ARGB pixels.
            // Meridian's pickers and system sounds (zenith-native-style-catalog-abi 1.0, kernel 0.0.72): the choices of a setting, and
            // a preview drawn by the desktop. Request (32 bytes): version 1, op (1 list, 2 preview), field, index,
            // u64 output, u32 capacity, u32 width << 16 | height.
            // Notifications. Version 1 preserves the original op 1..6 packets. Version 2 extends the
            // op 2 snapshot and op 3 save with popup flags and corner (settings grant still applies).
            if (c.Call == 670)
            {
                if (c.Arg1 != 32 || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, 32, false)) { return -14; }
                byte[] r = ReadUser(c.Arg0, 32);
                uint version = BitConverter.ToUInt32(r, 0), op = BitConverter.ToUInt32(r, 4), argument = BitConverter.ToUInt32(r, 8);
                ulong buffer = BitConverter.ToUInt64(r, 16); uint size = BitConverter.ToUInt32(r, 24);
                if (version != 1 && (version != 2 || op is not (2 or 3) || BitConverter.ToUInt32(r, 12) != 0 || BitConverter.ToUInt32(r, 28) != 0)) { return -22; }
                var service = Zenith.Core.Configuration.SystemConfiguration.Settings;
                if (op == 1)
                {
                    if (size != 256 || !Valid(buffer, 256, false)) { return -22; }
                    byte[] note = ReadUser(buffer, 256);
                    static string Field(byte[] b, int at, int length)
                    {
                        int n = 0; while (n < length && b[at + n] != 0) { n++; }
                        return n == length ? "\u0001" : Encoding.UTF8.GetString(b, at, n);   // unterminated: refused below
                    }
                    string title = Field(note, 0, 64), body = Field(note, 64, 160), icon = Field(note, 224, 32);
                    if (title == "\u0001" || body == "\u0001" || icon == "\u0001") { return -22; }
                    return Zenith.Core.Notifications.NotificationCenter.Post(ProgramName((int)c.Pid), title, body, icon, (uint)c.Uid, (int)c.Pid);
                }
                if (service is null) { return -19; }
                if (op == 2)
                {
                    if (size < 32 || !Valid(buffer, 32, true) || (version == 2 && (size != 32 || argument != 0))) { return -22; }
                    var (preferences, revision) = service.NotificationSnapshot();
                    byte[] data = new byte[32];
                    BitConverter.TryWriteBytes(data.AsSpan(0), version); BitConverter.TryWriteBytes(data.AsSpan(4), preferences.DoNotDisturb ? 1u : 0u);
                    BitConverter.TryWriteBytes(data.AsSpan(8), preferences.Time); BitConverter.TryWriteBytes(data.AsSpan(12), (uint)Zenith.Core.Configuration.SettingsService.PopupNames.Length);
                    BitConverter.TryWriteBytes(data.AsSpan(16), revision);
                    if (version == 2) { BitConverter.TryWriteBytes(data.AsSpan(24), preferences.Flags); BitConverter.TryWriteBytes(data.AsSpan(28), preferences.Corner); }
                    Transfer(buffer, data, true); return data.Length;
                }
                if (op == 3)
                {
                    int bytes = version == 2 ? 16 : 8;
                    if (size < bytes || !Valid(buffer, bytes, false) || (argument & 0xFF) > 1 || (version == 2 && size != bytes)) { return -22; }
                    byte[] data = ReadUser(buffer, bytes);
                    return service.SaveNotifications((argument & 1) != 0, argument >> 8, BitConverter.ToUInt64(data, 0),
                        version == 2 ? BitConverter.ToUInt32(data, 8) : null, version == 2 ? BitConverter.ToUInt32(data, 12) : null);
                }
                // 1.1 (kernel 0.0.76): op 4 lists the senders ("name TAB 0|1 LF", 1 = switched off) into the buffer;
                // op 5 switches one off (argument 1) or on (0): buffer = u64 revision, name[56]; op 6 posts with up to
                // two buttons: buffer = the 256-byte notification, then two 64-byte buttons (label[24], reserved).
                if (op == 4)
                {
                    if (size < 64 || size > 8192 || !Valid(buffer, (int)size, true)) { return -22; }
                    var names = Zenith.Core.Notifications.NotificationCenter.Senders();
                    var muted = service.MutedSenders;
                    foreach (string m in muted) { if (!names.Contains(m)) { names.Add(m); } }
                    var text = new StringBuilder();
                    int bytes = 0;
                    foreach (string n in names)
                    {
                        bool off = false; foreach (string m in muted) { if (m == n) { off = true; } }
                        string line = n + "\t" + (off ? "1" : "0") + "\n";
                        int length = Encoding.UTF8.GetByteCount(line);
                        if (bytes + length > size) { break; }
                        text.Append(line);
                        bytes += length;
                    }
                    byte[] data = Encoding.UTF8.GetBytes(text.ToString());
                    if (data.Length > 0) { Transfer(buffer, data, true); }
                    return data.Length;
                }
                if (op == 5)
                {
                    if (size != 64 || !Valid(buffer, 64, false) || argument > 1) { return -22; }
                    byte[] q = ReadUser(buffer, 64);
                    int n = 0; while (n < 56 && q[8 + n] != 0) { n++; }
                    if (n == 56) { return -22; }
                    return service.SaveSenderMuted(Encoding.UTF8.GetString(q, 8, n), argument == 1, BitConverter.ToUInt64(q, 0));
                }
                if (op == 6)
                {
                    if (size != 384 || !Valid(buffer, 384, false)) { return -22; }
                    byte[] note = ReadUser(buffer, 384);
                    static string Field6(byte[] b, int at, int length)
                    {
                        int k = 0; while (k < length && b[at + k] != 0) { k++; }
                        return k == length ? "\u0001" : Encoding.UTF8.GetString(b, at, k);
                    }
                    string title = Field6(note, 0, 64), body = Field6(note, 64, 160), icon = Field6(note, 224, 32);
                    if (title == "\u0001" || body == "\u0001" || icon == "\u0001") { return -22; }
                    var buttons = new System.Collections.Generic.List<Zenith.Core.Notifications.NotificationCenter.NoteAction>();
                    for (int b = 0; b < 2; b++)
                    {
                        string label = Field6(note, 256 + b * 64, 24);
                        if (label == "\u0001") { return -22; }
                        // A program's button can only reach the program itself: the desktop sends it event 13.
                        if (label.Length > 0) { buttons.Add(new(label, "@sender", Array.Empty<string>())); }
                    }
                    return Zenith.Core.Notifications.NotificationCenter.Post(ProgramName((int)c.Pid), title, body, icon, (uint)c.Uid, (int)c.Pid, buttons.Count == 0 ? null : buttons.ToArray());
                }
                return -22;
            }
            if (c.Call == 669)
            {
                if (c.Arg1 != 32 || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, 32, false)) { return -14; }
                byte[] r = ReadUser(c.Arg0, 32);
                uint version = BitConverter.ToUInt32(r, 0), op = BitConverter.ToUInt32(r, 4), field = BitConverter.ToUInt32(r, 8), index = BitConverter.ToUInt32(r, 12);
                ulong output = BitConverter.ToUInt64(r, 16); uint capacity = BitConverter.ToUInt32(r, 24), size = BitConverter.ToUInt32(r, 28);
                if (version != 1 || capacity > 1 << 20) { return -22; }
                if (capacity > 0 && !Valid(output, (int)capacity, true)) { return -14; }
                var service = Zenith.Core.Configuration.SystemConfiguration.Settings;
                if (service is null) { return -19; }
                uint accent = Zenith.Core.Configuration.StyleCatalog.Accent?.Invoke() ?? 0x7ECAC7;
                if (op == 1)
                {
                    if (capacity < Zenith.Core.Configuration.StyleCatalog.ListBytes) { return -34; }
                    byte[]? list = Zenith.Core.Configuration.StyleCatalog.List(service, field, accent);
                    if (list is null) { return -19; }
                    Transfer(output, list, true); return list.Length;
                }
                // System sounds (U14): 3 reads level, event mask and labels; 4 saves (index = level, size = mask; root
                // or the system settings grant); 5 plays event <index> once as a preview.
                if (op == 3)
                {
                    if (capacity < 512) { return -34; }
                    var (_, revision) = service.Snapshot();
                    byte[] data = new byte[512];
                    BitConverter.TryWriteBytes(data.AsSpan(0), 1u); BitConverter.TryWriteBytes(data.AsSpan(4), service.SoundLevel);
                    BitConverter.TryWriteBytes(data.AsSpan(8), service.SoundMask); BitConverter.TryWriteBytes(data.AsSpan(12), (uint)Zenith.Core.Configuration.SettingsService.SoundEvents.Length);
                    BitConverter.TryWriteBytes(data.AsSpan(16), revision);
                    for (int i = 0; i < Zenith.Core.Configuration.SettingsService.SoundEventLabels.Length; i++)
                    { Encoding.ASCII.GetBytes(Zenith.Core.Configuration.SettingsService.SoundEventLabels[i]).CopyTo(data, 32 + i * 32); }
                    Transfer(output, data, true); return data.Length;
                }
                if (op == 4)
                {
                    if (capacity < 8 || !Valid(output, 8, false)) { return -22; }
                    ulong expected = BitConverter.ToUInt64(ReadUser(output, 8), 0);
                    return service.SaveSounds(index, size, expected);
                }
                if (op == 5)
                {
                    if (index >= Zenith.Core.Configuration.SettingsService.SoundEvents.Length) { return -22; }
                    Zenith.Core.Audio.SystemSounds.Play(Zenith.Core.Configuration.SettingsService.SoundEvents[index], true); return 0;
                }
                if (op == 2)
                {
                    int w = (int)(size >> 16), h = (int)(size & 0xFFFF);
                    if (w is < 32 or > 320 || h is < 20 or > 240 || capacity < (uint)(w * h * 4)) { return -22; }
                    int[]? picture;
                    if (field == Zenith.Core.Configuration.StyleCatalog.FieldWallpaper)
                    {
                        if (index >= Zenith.Core.Configuration.SettingsService.WallpaperNames.Length) { return -22; }
                        var render = Zenith.Core.Configuration.StyleCatalog.RenderWallpaper;
                        if (render is null) { return -19; }
                        picture = render(index, w, h);
                    }
                    else
                    {
                        picture = Zenith.Core.Configuration.StyleCatalog.RenderPreview?.Invoke(field, index, w, h);
                        if (picture is null) { return -22; }
                    }
                    byte[] pixels = new byte[picture.Length * 4];
                    Buffer.BlockCopy(picture, 0, pixels, 0, pixels.Length);
                    Transfer(output, pixels, true); return w << 16 | h;
                }
                return -22;
            }
            if (c.Call == 660)
            {
                if (c.Arg1 != 32 || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, 32, false)) { return -14; }
                byte[] r = ReadUser(c.Arg0, 32);
                uint version = BitConverter.ToUInt32(r, 0), max = BitConverter.ToUInt32(r, 4), pathBytes = BitConverter.ToUInt32(r, 16), capacity = BitConverter.ToUInt32(r, 20);
                ulong pathAt = BitConverter.ToUInt64(r, 8), pixelsAt = BitConverter.ToUInt64(r, 24);
                if (version != 1 || max is < 16 or > 256 || pathBytes is 0 or > 1024 || capacity < max * max * 4) { return -22; }
                if (!Valid(pathAt, (int)pathBytes, false) || !Valid(pixelsAt, (int)capacity, true)) { return -14; }
                string image;
                try { image = files.Resolve(new UTF8Encoding(false, true).GetString(ReadUser(pathAt, (int)pathBytes))); }
                catch (ArgumentException) { return -22; }
                System.IO.FileStream stream;
                try { stream = new System.IO.FileStream(image, System.IO.FileMode.Open, System.IO.FileAccess.Read); }
                catch (System.IO.FileNotFoundException) { return -2; }
                catch (System.IO.DirectoryNotFoundException) { return -2; }
                catch (UnauthorizedAccessException) { return -13; }
                catch (System.IO.IOException) { return -5; }
                if (stream.Length > 32L << 20) { stream.Dispose(); return -27; }
                var thumb = Zenith.Core.Files.Thumbnails.Make(stream, (int)max, out _);
                stream.Dispose();
                if (thumb is null) { return -95; }
                byte[] pixels = new byte[thumb.Pixels.Length * 4];
                Buffer.BlockCopy(thumb.Pixels, 0, pixels, 0, pixels.Length);
                Transfer(pixelsAt, pixels, true);
                return thumb.Width << 16 | thumb.Height;
            }
            if (c.Call == 655)
            {
                if (c.Arg1 is 0 or > 1024) { return -36; }
                if (!Valid(c.Arg0, (int)c.Arg1, false) || !Valid(c.Arg2, ProcessFileService.FilesystemBytes, true)) { return -14; }
                string where = new UTF8Encoding(false, true).GetString(ReadUser(c.Arg0, (int)c.Arg1));
                long result = NativeFileService.Filesystem(files.Resolve(where), out byte[] info);
                if (result == 0) { Transfer(c.Arg2, info, true); }
                return result;
            }
            if (c.Call == 656)
            {
                if (c.Arg1 is < ProcessFileService.JobHeader or > ProcessFileService.MaximumJobRequest || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, (int)c.Arg1, false)) { return -14; }
                if (!ProcessFileService.ParseJob(ReadUser(c.Arg0, (int)c.Arg1), out var operation, out uint jobFlags, out string destination, out string[] sources)) { return -22; }
                return Zenith.Core.Files.FileJobs.Start((int)c.Pid, Zenith.Core.Security.CredentialContext.Current, operation, jobFlags, destination, sources, new NativeFileService.Store(), s_serviceGate);
            }
            if (c.Call == 657)
            {
                if (c.Arg0 is 0 or > long.MaxValue || c.Arg2 != ProcessFileService.StatusBytes) { return -22; }
                if (!Valid(c.Arg1, ProcessFileService.StatusBytes, true)) { return -14; }
                var job = Zenith.Core.Files.FileJobs.Find((long)c.Arg0, (int)c.Pid);
                if (job is null) { return -9; }
                var status = job.Status; Transfer(c.Arg1, ProcessFileService.Status(status), true); return (long)status.State;
            }
            if (c.Call == 658)
            {
                if (c.Arg0 is 0 or > long.MaxValue) { return -22; }
                return ProcessFileService.Control(Zenith.Core.Files.FileJobs.Find((long)c.Arg0, (int)c.Pid), (int)c.Pid, (long)c.Arg0, c.Arg1, c.Arg2);
            }
            // Equinox: disks and their edits (zenith-native-disk-abi 1.0).
            if (c.Call == 647)
            {
                if (c.Arg1 != ProcessDisks.SnapshotBytes || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, ProcessDisks.SnapshotBytes, true)) { return -14; }
                Transfer(c.Arg0, ProcessDisks.Snapshot((uint)c.Uid), true); return 0;
            }
            if (c.Call == 648)
            {
                if (c.Arg1 != ProcessDisks.RequestBytes || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, ProcessDisks.RequestBytes, false)) { return -14; }
                return ProcessDisks.Request(ReadUser(c.Arg0, ProcessDisks.RequestBytes), (uint)c.Uid);
            }
            if(c.Call == 640)
            {
                if(c.Arg1 != ProcessDisplaySettings.SnapshotBytes || c.Arg2 != 0) { return -22; }
                if(!Valid(c.Arg0,ProcessDisplaySettings.SnapshotBytes,true)) { return -14; }
                Transfer(c.Arg0,ProcessDisplaySettings.Snapshot(Zenith.Core.Configuration.SystemConfiguration.Display,Zenith.Core.Configuration.DisplayService.Now,(int)c.Pid),true); return 0;
            }
            if(c.Call == 641)
            {
                if(c.Arg1 != ProcessDisplaySettings.RequestBytes || c.Arg2 != 0) { return -22; }
                if(!Valid(c.Arg0,ProcessDisplaySettings.RequestBytes,false)) { return -14; }
                return ProcessDisplaySettings.Request(Zenith.Core.Configuration.SystemConfiguration.Display,ReadUser(c.Arg0,ProcessDisplaySettings.RequestBytes),(int)c.Uid,(int)c.Pid);
            }
            // Desktop preferences 1.0: read a page (1), save its revisioned draft (2), or list installed apps (3).
            if (c.Call == 672)
            {
                var service = Zenith.Core.Configuration.SystemConfiguration.Settings;
                if (c.Arg1 == 3)
                {
                    if (c.Arg2 is < 8 or > 8192 || !Valid(c.Arg0, (int)c.Arg2, true)) { return -22; }
                    byte[] catalog = Zenith.Gui.Shell.DesktopAppCatalog.Encode();
                    if ((ulong)catalog.Length > c.Arg2) { return -28; }
                    Transfer(c.Arg0, catalog, true);
                    return catalog.Length;
                }
                if (c.Arg1 == 1)
                {
                    if (c.Arg2 is < 1 or > 5 || !Valid(c.Arg0, ProcessDesktopPreferences.Bytes, true)) { return -22; }
                    bool writable = CredentialContext.Current.IsRoot
                        || Zenith.Core.Portal.ProcessPrivileges.Holds((int)c.Pid, Zenith.Core.Portal.Privilege.SystemSettings);
                    Transfer(c.Arg0, ProcessDesktopPreferences.Snapshot(service, (uint)c.Arg2, writable), true);
                    return 0;
                }
                if (c.Arg1 != 2 || c.Arg2 != ProcessDesktopPreferences.Bytes || !Valid(c.Arg0, ProcessDesktopPreferences.Bytes, false)) { return -22; }
                bool grant = Zenith.Core.Portal.ProcessPrivileges.Holds((int)c.Pid, Zenith.Core.Portal.Privilege.SystemSettings);
                var desktopScope = CredentialContext.Enter(grant ? Credentials.Root : CredentialContext.Current);
                try { int result = ProcessDesktopPreferences.Save(service, ReadUser(c.Arg0, ProcessDesktopPreferences.Bytes)); desktopScope.Dispose(); return result; }
                catch { desktopScope.Dispose(); throw; }
            }
            if(c.Call == 625)
            {
                if(c.Arg1 != 4096 || c.Arg2 != 0) { return -22; }
                if(!Valid(c.Arg0,4096,true)) { return -14; }
                Transfer(c.Arg0,ProcessSystemSettings.Snapshot(Zenith.Core.Configuration.SystemConfiguration.Settings,true),true);return 0;
            }
            if(c.Call == 626)
            {
                if(c.Arg1 != 32 || c.Arg2 != 0) { return -22; }
                if(!Valid(c.Arg0,32,false)) { return -14; }
                return ProcessSystemSettings.Save(Zenith.Core.Configuration.SystemConfiguration.Settings,ReadUser(c.Arg0,32),true);
            }
            if (c.Call == 622)
            {
                if (c.Arg0 > 1 || c.Arg2 != 512) { return -22; }
                if (!Valid(c.Arg1,512,true)) { return -14; }
                try { Transfer(c.Arg1,ProcessScopedAppearance.Snapshot(Zenith.Core.Configuration.SystemConfiguration.Appearance,Zenith.Core.Configuration.SystemConfiguration.PersonalAppearance,(uint)c.Arg0),true);return 0; }
                catch { return -5; }
            }
            if (c.Call == 623)
            {
                if (c.Arg1 != 48 || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0,48,false)) { return -14; }
                byte[] scoped = ReadUser(c.Arg0,48);
                // The system scope (0) runs as root under a system-settings grant; a personal scope never does.
                bool system = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(scoped.AsSpan(4)) == 0
                    && Zenith.Core.Portal.ProcessPrivileges.Holds((int)c.Pid, Zenith.Core.Portal.Privilege.SystemSettings);
                var scope = Zenith.Core.Security.CredentialContext.Enter(system ? Zenith.Core.Security.Credentials.Root : Zenith.Core.Security.CredentialContext.Current);
                try { long saved = ProcessScopedAppearance.Save(Zenith.Core.Configuration.SystemConfiguration.Appearance,Zenith.Core.Configuration.SystemConfiguration.PersonalAppearance,scoped); scope.Dispose(); return saved; }
                catch { scope.Dispose(); throw; }
            }
            if (c.Call == 620)
            {
                if (c.Arg1 != ProcessSystemSettings.SnapshotBytes || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, ProcessSystemSettings.SnapshotBytes, true)) { return -14; }
                Transfer(c.Arg0, ProcessSystemSettings.Snapshot(Zenith.Core.Configuration.SystemConfiguration.Settings), true); return 0;
            }
            if (c.Call == 621)
            {
                if (c.Arg1 != ProcessSystemSettings.RequestBytes || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, ProcessSystemSettings.RequestBytes, false)) { return -14; }
                return ProcessSystemSettings.Save(Zenith.Core.Configuration.SystemConfiguration.Settings, ReadUser(c.Arg0, ProcessSystemSettings.RequestBytes));
            }
            if (c.Call == 618)
            {
                if (c.Arg1 != 48 || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, 48, true)) { return -14; }
                Transfer(c.Arg0, ProcessAppearance.MaterialControlSnapshot(Zenith.Core.Configuration.SystemConfiguration.Appearance), true); return 0;
            }
            if (c.Call == 619)
            {
                if (c.Arg1 != 40 || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, 40, false)) { return -14; }
                return ProcessAppearance.SaveMaterialControl(Zenith.Core.Configuration.SystemConfiguration.Appearance, ReadUser(c.Arg0, 40));
            }
            if (c.Call is 608 or 616)
            {
                if (c.Arg1 != 48 || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, 48, true)) { return -14; }
                Transfer(c.Arg0, ProcessAppearance.MaterialSnapshot(Zenith.Core.Configuration.SystemConfiguration.Appearance, c.Call == 616), true);return 0;
            }
            if (c.Call is 609 or 617)
            {
                if (c.Arg1 != 32 || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, 32, false)) { return -14; }
                return ProcessAppearance.SaveMaterials(Zenith.Core.Configuration.SystemConfiguration.Appearance, ReadUser(c.Arg0, 32), c.Call == 617);
            }
            if (c.Call == 607)
            {
                if (c.Arg1 != 32 || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, 32, true)) { return -14; }
                if (!graphics.SupportsMaterials) { return -19; }
                Transfer(c.Arg0, ProcessMaterialFrame.Capabilities(), true); return 0;
            }
            if (c.Call == 606)
            {
                if (c.Arg0 > int.MaxValue) { return -9; }
                if (c.Arg2 != ProcessMaterialFrame.Bytes) { return -22; }
                if (!Valid(c.Arg1, ProcessMaterialFrame.Bytes, false)) { return -14; }
                if (!ProcessMaterialFrame.TryRead(ReadUser(c.Arg1, ProcessMaterialFrame.Bytes), out var frame)) { return -22; }
                int size = graphics.Size((int)c.Arg0);
                if (size < 0) { return size; }
                if (frame.Length != (ulong)size) { return -22; }
                int regionBytes = frame.Count * ProcessMaterialRegion.Bytes;
                if (!Valid(frame.Pixels, size, false) || (regionBytes != 0 && !Valid(frame.Regions, regionBytes, false))) { return -14; }
                byte[] regions = regionBytes == 0 ? Array.Empty<byte>() : ReadUser(frame.Regions, regionBytes);
                if (!ProcessMaterialRegion.TryRead(regions, frame.Count, out var materialRegions)) { return -22; }
                int result = graphics.PresentMaterials((int)c.Arg0, ReadGraphics(frame.Pixels, size), materialRegions);
                Pace(graphics.Pacer.Present(MonotonicMilliseconds)); return result;
            }
            if (c.Call == 601)
            {
                if (c.Arg0 > int.MaxValue) { return -9; }
                int size = graphics.Size((int)c.Arg0);
                if (size < 0) { return size; }
                if (c.Arg2 != (ulong)size) { return -22; }
                if (!Valid(c.Arg1, size, false)) { return -14; }
                int result = graphics.Present((int)c.Arg0, ReadGraphics(c.Arg1, size));
                Pace(graphics.Pacer.Present(MonotonicMilliseconds)); return result;
            }
            if (c.Call == 602)
            {
                if (c.Arg0 > int.MaxValue) { return -9; }
                if (c.Arg2 != ProcessGraphicsEvent.Bytes) { return -22; }
                if (!Valid(c.Arg1, ProcessGraphicsEvent.Bytes, true)) { return -14; }
                var e = graphics.Poll((int)c.Arg0);
                if (e is null) { return -9; }
                if (e.Value.Type == 0) { graphics.WaitForEvent((int)c.Arg0, graphics.Pacer.EmptyPoll()); }
                Transfer(c.Arg1, e.Value.Encode(), true); return e.Value.Type == 0 ? 0 : 1;
            }
            if (c.Call == 603)
            { return c.Arg0 > int.MaxValue || c.Arg1 != 0 || c.Arg2 != 0 ? -22 : graphics.Close((int)c.Arg0); }
            if (c.Call == 500)
            {
                if (c.Arg1 != ProcessNetworkRequest.Bytes || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, ProcessNetworkRequest.Bytes, false)) { return -14; }
                if (!ProcessNetworkRequest.TryRead(ReadUser(c.Arg0, ProcessNetworkRequest.Bytes), out var r)) { return -22; }
                if ((r.InputLength > 0 && !Valid(r.Input, r.InputLength, false))
                    || (r.TargetLength > 0 && !Valid(r.Target, r.TargetLength, false))
                    || (r.Capacity > 0 && !Valid(r.Output, r.Capacity, true))) { return -14; }
                var utf8 = new UTF8Encoding(false, true);
                string input = r.InputLength == 0 ? "" : utf8.GetString(ReadUser(r.Input, r.InputLength));
                string? target = r.TargetLength == 0 ? null : utf8.GetString(ReadUser(r.Target, r.TargetLength));
                if (input.Contains('\0') || target?.Contains('\0') == true) { return -22; }
                string? output = null;
                switch (r.Operation)
                {
                    case ProcessNetworkOperation.Status: output = network.Status(); break;
                    case ProcessNetworkOperation.Error: output = network.LastError; break;
                    case ProcessNetworkOperation.Resolve: output = network.Resolve(input, target, (int)r.Value); break;
                    case ProcessNetworkOperation.Echo: return network.Echo(input);
                    case ProcessNetworkOperation.Connect: return network.Connect(input, (int)r.Value, r.Flags != 0);
                    case ProcessNetworkOperation.Download: return network.Download(files.Resolve(input), r.Flags != 0, (int)r.Value);
                    case ProcessNetworkOperation.Commit: return network.Commit((int)r.Value, (int)r.Extra);
                }
                byte[] result = Encoding.UTF8.GetBytes(output ?? "");
                if (result.Length > r.Capacity) { return -34; }
                Transfer(r.Output, result, true); return result.Length;
            }
            if (c.Call is 501 or 502)
            {
                if (c.Arg0 > int.MaxValue) { return -9; }
                if (c.Arg2 > ProcessNetwork.MaximumIo) { return -22; }
                if (!Valid(c.Arg1, (int)c.Arg2, c.Call == 501)) { return -14; }
                if (c.Call == 502) { return network.Write((int)c.Arg0, ReadUser(c.Arg1, (int)c.Arg2)); }
                byte[] result = new byte[(int)c.Arg2]; int count = network.Read((int)c.Arg0, result);
                if (count > 0) { Transfer(c.Arg1, result.AsSpan(0, count).ToArray(), true); }
                return count;
            }
            if (c.Call == 503)
            { return c.Arg0 > int.MaxValue || c.Arg1 != 0 || c.Arg2 != 0 ? -22 : network.Close((int)c.Arg0); }
            if (c.Call == 405)
            {
                if (c.Arg1 != ProcessPathRequest.Bytes || c.Arg2 != 0) { return -22; }
                if (!Valid(c.Arg0, ProcessPathRequest.Bytes, false)) { return -14; }
                if (!ProcessPathRequest.TryRead(ReadUser(c.Arg0, ProcessPathRequest.Bytes), out var request)) { return -22; }
                if (!Valid(request.Path, request.Length, false) || (request.TargetLength > 0 && !Valid(request.Target, request.TargetLength, false))) { return -14; }
                var utf8 = new UTF8Encoding(false, true);
                string path = utf8.GetString(ReadUser(request.Path, request.Length));
                string? target = request.TargetLength == 0 ? null : utf8.GetString(ReadUser(request.Target, request.TargetLength));
                if (path.Contains('\0') || target?.Contains('\0') == true) { return -22; }
                return files.Mutate(request.Operation, path, target, request.Value, request.Extra);
            }
            if (c.Call == 407)
            {
                if (c.Arg0 > int.MaxValue) { return -9; }
                if (c.Arg2 is 0 or > 1024) { return -36; }
                if (!Valid(c.Arg1, (int)c.Arg2, false)) { return -14; }
                string path = new UTF8Encoding(false, true).GetString(ReadUser(c.Arg1, (int)c.Arg2));
                return files.OpenCopyDestination((int)c.Arg0, path);
            }
            if (c.Call == 406)
            { return c.Arg0 > int.MaxValue ? -9 : c.Arg2 > 2 ? -22 : files.Seek((int)c.Arg0, unchecked((long)c.Arg1), (int)c.Arg2); }
            if (c.Call == 403)
            {
                if (c.Arg1 > ProcessFiles.MaximumIo || c.Arg2 > 8192) { return -22; }
                if (!Valid(c.Arg0, (int)c.Arg1, true)) { return -14; }
                if (_processSnapshot is null)
                {
                    var text = new StringBuilder("PID UID STATE STATUS PROGRAM\n");
                    foreach (var item in Processes.List(Zenith.Core.Security.CredentialContext.Current.Uid))
                    {
                        text.Append(item.Pid).Append(' ').Append(item.Uid).Append(' ').Append(item.State.ToString().ToLowerInvariant()).Append(' ')
                        .Append(item.Status?.ToString() ?? "-").Append(' ').Append(item.Name).Append('\n');
                    }
                    _processSnapshot = Encoding.UTF8.GetBytes(text.ToString());
                }
                int offset = (int)c.Arg2;
                int count = Math.Min((int)c.Arg1, Math.Max(0, _processSnapshot.Length - offset));
                if (count > 0) { Transfer(c.Arg0, _processSnapshot.AsSpan(offset, count).ToArray(), true); }
                return count;
            }
            if (c.Call == 404)
            {
                if (c.Arg0 == 0 || c.Arg0 > int.MaxValue || c.Arg1 != 0 || c.Arg2 != 0) { return -22; }
                return Processes.RequestStop((int)c.Arg0, Zenith.Core.Security.CredentialContext.Current.Uid);
            }
            if (c.Call is 401 or 402)
            {
                if (c.Arg0 > int.MaxValue) { return -9; }
                if (c.Call == 402 && c.Arg1 > int.MaxValue) { return -22; }
                return files.TerminalMode((int)c.Arg0, c.Call == 402 ? (int)c.Arg1 : null);
            }
            if (c.Call == 410) { return c.Arg0 != 0 || c.Arg1 != 0 || c.Arg2 != 0 ? -22 : files.TerminalSize(); }
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
            if (c.Call is 2 or 4 or 408)
            {
                if (c.Arg1 == 0 || c.Arg1 > 1024) { return -36; }
                if (!Valid(c.Arg0, (int)c.Arg1, false)) { return -14; }
                string path = new UTF8Encoding(false, true).GetString(ReadUser(c.Arg0, (int)c.Arg1));
                if (c.Call == 2) { return files.Open(path, c.Arg2); }
                if (!Valid(c.Arg2, ProcessFiles.StatBytes, true)) { return -14; }
                byte[] stat = new byte[ProcessFiles.StatBytes]; files.Stat(path, stat, c.Call != 408); Transfer(c.Arg2, stat, true); return 0;
            }
            if (c.Call == 0 || c.Call == 217)
            {
                if (c.Arg0 > int.MaxValue) { return -9; }
                if (c.Arg2 > ProcessFiles.MaximumIo) { return -22; }
                int count = (int)c.Arg2;
                if (!Valid(c.Arg1, count, true)) { return -14; }
                bool stdin = c.Call == 0 && c.Arg0 == 0;
                ref int transferred = ref (_largeNative && c.Call == 217 ? ref directoryBytes : ref readBytes);
                int readLimit = _largeNative && c.Call == 0 ? ProcessFiles.LargeReadTransfer : ProcessFiles.MaximumTransfer;
                if (!stdin && transferred + count > readLimit) { return -28; }
                byte[] buffer = new byte[count];
                int result = c.Call == 0 ? files.Read((int)c.Arg0, buffer) : files.Next((int)c.Arg0, buffer);
                if (result > 0) { if (!stdin) { transferred += result; } Transfer(c.Arg1, buffer.AsSpan(0, result).ToArray(), true); }
                return result;
            }
            return -38;
        }
        catch (Exception error)
        {
            // Cosmos NativeAOT typed catches are unreliable; classify explicitly.
            if (c.Call is >= 500 and <= 503) { network.Error(error.Message); }
            if (error is OperationCanceledException) { throw; }
            if (error is BrokenPipeException) { return -32; }
            if (error is UnauthorizedAccessException) { return -13; }
            if (error is FileNotFoundException || error is DirectoryNotFoundException) { return -2; }
            if (error is ArgumentException) { return -22; }
            return -5;
        }
    }
    public static (int Status, string Error, ulong Fault) Run(string name, IReadOnlyList<string> arguments, Func<bool> cancelled, object? lifetimeRoot = null, string cwd = "/", string? input = null, ProcessStreams? streams = null, IReadOnlyDictionary<string, string>? environment = null, Action? requestStop = null, NativeWindowPlacement? placement = null)
    {
        // C5.3: under smp=managed the program and its system-call service run together on a placed thread,
        // which enters its AP's process bridge itself (CpuWorkers.RunProcessSlice). The caller waits here.
        // An explicit ZENITH_CPU_MASK keeps the BSP-owned slice protocol and its placement guarantees.
        if (!Zenith.Core.Multiprocessing.CpuWorkers.PlaceProcesses || environment?.ContainsKey("ZENITH_CPU_MASK") == true) { return RunHere(name, arguments, cancelled, lifetimeRoot, cwd, input, streams, environment, requestStop, placement); }
        var identity = Zenith.Core.Security.CredentialContext.Current;
        (int, string, ulong) result = (1, "native program did not start", 0); Exception? failure = null;
        var thread = Zenith.Core.Multiprocessing.CpuWorkers.StartAnywhere(() =>
        {
            var credentials = Zenith.Core.Security.CredentialContext.Enter(identity);
            try { result = RunHere(name, arguments, cancelled, lifetimeRoot, cwd, input, streams, environment, requestStop, placement); }
            catch (Exception error) { failure = error; }
            credentials.Dispose();   // explicit: Cosmos skips finally while unwinding
        }, "native " + name);
        thread.Join();
        if (failure is not null) { throw failure; }
        return result;
    }

    private Region[] LoadImage(string path, PackageFile owned, bool large, Func<bool> cancelled, out ElfImage image)
    {
        var store = new NativePackageStore();
        using var stream = store.OpenRead(path, large ? ElfImage.MaximumBytes : ElfImage.LegacyMaximumBytes, out int size);
        try
        {
            if (size != owned.Length || store.Mode(path) != owned.Mode)
            { throw new IOException("native executable differs from its installed package"); }
            var metadata = new List<ElfImage.Metadata>();
            var layout = ElfImage.Read(stream, size, large, metadata);
            _largeNative = large;
            Initialize();
            var regions = new Region[layout.Segments.Count + 2];
            int region = 0;
            foreach (var segment in layout.Segments)
            {
                ulong end = segment.Address + (ulong)segment.Pages * 4096;
                bool writable = (segment.Flags & 2) != 0;
                _ranges.Add(new(segment.Address, end, writable));
                for (ulong address = segment.Address; address < end; address += 4096)
                {
                    if (cancelled()) { throw new OperationCanceledException(); }
                    Map(address, Page(), writable, (segment.Flags & 1) != 0);
                }
                regions[region++] = new() { Start = segment.Address, End = end };
            }

            // Validate the layout against the very same bytes sent to the hash and private pages.
            // No executable bytes become runnable until this stream matches the signed digest.
            Span<byte> digest = stackalloc byte[32];
            Zenith.Core.Security.Sha256.HashStream(stream, size, digest, (buffer, offset, count) =>
            {
                if (cancelled()) { throw new OperationCanceledException(); }
                foreach (var record in metadata)
                {
                    int from = Math.Max(record.Offset, offset);
                    int to = Math.Min(record.Offset + record.Bytes.Length, offset + count);
                    if (to > from && !buffer.AsSpan(from - offset, to - from).SequenceEqual(record.Bytes.AsSpan(from - record.Offset, to - from)))
                    { throw new IOException("native executable metadata changed during load"); }
                }
                foreach (var segment in layout.Segments)
                {
                    int from = Math.Max(segment.Offset, offset);
                    int to = Math.Min(segment.Offset + segment.FileBytes, offset + count);
                    if (to > from) { Transfer(segment.Address + (ulong)(from - segment.Offset), buffer.AsSpan(from - offset, to - from), true); }
                }
            });
            if (!Convert.ToHexString(digest).Equals(owned.Digest, StringComparison.OrdinalIgnoreCase))
            { throw new IOException("native executable differs from its installed package"); }
            image = layout;
            return regions;
        }
        catch { stream.Dispose(); throw; }
    }

    private static (int Status, string Error, ulong Fault) RunHere(string name, IReadOnlyList<string> arguments, Func<bool> cancelled, object? lifetimeRoot, string cwd, string? input, ProcessStreams? streams, IReadOnlyDictionary<string, string>? environment, Action? requestStop, NativeWindowPlacement? placement)
    {
#if ARCH_ARM64
        throw new IOException("native ELF process execution is currently x86_64 only");
#else
        // Cosmos exception dispatch is not safe for overlapping managed unwinds.
        // Serialize expected setup/VFS errors, converting them before releasing the
        // gate. Never hold this gate across blocking standard-stream operations.
        PackageFile owned = null!; ProcessArguments stack = null!; bool large = false;
        string path = "/usr/share/zenith/native/" + name + ".elf";
        string? setupError = null;
        Monitor.Enter(s_serviceGate);
        try
        {
            if (!PackageManifest.ValidName(name)) { throw new IOException("invalid native program name"); }
            var manager = new PackageManager(new NativePackageStore());
            var candidate = manager.List().Select(item => item.Manifest)
                .SelectMany(manifest => manifest.Files.Select(file => (Manifest: manifest, File: file)))
                .FirstOrDefault(item => item.File.Path == path);
            owned = candidate.File ?? throw new IOException("native executable must be installed by a signed package");
            if (owned.Mode != 0x1ed) { throw new IOException("native executable requires signed executable permissions"); }
            large = candidate.Manifest!.LargeNative;
            stack = ProcessArguments.Create(arguments, environment);
        }
        catch (Exception error) { setupError = error.Message; }
        Monitor.Exit(s_serviceGate);
        if (setupError is not null) { return (1, setupError, 0); }
        uint affinity;
        try { affinity = ProcessPlacement.Parse(environment); }
        catch (ArgumentException error) { return (1,error.Message,0); }
        int pid = -1;
        var identity = Zenith.Core.Security.CredentialContext.Current;
        bool maintenance;
        lock (Installer.MaintenanceGate)
        {
            // A pkg download or check no longer blocks launches; only the file commit does.
            maintenance = Installer.IsRunning || Zenith.Core.Packages.PackageManager.Committing
                || Zenith.Core.Boot.NativeKernelUpdates.IsRunning;
            if (!maintenance) { pid = Processes.Start(name, identity.Uid, requestStop ?? (() => { })); }
        }
        if (pid < 0) { return (1, ProcessTable.Refusal(maintenance), 0); }
        bool released = false;
        NativeProcess? process = null; ProcessFiles? files = null; ProcessNetwork? network = null; ProcessGraphics? graphics = null;
        try
        {
            if (cancelled()) { Release(pid, ref released, 130); return (130, "", 0); }
            process = new NativeProcess();
            ElfImage image = null!;
            Region[] regions = null!;
            Monitor.Enter(s_serviceGate);
            try { regions = process.LoadImage(path, owned, large, cancelled, out image); }
            catch (Exception error) { setupError = error.Message; }
            Monitor.Exit(s_serviceGate);
            if (setupError is not null)
            {
                process.Dispose();
                int failure = cancelled() ? 130 : 1;
                Release(pid, ref released, failure);
                return (failure, failure == 130 ? "" : setupError, 0);
            }
            int region = image.Segments.Count;
            ulong stackBase = ProcessArguments.Top - ProcessArguments.StackBytes;
            for (int offset = 0; offset < stack.Bytes.Length; offset += 4096)
            {
                byte* frame = process.Page(); stack.Bytes.AsSpan(offset, 4096).CopyTo(new Span<byte>(frame, 4096));
                process.Map(stackBase + (ulong)offset, frame, true, false);
            }
            regions[region] = new() { Start = stackBase, End = ProcessArguments.Top };
            process._ranges.Add(new(stackBase, ProcessArguments.Top, true));
            files = new ProcessFiles(new NativeProcessFiles(), cwd, input, streams);
            network = new ProcessNetwork(new NativeProcessNetworkSource(() => { if (cancelled()) { throw new OperationCanceledException(); } }), pid);
            graphics = NativeProcessGraphics.Create(identity.Uid, placement ?? new NativeWindowPlacement(name, null));
            // The current managed call stack is suspended while the CPU runs the
            // freestanding image. Keep its owner and allocations explicitly rooted.
            lock (s_rootsGate) { s_roots.Add(pid, new object[] { process, regions, arguments, image, stack, lifetimeRoot ?? arguments, files, network, graphics }); }
            fixed (Region* list = regions)
            {
                var context = new Context
                {
                    Cr3 = process.Physical(process._root),
                    Entry = image.Entry,
                    Stack = stack.StackPointer,
                    Regions = list,
                    Count = (ulong)regions.Length,
                    Ticks = 1000,
                    InterruptStack = 0,
                    Hhdm = process._hhdm,
                    Uid = Zenith.Core.Security.CredentialContext.Current.Uid,
                    Gid = Zenith.Core.Security.CredentialContext.Current.Gid,
                    Pid = (ulong)pid,
                    Frame = process.Allocate(176),
                    UserFp = process.Page()
                };
                int status; int calls = 0, networkCalls = 0, graphicsCalls = 0, readBytes = 0, directoryBytes = 0;
                uint serviced = 0; // every kind of call: the budget counters above stand still for the others
                Processes.SetState(pid, ProcessState.Running);
                do
                {
                    if (cancelled()) { status = 130; break; }
                    if (!process._interactive.Advance(graphics!.Live, MonotonicMilliseconds, out bool renew)) { status = 130; break; }
                    if (renew)
                    // A live window app may use its CPU for the whole minute: it is on screen and closing
                    // it stops it. 1000 quanta (10 s a minute) killed apps that keep drawing, such as
                    // Observatory's gliding graphs, with status 124. Windowless programs keep the watchdog.
                    { calls = graphicsCalls = readBytes = directoryBytes = 0; files.RenewBudget(); graphics.RenewBudget(); context.Ticks = ProcessInteractive.LiveQuantaPerWindow; }
                    status = Zenith.Core.Multiprocessing.CpuWorkers.RunProcessSlice((ulong)&context, affinity, cancelled, out int cpu);
                    if (status == -1)
                    {
                        Monitor.Enter(s_rawGate);
                        try { status = (int)RunNative(&context); Monitor.Exit(s_rawGate); }
                        catch { Monitor.Exit(s_rawGate); throw; }
                        cpu = 0;
                    }
                    Processes.SetExecution(pid,cpu,affinity);
                    // A device IRQ interrupted the user window and has been delivered
                    // to its managed driver; resume the unchanged frame.
                    if (status == DeviceInterruptYield) { continue; }
                    if (status != 4096) { break; }
                    if (cancelled()) { status = 130; break; }
                    if ((context.Call is >= 600 and <= 607 or >= 613 and <= 615 or 628 or 629 or >= 630 and <= 639 or 645 or 646 or 659 or >= 661 and <= 672) ? ++graphicsCalls > ProcessGraphics.MaximumCalls : context.Call is >= 500 and <= 503 ? ++networkCalls > ProcessNetwork.MaximumCalls : ++calls > (large ? ProcessFiles.LargeCallBudget : 4096)) { status = 124; break; }
                    // A portal grant serves that area's calls as root for this process alone (uid 0 in the
                    // context too, which those handlers read); every other call keeps the program's identity.
                    bool elevated = Zenith.Core.Portal.ProcessPrivileges.Elevates(pid, context.Call, context.Arg0);
                    ulong ownUid = context.Uid, ownGid = context.Gid;
                    if (elevated) { context.Uid = 0; context.Gid = 0; }
                    var caller = Zenith.Core.Security.CredentialContext.Enter(elevated ? Zenith.Core.Security.Credentials.Root : identity);
                    // Transport reads park like stdin; never hold the filesystem service gate
                    // while waiting for a peer. Mutations and transport setup stay serialized.
                    bool standard = context.Call is >= 661 and <= 671 or 642 or 643 or 644 or 649 or 651 or 653 or 657 or 658 or 400 or 401 or 402 or 403 or 404 or 410 or 501 or 600 or 601 or 602 or 603 or 604 or 605 or 606 or 607 or 628 or 629 or 630 or 631 or 632 or 633 or 634 or 635 or 636 or 637 or 638 or 639 or 645 or 646 or 659 || (context.Call == 672 && context.Arg1 is 1 or 3) || ((context.Call is 0 or 1 or 3) && context.Arg0 <= 2);
                    if (!standard) { Monitor.Enter(s_serviceGate); }
                    try
                    {
                        Processes.SetState(pid, ProcessState.Waiting);
                        context.Result = process.Service(ref context, files, network!, graphics!, ref readBytes, ref directoryBytes);
                        Processes.SetState(pid, ProcessState.Running);
                        if (!standard) { Monitor.Exit(s_serviceGate); }
                        caller.Dispose(); context.Uid = ownUid; context.Gid = ownGid;
                    }
                    catch
                    {
                        if (!standard) { Monitor.Exit(s_serviceGate); }
                        caller.Dispose(); context.Uid = ownUid; context.Gid = ownGid; throw;
                    }
                    // Give console/cancellation workers a scheduling point during
                    // syscall-heavy loops; Thread.Yield is a stub in Cosmos 3.0.89.
                    if ((++serviced & 15) == 0) { Thread.Sleep(1); }
                } while (true);
                GC.KeepAlive(s_roots);
                ulong fault = context.Fault;
                graphics?.Dispose(); network?.Dispose(); CloseFiles(files); process.Dispose(); int exit = cancelled() ? 130 : status;
                Release(pid, ref released, exit); return (exit, "", fault);
            }
        }
        catch { graphics?.Dispose(); network?.Dispose(); CloseFiles(files); process?.Dispose(); Release(pid, ref released, 1); throw; }
#endif
    }
}
