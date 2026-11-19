using System;
using System.Buffers.Binary;
using System.Threading;

namespace Zenith.Core.Audio;

/// <summary>System sounds (docs/design/sounds-concept.svg, kernel 0.0.71): plays a named event of the chosen pack
/// (style field 16: Zenith Chimes or Silent) through the session's mixer, under the apps, on its own thread. The pack
/// is the zenith-sounds package: mono 24 kHz WAV files, played at 48 kHz stereo. A missing pack or device stays silent.</summary>
internal static class SystemSounds
{
    private const int Pid = int.MaxValue - 7;   // the system's own stream, never a program's pid
    private const string Directory = "/usr/share/zenith/sounds/chimes/";
    private static int s_playing;

    public static bool Enabled => (Zenith.Core.Configuration.SystemConfiguration.Settings?.Style(16) ?? 0) == 0;

    /// <summary>Plays <paramref name="name"/> (login, logout, notification, attention, error, device-added, device-removed,
    /// done, volume, trash, screenshot, shutdown) unless sounds are off or one is already playing.</summary>
    public static void Play(string name)
    {
        if (!Enabled || Interlocked.CompareExchange(ref s_playing, 1, 0) != 0) { return; }
        byte[]? pcm = null;
        // The pack is a system resource: read it as the system, whoever is signed in.
        var root = Zenith.Core.Security.CredentialContext.Enter(Zenith.Core.Security.Credentials.Root);
        try { pcm = Load(Directory + name + ".wav"); } catch (Exception) { }
        root.Dispose();
        if (pcm is null) { Interlocked.Exchange(ref s_playing, 0); return; }
        uint uid = Zenith.Core.Security.Accounts.Session.Uid;
        new Thread(() =>
        {
            long handle = 0;
            try
            {
                handle = AudioService.Open(Pid, uid, "System sounds", 0, 0);
                if (handle > 0)
                {
                    int at = 0, waits = 0;
                    while (at < pcm.Length && waits < 400)
                    {
                        int n = AudioService.Pcm(Pid, uid, handle, pcm.AsSpan(at, Math.Min(8192, pcm.Length - at)), true);
                        if (n > 0) { at += n; waits = 0; }
                        else if (n == -11) { Thread.Sleep(10); waits++; }
                        else { break; }
                    }
                    Thread.Sleep(250);   // let the queued tail play before the stream goes
                    Log.Write("audio", "system sound " + name);
                }
            }
            catch (Exception) { }
            if (handle > 0) { try { AudioService.Close(Pid, handle); } catch (Exception) { } }
            Interlocked.Exchange(ref s_playing, 0);
        }).Start();
    }

    /// <summary>A mono 24 kHz 16-bit WAV as 48 kHz stereo S16LE (each sample twice, both channels), or null.</summary>
    public static byte[]? Load(string path)
    {
        if (!System.IO.File.Exists(path)) { return null; }
        byte[] wav = System.IO.File.ReadAllBytes(path);
        return Convert(wav);
    }
    public static byte[]? Convert(byte[] wav)
    {
        if (wav.Length < 44 || wav.Length > 262144 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F'
            || BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)) != 1 || BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)) != 24000
            || BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)) != 16 || wav[36] != 'd' || wav[37] != 'a') { return null; }
        int bytes = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40)), (uint)(wav.Length - 44)) & ~1;
        var output = new byte[bytes * 4];
        for (int i = 0, o = 0; i < bytes; i += 2)
        {
            byte lo = wav[44 + i], hi = wav[45 + i];
            for (int k = 0; k < 4; k++) { output[o++] = lo; output[o++] = hi; }
        }
        return output;
    }
}
