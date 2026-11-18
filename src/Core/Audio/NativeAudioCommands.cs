using System;
using System.Buffers.Binary;
using System.Text;
using System.Threading;
using Zenith.Core.Security;
using Zenith.Core.Shell;

namespace Zenith.Core.Audio;

/// <summary>Recovery/diagnostic controls use the same service as Meridian, without shell callbacks.</summary>
internal static class AudioCommands
{
    private static int s_probePid;
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));
    private static string Text(byte[] b, int o)
    {
        int n = 0; while (n < 64 && b[o + n] != 0) { n++; }
        return Encoding.ASCII.GetString(b, o, n);
    }
    public static void Register(Action<Command> add) => add(new Command("audio", "audio [test | capture-test | output ID | input ID | volume ID 0..100 | mute ID 0|1 | app-volume HANDLE 0..100 | app-mute HANDLE 0|1]", "Audio devices and application streams", Run));
    private static int Run(CommandContext c)
    {
        uint uid = CredentialContext.Current.Uid;
        var state = AudioService.Snapshot(uid);
        if (c.Args.Length == 0)
        {
            c.WriteLine("AUDIO " + AudioService.Diagnostic);
            c.WriteLine("PCM stereo 48000 Hz 16-bit; errors=" + U64(state, 48));
            for (int i = 0; i < U32(state, 4); i++)
            {
                int o = 64 + i * 96; uint flags = U32(state, o + 8);
                c.WriteLine("device=" + U32(state, o) + " " + (U32(state, o + 4) == 0 ? "output" : "input") + " " + Text(state, o + 32)
                    + " selected=" + ((flags & 2) != 0) + " volume=" + U32(state, o + 12) + " mute=" + ((flags & 1) != 0));
            }
            for (int i = 0; i < U32(state, 8); i++)
            {
                int o = 1600 + i * 128;
                c.WriteLine("stream=" + U64(state, o) + " pid=" + U32(state, o + 8) + " " + Text(state, o + 40)
                    + " " + (U32(state, o + 16) == 0 ? "playback" : "recording") + " volume=" + U32(state, o + 24) + " mute=" + U32(state, o + 28));
            }
            return 0;
        }
        if (c.Args.Length == 1 && c.Args[0] == "capture-test") { return Capture(c, uid); }
        uint action = c.Args[0] switch { "test" => 7, "output" => 1, "input" => 2, "volume" => 3, "mute" => 4, "app-volume" => 5, "app-mute" => 6, _ => 0 };
        int count = action == 7 ? 1 : action is 1 or 2 ? 2 : 3;
        if (action == 0 || c.Args.Length != count) { return c.Fail("see help audio"); }
        ulong target = U32(state, 24); uint value = 0;
        if (count > 1 && !ulong.TryParse(c.Args[1], out target)) { return c.Fail("invalid device or stream identity"); }
        if (count == 3 && !uint.TryParse(c.Args[2], out value)) { return c.Fail("invalid audio value"); }
        var request = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 1); BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), action);
        BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(8), target); BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), value);
        BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(24), U64(state, 16));
        long result = AudioService.Control(uid, request);
        if (result != 0) { return c.Fail("audio control failed: " + result); }
        c.WriteLine(action == 7 ? "AUDIO_TEST playing" : "AUDIO_CONTROL applied"); return 0;
    }
    private static int Capture(CommandContext c, uint uid)
    {
        int pid = Interlocked.Decrement(ref s_probePid);
        long handle = AudioService.Open(pid, uid, "Audio input test", 1, 0);
        if (handle < 0) { return c.Fail("capture refused: " + handle + "; use an authorized recording app or sudo"); }
        var data = new byte[4096]; int bytes = 0, peak = 0;
        long limit = System.Diagnostics.Stopwatch.GetTimestamp() + 10 * System.Diagnostics.Stopwatch.Frequency;
        try
        {
            while (bytes < 192000 && System.Diagnostics.Stopwatch.GetTimestamp() < limit)
            {
                c.Shell.ThrowIfCancelled();
                int n = AudioService.Pcm(pid, uid, handle, data, false);
                if (n < 0 && n != -11) { throw new InvalidOperationException("capture failed: " + n); }
                if (n > 0)
                {
                    bytes += n;
                    for (int i = 0; i < n; i += 2) { peak = Math.Max(peak, Math.Abs((int)(short)(data[i] | data[i + 1] << 8))); }
                }
                Thread.Sleep(4);
            }
            AudioService.Close(pid, handle);
        }
        catch { AudioService.Close(pid, handle); throw; }
        c.WriteLine("AUDIO_CAPTURE bytes=" + bytes + " peak=" + peak);
        return bytes >= 192000 ? 0 : c.Fail("capture DMA did not advance");
    }
}
