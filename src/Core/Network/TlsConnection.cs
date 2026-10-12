using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Zenith.Core.Time;

namespace Zenith.Core.Network;

/// <summary>Serialized fixed-memory TLS bridge; C never calls back into managed code.</summary>
internal sealed unsafe class TlsConnection : IDisposable
{
    public const string TrustPath = "/etc/zenith/tls-ca.pem";
    private const int WantRead = -0x6900, WantWrite = -0x6880, PeerClosed = -0x7880;
    private readonly Socket _socket;
    private readonly Action _cancel, _ready;
    private readonly byte[] _wire = new byte[8192];
    private bool _active;
    public TlsConnection(Socket socket, string host, string ca, Action cancel, Action ready)
    {
        _socket = socket; _cancel = cancel; _ready = ready;
        Open(host, ca); _active = true;
        try
        {
            var timer = Stopwatch.StartNew();
            while (true)
            {
                Check(timer, 15000); int result = Handshake(); Drain();
                if (result == 0) { if (Verify() != 0) { throw new IOException("TLS certificate verification failed"); } break; }
                Retry(result); Pump();
            }
        }
        catch { Dispose(); throw; }
    }
    public static void SelfTest()
    {
        if (NativeSelfTest() != 0) { throw new IOException("TLS primitive known-answer tests failed"); }
    }
    [DllImport("*", EntryPoint = "zenith_tls_selftest")]
    private static extern int NativeSelfTest();
    public static void ValidateTrust(string ca)
    {
        Open("trust.invalid", ca); Close();
    }
    private static void Open(string host, string ca)
    {
        byte[] roots = Encoding.ASCII.GetBytes(ca + "\0"), name = Encoding.ASCII.GetBytes(host + "\0");
        DateTime now = SystemClock.UtcNow;
        fixed (byte* root = roots, hostname = name)
        {
            int result = NativeOpen(root, (nuint)roots.Length, hostname, now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);
            if (result != 0) { throw new IOException("TLS initialization rejected trust/clock/entropy (code=" + result + ")"); }
        }
    }
    public void Write(byte[] bytes)
    {
        int offset = 0; var timer = Stopwatch.StartNew();
        while (offset < bytes.Length)
        {
            Check(timer, 15000); int result;
            fixed (byte* p = bytes) { result = NativeWrite(p + offset, (nuint)(bytes.Length - offset)); }
            Drain(); if (result > 0) { offset += result; continue; }
            Retry(result); Pump();
        }
    }
    public int Read(byte[] bytes, int maximum = 3000)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            Check(timer, maximum); int result;
            fixed (byte* p = bytes) { result = NativeRead(p, (nuint)bytes.Length); }
            Drain(); if (result > 0) { return result; }
            if (result == 0 || result == PeerClosed) { return 0; }
            Retry(result); Pump();
        }
    }
    private void Check(Stopwatch timer, int maximum)
    { _cancel(); _ready(); if (timer.ElapsedMilliseconds > maximum) { throw new IOException("TLS operation timed out"); } }
    private static void Retry(int code)
    { if (code != WantRead && code != WantWrite) { throw new IOException("TLS handshake/record rejected (code=" + code + ")"); } }
    private void Drain()
    {
        while (true)
        {
            _cancel(); _ready(); int count;
            fixed (byte* p = _wire) { count = NativePull(p, (nuint)_wire.Length); }
            if (count == 0) { return; }
            if (count < 0 || _socket.Send(_wire, 0, count, SocketFlags.None) != count) { throw new IOException("TLS transport write failed"); }
        }
    }
    private void Pump()
    {
        _cancel(); _ready(); int available = _socket.Available;
        if (available > 0)
        {
            int count = _socket.Receive(_wire, 0, Math.Min(available, _wire.Length), SocketFlags.None);
            fixed (byte* p = _wire)
            { if (count <= 0 || NativePush(p, (nuint)count) != count) { throw new IOException("TLS receive queue exceeded or closed"); } }
        }
        else if (!_socket.Connected) { throw new IOException("TLS transport closed unexpectedly"); }
        else { Thread.Sleep(10); }
    }
    public void Dispose() { if (_active) { Close(); _active = false; } }
    [DllImport("*", EntryPoint = "zenith_tls_open")]
    private static extern int NativeOpen(byte* ca, nuint length, byte* hostname, int year, int month, int day, int hour, int minute, int second);
    [DllImport("*", EntryPoint = "zenith_tls_close")]
    private static extern void Close();
    [DllImport("*", EntryPoint = "zenith_tls_handshake")]
    private static extern int Handshake();
    [DllImport("*", EntryPoint = "zenith_tls_verify")]
    private static extern uint Verify();
    [DllImport("*", EntryPoint = "zenith_tls_read")]
    private static extern int NativeRead(byte* data, nuint length);
    [DllImport("*", EntryPoint = "zenith_tls_write")]
    private static extern int NativeWrite(byte* data, nuint length);
    [DllImport("*", EntryPoint = "zenith_tls_push")]
    private static extern int NativePush(byte* data, nuint length);
    [DllImport("*", EntryPoint = "zenith_tls_pull")]
    private static extern int NativePull(byte* data, nuint length);
}
