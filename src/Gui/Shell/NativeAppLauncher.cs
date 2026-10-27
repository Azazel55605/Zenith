using System;
using System.Collections.Generic;
using System.Threading;
using Zenith.Core.IO;
using Zenith.Core.Processes;
using Zenith.Core.Security;

namespace Zenith.Gui.Shell;

/// <summary>Launch work never blocks the compositor. Each invocation captures
/// its caller, owns cancellation and dies with this desktop session.</summary>
internal sealed class NativeAppLauncher
{
    private sealed class Invocation
    {
        public readonly ProcessCancellation Stop = new(() => { });
        public bool Done;
        public string? Error;
    }
    private sealed class EmptyInput : IByteInput
    { public int Read(byte[] buffer) => 0; public void Close() { } }
    private readonly object _gate = new();
    private readonly List<Invocation> _running = new();
    private bool _closed;
    public bool Launch(string program, string[] arguments, string cwd)
    {
        Credentials identity = CredentialContext.Current;
        var invocation = new Invocation();
        lock (_gate)
        { if (_closed || _running.Count >= 4) { return false; } _running.Add(invocation); }
        new Thread(() =>
        {
            using var caller = CredentialContext.Enter(identity);
            var streams = new ProcessStreams(new EmptyInput(), (_, bytes) => bytes.Length, false, false, false);
            string? failure = null;
            try
            {
                var result = NativeProcess.Run(program, arguments, () => invocation.Stop.Requested,
                    invocation, cwd, streams: streams, requestStop: invocation.Stop.Request);
                if (result.Status != 0 && result.Status != 130)
                { failure = result.Error.Length > 0 ? result.Error : "Application exited with status " + result.Status; }
            }
            catch { failure = "Application could not be started."; }
            streams.Close(); invocation.Stop.Close();
            lock (_gate) { invocation.Error = failure; invocation.Done = true; }
        }).Start();
        return true;
    }
    public void Tick(WindowManager windows)
    {
        lock (_gate)
        {
            for (int i = _running.Count - 1; i >= 0; i--)
            {
                if (!_running[i].Done) { continue; }
                if (!_closed && _running[i].Error is string error) { windows.Open(new AppLaunchNotice(error)); }
                _running.RemoveAt(i);
            }
        }
    }
    public void Stop()
    { lock (_gate) { _closed = true; foreach (var invocation in _running) { invocation.Stop.Request(); } } }
}
