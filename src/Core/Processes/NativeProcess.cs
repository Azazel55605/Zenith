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
    private byte[]? _processSnapshot;
    private byte[]? _graphicsTransfer;
    private const ulong FramebufferAddress = 0x10000000;
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
        Zenith.Core.Configuration.SystemConfiguration.Display.Abandon(pid);
        Processes.Complete(pid, status);
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
        var table = _root;
        for (int shift = 39; shift >= 21; shift -= 9)
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
    private readonly ProcessInteractive _interactive = new();
    private static long MonotonicMilliseconds => System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000);
    private static void Pace(int milliseconds) { if (milliseconds > 0) { Thread.Sleep(milliseconds); } }
    private long Service(ref Context c, ProcessFiles files, ProcessNetwork network, ProcessGraphics graphics, ref int readBytes)
    {
        try
        {
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
            if (c.Call == 630)
            {
                if (c.Arg0 > int.MaxValue || c.Arg1 != 0 || c.Arg2 != 0) { return -22; }
                if (!graphics.Live) { return -19; }
                int terminal = graphics.Terminals.Open(graphics.TerminalHost, (int)c.Arg0);
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
            if (c.Call == 634) { return c.Arg1 > int.MaxValue || c.Arg2 != 0 ? -22 : graphics.Terminals.Resize((long)c.Arg0, (int)c.Arg1); }
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
            { return c.Arg0 > int.MaxValue || c.Arg1 != 1 || c.Arg2 != 0 ? -22 : graphics.EnablePointerMotion((int)c.Arg0); }
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
                return ProcessScopedAppearance.Save(Zenith.Core.Configuration.SystemConfiguration.Appearance,Zenith.Core.Configuration.SystemConfiguration.PersonalAppearance,ReadUser(c.Arg0,48));
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
            if (c.Call is >= 500 and <= 503) { network.Error(error.Message); }
            if (error is OperationCanceledException) { throw; }
            if (error is BrokenPipeException) { return -32; }
            if (error is UnauthorizedAccessException) { return -13; }
            if (error is FileNotFoundException || error is DirectoryNotFoundException) { return -2; }
            if (error is ArgumentException) { return -22; }
            return -5;
        }
    }
    public static (int Status, string Error, ulong Fault) Run(string name, IReadOnlyList<string> arguments, Func<bool> cancelled, object? lifetimeRoot = null, string cwd = "/", string? input = null, ProcessStreams? streams = null, IReadOnlyDictionary<string, string>? environment = null, Action? requestStop = null)
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
            image = ElfImage.Read(bytes); stack = ProcessArguments.Create(arguments, environment);
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
            process = new NativeProcess(); process.Initialize(); var regions = new Region[image.Segments.Count + 2]; int region = 0;
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
            network = new ProcessNetwork(new NativeProcessNetworkSource(() => { if (cancelled()) { throw new OperationCanceledException(); } }));
            graphics = NativeProcessGraphics.Create(identity.Uid);
            // The current managed call stack is suspended while the CPU runs the
            // freestanding image. Keep its owner and allocations explicitly rooted.
            lock (s_rootsGate) { s_roots.Add(pid, new object[] { process, regions, arguments, bytes, image, stack, lifetimeRoot ?? arguments, files, network, graphics }); }
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
                int status; int calls = 0, networkCalls = 0, graphicsCalls = 0, readBytes = 0;
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
                    { calls = graphicsCalls = readBytes = 0; files.RenewBudget(); graphics.RenewBudget(); context.Ticks = ProcessInteractive.LiveQuantaPerWindow; }
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
                    if ((context.Call is >= 600 and <= 607 or >= 613 and <= 615 or 628 or 629 or >= 630 and <= 638 or 645 or 646) ? ++graphicsCalls > ProcessGraphics.MaximumCalls : context.Call is >= 500 and <= 503 ? ++networkCalls > ProcessNetwork.MaximumCalls : ++calls > 4096) { status = 124; break; }
                    var caller = Zenith.Core.Security.CredentialContext.Enter(identity);
                    // Transport reads park like stdin; never hold the filesystem service gate
                    // while waiting for a peer. Mutations and transport setup stay serialized.
                    bool standard = context.Call is 642 or 643 or 644 or 649 or 400 or 401 or 402 or 403 or 404 or 501 or 600 or 601 or 602 or 603 or 604 or 605 or 606 or 607 or 628 or 629 or 630 or 631 or 632 or 633 or 634 or 635 or 636 or 637 or 638 or 645 or 646 || ((context.Call is 0 or 1 or 3) && context.Arg0 <= 2);
                    if (!standard) { Monitor.Enter(s_serviceGate); }
                    try
                    {
                        Processes.SetState(pid, ProcessState.Waiting);
                        context.Result = process.Service(ref context, files, network!, graphics!, ref readBytes);
                        Processes.SetState(pid, ProcessState.Running);
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
