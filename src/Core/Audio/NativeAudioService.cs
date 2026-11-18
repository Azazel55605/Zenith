using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading;
using Zenith.Core.Security;
using Zenith.Core.Portal;

namespace Zenith.Core.Audio;

/// <summary>H2: the BSP owns HDA and fixed DMA rings. Native apps exchange copied PCM only.
/// Controls are session-local; no hardware addresses, microphone samples or other users' streams
/// leave this service. No audio work is performed by the desktop frame loop.</summary>
internal static unsafe class AudioService
{
    public const int SnapshotBytes = 4096;
    private sealed class Device
    {
        public uint Id;
        public int Direction, Volume = 80, Peak;
        public bool Muted;
        public string Name = "";
    }
    private static readonly object s_gate = new();
    private static readonly AudioMixer s_mixer = new();
    private static readonly Device?[] s_devices = new Device?[16];
    private static readonly uint[] s_default = new uint[2];
    private static readonly byte[] s_period = new byte[AudioMixer.PeriodBytes];
    private static bool s_available;
    private static volatile bool s_stopping;
    private static ulong s_revision = 1;
    private static nint s_dmaOwner;
    private static int s_count, s_testFrames, s_tonePhase;
    private static long s_errors;
    private static int s_sessionGeneration;
    public static string Diagnostic { get; private set; } = "No supported audio device";
#if !ARCH_ARM64
    [DllImport("*", EntryPoint = "zenith_hda_probe"), SuppressGCTransition] private static extern ulong Probe();
    [DllImport("*", EntryPoint = "zenith_hda_initialize"), SuppressGCTransition] private static extern ulong InitializeNative(ulong mmio, ulong dma, ulong physical);
    [DllImport("*", EntryPoint = "zenith_hda_field"), SuppressGCTransition] private static extern ulong Field(ulong device, ulong field);
    [DllImport("*", EntryPoint = "zenith_hda_select"), SuppressGCTransition] private static extern ulong Select(ulong direction, ulong device);
    [DllImport("*", EntryPoint = "zenith_hda_run"), SuppressGCTransition] private static extern ulong Run(ulong direction, ulong enable);
    [DllImport("*", EntryPoint = "zenith_hda_period"), SuppressGCTransition] private static extern long Period(ulong direction);
    [DllImport("*", EntryPoint = "zenith_hda_transfer"), SuppressGCTransition] private static extern ulong TransferNative(ulong direction, ulong period, byte* buffer);
#endif
    public static void Initialize()
    {
        s_sessionGeneration = Accounts.Generation;
#if !ARCH_ARM64
        ulong bar = Probe();
        if (bar == 0) { Log.Write("audio", Diagnostic); return; }
        Cosmos.Kernel.Core.X64.Cpu.DeviceMapper.EnsureMapped(bar);
        var hhdm = Cosmos.Kernel.Boot.Limine.Limine.HHDM.Response;
        if (hhdm == null) { return; }
        // Lifetime-owned, physically contiguous descriptor + output + input storage (68 KiB).
        s_dmaOwner = (nint)NativeMemory.Alloc(18 * 4096);
        void* dma = s_dmaOwner == 0 ? null : (void*)(((ulong)s_dmaOwner + 4095) & ~4095UL);
        if (dma == null) { Diagnostic = "Audio DMA memory unavailable"; return; }
        if ((ulong)dma < hhdm->Offset || (ulong)dma >= 0xffffffff80000000UL)
        { NativeMemory.Free((void*)s_dmaOwner); s_dmaOwner = 0; Diagnostic = "Audio DMA is not HHDM-backed"; return; }
        new Span<byte>(dma, 17 * 4096).Clear();
        // Cosmos NativeMemory large allocations are contiguous page runs in the HHDM.
        ulong physical = (ulong)dma - hhdm->Offset;
        s_count = (int)Math.Min(16UL, InitializeNative(bar + hhdm->Offset, (ulong)dma, physical));
        for (int i = 0; i < s_count; i++)
        {
            uint cfg = (uint)Field((ulong)i, 2), codec = (uint)Field((ulong)i, 3), pin = (uint)Field((ulong)i, 4);
            int direction = (int)Field((ulong)i, 1);
            string kind = (cfg >> 20 & 15) switch { 0 => "Line out", 1 => "Speakers", 2 => "Headphones", 8 => "Line in", 10 => "Microphone", _ => direction == 0 ? "Output" : "Input" };
            var device = new Device { Id = (uint)Field((ulong)i, 0), Direction = direction, Name = "HD Audio " + kind + " (" + codec + ":" + pin + ")" };
            s_devices[i] = device;
            if (Field((ulong)i, 5) != 0) { s_default[direction] = device.Id; }
        }
        s_available = s_count > 0;
        if (!s_available) { NativeMemory.Free((void*)s_dmaOwner); s_dmaOwner = 0; }
        Diagnostic = s_available ? "Intel HD Audio; stereo 48000 Hz S16LE" : "HD Audio codec has no supported stereo 48 kHz PCM route";
        Log.Write("audio", Diagnostic + "; endpoints=" + s_count);
        if (s_available)
        {
            // Ordinary threads stay on BSP; never use StartAnywhere for driver I/O.
            new Thread(Pump) { IsBackground = true, Name = "audio" }.Start();
        }
#endif
    }
    private static Device? Find(uint id)
    {
        for (int i = 0; i < s_count; i++) { if (s_devices[i]!.Id == id) { return s_devices[i]; } }
        return null;
    }
    private static bool Session(uint uid) => uid == 0 || (uid != 65534 && uid == Accounts.Session.Uid && !Zenith.Core.Sessions.SessionRuntime.IsLogin);
    private static bool Recording(int pid, uint uid) => uid == 0 || ProcessPrivileges.Holds(pid, Privilege.AudioRecord);
    private static void ObserveSession()
    {
        if (s_sessionGeneration == Accounts.Generation) { return; }
        s_sessionGeneration = Accounts.Generation;
        for (int i = 0; i < s_mixer.Streams.Length; i++) { s_mixer.Streams[i] = null; }
        s_testFrames = 0; s_revision++;
        foreach (var device in s_devices) { if (device is not null) { device.Peak = 0; } }
    }
    public static long Open(int pid, uint uid, string name, int direction, uint device)
    {
        lock (s_gate)
        {
            ObserveSession();
            if (direction is < 0 or > 1) { return -22; }
            if (!Session(uid)) { return -13; }
            if (direction == 1 && !Recording(pid, uid)) { return -13; }
            if (!s_available || s_stopping || s_default[direction] == 0) { return -19; }
            // One selected physical route per direction. Apps follow the user's device choice.
            if (device != 0 && device != s_default[direction]) { return -19; }
            long handle = s_mixer.Open(pid, uid, name, direction);
            if (handle > 0) { s_revision++; Monitor.Pulse(s_gate); }
            return handle;
        }
    }
    public static int Pcm(int pid, uint uid, long handle, Span<byte> bytes, bool write)
    {
        lock (s_gate)
        {
            ObserveSession();
            var stream = s_mixer.Find(handle);
            if (stream is null || stream.Pid != pid) { return -9; }
            if (!Session(uid) || (!write && !Recording(pid, uid))) { s_mixer.Close(pid, handle); s_revision++; return -13; }
            if (s_default[stream.Direction] == 0 || !s_available || s_stopping) { return -19; }
            return s_mixer.Transfer(pid, handle, bytes, write);
        }
    }
    public static int Close(int pid, long handle)
    {
        lock (s_gate) { int result = s_mixer.Close(pid, handle); if (result == 0) { s_revision++; } return result; }
    }
    public static void Release(int pid)
    {
        lock (s_gate) { s_mixer.Release(pid); s_revision++; }
    }
    private static void U32(Span<byte> b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b[o..], v);
    private static void U64(Span<byte> b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b[o..], v);
    private static void Text(Span<byte> b, int o, string value)
    {
        int n = Math.Min(63, value.Length);
        for (int i = 0; i < n; i++) { char c = value[i]; b[o + i] = c is >= ' ' and <= '~' ? (byte)c : (byte)'?'; }
    }
    public static byte[] Snapshot(uint uid)
    {
        var b = new byte[SnapshotBytes];
        lock (s_gate)
        {
            ObserveSession();
            U32(b, 0, 1); U32(b, 4, (uint)s_count); U64(b, 16, s_revision);
            U32(b, 24, s_default[0]); U32(b, 28, s_default[1]); U32(b, 32, s_available ? 1u : 0u);
            U32(b, 36, 48000); U32(b, 40, 2); U32(b, 44, 16); U64(b, 48, (ulong)s_errors
#if !ARCH_ARM64
                + Field(ulong.MaxValue, 1)
#endif
            );
            for (int i = 0; i < s_count; i++)
            {
                var d = s_devices[i]!; int o = 64 + i * 96;
                U32(b, o, d.Id); U32(b, o + 4, (uint)d.Direction); U32(b, o + 8, (d.Muted ? 1u : 0u) | (s_default[d.Direction] == d.Id ? 2u : 0u));
                U32(b, o + 12, (uint)d.Volume); U32(b, o + 16, (uint)d.Peak); U32(b, o + 20, 48000); U32(b, o + 24, 2); Text(b, o + 32, d.Name);
            }
            int count = 0;
            foreach (var stream in s_mixer.Streams)
            {
                if (stream is null || (uid != 0 && uid != stream.Uid)) { continue; }
                int o = 1600 + count++ * 128;
                U64(b, o, (ulong)stream.Handle); U32(b, o + 8, (uint)stream.Pid); U32(b, o + 12, stream.Uid); U32(b, o + 16, (uint)stream.Direction);
                U32(b, o + 20, s_default[stream.Direction]); U32(b, o + 24, (uint)stream.Volume); U32(b, o + 28, stream.Muted ? 1u : 0u);
                U32(b, o + 32, (uint)stream.Peak); U32(b, o + 36, (uint)stream.Count); Text(b, o + 40, stream.Name);
            }
            U32(b, 8, (uint)count); U32(b, 12, Session(uid) ? 1u : 0u);
        }
        return b;
    }
    // ---------- The taskbar's sound center (trusted desktop code; the same session and revision rules as call 662).
    public readonly record struct OutputState(bool Available, uint Id, string Name, int Volume, bool Muted, int Peak);
    public readonly record struct StreamState(long Handle, string Name, int Volume, bool Muted, int Peak);
    /// <summary>The selected output, and the playback streams this user may see.</summary>
    public static (OutputState Output, System.Collections.Generic.List<StreamState> Streams) Overview(uint uid)
    {
        var streams = new System.Collections.Generic.List<StreamState>();
        lock (s_gate)
        {
            ObserveSession();
            var device = s_available ? Find(s_default[0]) : null;
            var output = device is null ? new OutputState(s_available, 0, s_available ? "No output selected" : Diagnostic, 0, false, 0)
                : new OutputState(true, device.Id, device.Name, device.Volume, device.Muted, device.Peak);
            foreach (var stream in s_mixer.Streams)
            {
                if (stream is null || stream.Direction != 0 || (uid != 0 && uid != stream.Uid)) { continue; }
                streams.Add(new StreamState(stream.Handle, stream.Name, stream.Volume, stream.Muted, stream.Peak));
            }
            return (output, streams);
        }
    }
    /// <summary>Sets the selected output's volume (0..100) or mute through <see cref="Control"/>; 0 or an error code.</summary>
    public static long SetOutput(uint uid, int volume, bool? muted)
    {
        uint id; ulong revision;
        lock (s_gate) { id = s_default[0]; revision = s_revision; }
        var request = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 1); BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), muted is null ? 3u : 4u);
        BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(8), id);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), muted is null ? (uint)Math.Clamp(volume, 0, 100) : muted.Value ? 1u : 0u);
        BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(24), revision);
        return Control(uid, request);
    }
    public static long Control(uint uid, ReadOnlySpan<byte> request)
    {
        if (request.Length != 32 || BinaryPrimitives.ReadUInt32LittleEndian(request) != 1 || BinaryPrimitives.ReadUInt32LittleEndian(request[20..]) != 0) { return -22; }
        uint action = BinaryPrimitives.ReadUInt32LittleEndian(request[4..]), value = BinaryPrimitives.ReadUInt32LittleEndian(request[16..]);
        ulong target = BinaryPrimitives.ReadUInt64LittleEndian(request[8..]), revision = BinaryPrimitives.ReadUInt64LittleEndian(request[24..]);
        lock (s_gate)
        {
            ObserveSession();
            if (!Session(uid)) { return -13; }
            if (revision != s_revision) { return -11; }
            if (!s_available || s_stopping) { return -19; }
            if (action is 5 or 6)
            {
                if (target > long.MaxValue) { return -9; }
                var stream = s_mixer.Find((long)target); if (stream is null) { return -9; }
                if (uid != 0 && uid != stream.Uid) { return -13; }
                if (action == 5) { if (value > 100) { return -22; } stream.Volume = (int)value; }
                else { if (value > 1) { return -22; } stream.Muted = value != 0;
                    if (stream.Direction == 1) { stream.Head = stream.Count = stream.Peak = 0; }
                }
            }
            else
            {
                if (target > uint.MaxValue) { return -22; }
                var device = Find((uint)target); if (device is null) { return -19; }
                if (action is 1 or 2)
                {
                    int direction = (int)action - 1;
                    if (device.Direction != direction || value != 0) { return -22; }
#if !ARCH_ARM64
                    if (Select((ulong)direction, device.Id) == 0) { s_default[direction] = 0; s_revision++; return -5; }
#endif
                    s_default[direction] = device.Id;
                    foreach (var current in s_mixer.Streams)
                    { if (current?.Direction == direction) { current.Head = current.Count = current.Peak = 0; } }
                }
                else if (action == 3) { if (value > 100) { return -22; } device.Volume = (int)value; }
                else if (action == 4) { if (value > 1) { return -22; } device.Muted = value != 0;
                    if (device.Direction == 1)
                    { foreach (var current in s_mixer.Streams) { if (current?.Direction == 1) { current.Head = current.Count = current.Peak = 0; } } }
                }
                else if (action == 7)
                {
                    if (device.Direction != 0 || device.Id != s_default[0] || value != 0) { return -22; }
                    s_testFrames = 48000 + 8192; s_tonePhase = 0; Monitor.Pulse(s_gate);
                }
                else { return -22; }
            }
            s_revision++;
            return 0;
        }
    }
#if !ARCH_ARM64
    private static void Pump()
    {
        bool entered = false;
        try
        {
            while (!s_stopping)
            {
                Monitor.Enter(s_gate); entered = true;
                {
                    ObserveSession();
                    // Session changes and revoked microphone grants purge their queues before DMA delivery.
                    foreach (var stream in s_mixer.Streams)
                    {
                        if (stream is not null && (!Session(stream.Uid) || (stream.Direction == 1 && !Recording(stream.Pid, stream.Uid))))
                        { s_mixer.Close(stream.Pid, stream.Handle); s_revision++; }
                    }
                    for (int direction = 0; direction < 2; direction++)
                    {
                        var device = Find(s_default[direction]); if (device is null) { continue; }
                        bool active = s_mixer.Active(direction) || (direction == 0 && s_testFrames > 0);
                        if (Run((ulong)direction, active ? 1UL : 0UL) == 0) { s_errors++; continue; }
                        for (int n = 0; active && n < 8; n++)
                        {
                            long period = Period((ulong)direction); if (period < 0) { break; }
                            fixed (byte* bytes = s_period)
                            {
                                if (direction == 1)
                                {
                                    if (TransferNative(1, (ulong)period, bytes) == 0) { continue; }
                                    device.Peak = s_mixer.Capture(s_period, device.Volume, device.Muted);
                                }
                                else
                                {
                                    device.Peak = s_mixer.Mix(s_period, device.Volume, device.Muted);
                                    if (s_testFrames > 0)
                                    {
                                        for (int i = 0; i < s_period.Length && s_testFrames > 0; i += 4, s_testFrames--)
                                        {
                                            // Quiet 480 Hz test, one second, both channels. Explicit user action only.
                                            int tone = device.Muted || s_testFrames <= 8192 ? 0 : (s_tonePhase++ % 100 < 50 ? 2400 : -2400) * device.Volume / 100;
                                            for (int ch = 0; ch < 2; ch++)
                                            {
                                                int o = i + ch * 2;
                                                int mixed = Math.Clamp((short)(s_period[o] | s_period[o + 1] << 8) + tone, short.MinValue, short.MaxValue);
                                                s_period[o] = (byte)mixed; s_period[o + 1] = (byte)(mixed >> 8);
                                            }
                                        }
                                    }
                                    TransferNative(0, (ulong)period, bytes);
                                }
                            }
                        }
                        if (!active) { device.Peak = 0; }
                    }
                    if (!s_stopping && !s_mixer.Active(0) && !s_mixer.Active(1) && s_testFrames == 0)
                    { Monitor.Wait(s_gate); }
                }
                entered = false; Monitor.Exit(s_gate);
                Thread.Sleep(4);
            }
        }
        catch (Exception error) { if (entered) { Monitor.Exit(s_gate); } s_errors++; Diagnostic = "Audio stopped: " + error.Message; s_available = false; Log.Write("audio", Diagnostic); }
        Run(0, 0); Run(1, 0);
    }
#endif
    public static void Stop()
    {
        lock (s_gate)
        {
            s_stopping = true; Monitor.PulseAll(s_gate);
#if !ARCH_ARM64
            if (s_available) { Run(0, 0); Run(1, 0); }
#endif
        }
    }
}
