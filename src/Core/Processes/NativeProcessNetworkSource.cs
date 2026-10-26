using System;
using System.IO;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Vfs;
using Zenith.Core.Network;
using Zenith.Core.Security;
using Zenith.Core.Storage;

namespace Zenith.Core.Processes;

internal sealed class NativeProcessNetworkSource : IProcessNetworkSource
{
    private readonly Action _cancel;
    public NativeProcessNetworkSource(Action cancel) => _cancel = cancel;
    public string Status() => NetworkService.Status();
    public string Resolve(string host, string? server, int port) => NetworkService.Resolve(host, _cancel, server, port);
    public long Echo(string host) => NetworkService.Ping(host, _cancel);
    public IProcessConnection Connect(string host, int port, bool secure) => NetworkService.OpenClient(host, port, secure, _cancel);
    public IProcessDownload Download(string path, bool replace, int maximum) => new DownloadSink(path, replace, maximum, _cancel);

    private sealed class DownloadSink : IProcessDownload
    {
        private readonly string _path, _temporary;
        private readonly bool _replace;
        private readonly int _maximum;
        private readonly Action _cancel;
        private readonly Credentials _identity = CredentialContext.Current;
        private IVfsFileHandle? _file;
        private int _written;
        private bool _committed, _closed;
        public DownloadSink(string path, bool replace, int maximum, Action cancel)
        {
            _path = path; _replace = replace; _maximum = maximum; _cancel = cancel;
            CheckDestination(); byte[] random = new byte[8];
            if (!HardwareEntropy.Source.TryFill(random)) { throw new IOException("download staging requires hardware entropy"); }
            _temporary = path + ".native-fetch-" + Convert.ToHexString(random);
            if (File.Exists(_temporary) || Directory.Exists(_temporary)) { throw new IOException("download staging collision"); }
            try
            {
                if (!VfsManager.TryCreateFile(_temporary, (VfsMode)0x180) || !VfsManager.TryOpenFile(_temporary, out var file))
                { throw new IOException("cannot stage native download beside destination"); }
                _file = file;
            }
            catch { Dispose(); throw; }
        }
        private void CheckDestination()
        {
            if (new VfsFileMetadata().TryRead(_path, out var existing) && (!_replace || existing.Type != "regular file"))
            { throw new IOException("destination exists; --replace permits regular files only"); }
        }
        public int Write(byte[] buffer)
        {
            _cancel(); if (_closed || _file is null) { throw new IOException("download staging is closed"); }
            if (buffer.Length > _maximum - _written) { throw new IOException("download exceeds declared bound"); }
            long count = _file.Write(buffer);
            if (count != buffer.Length) { throw new IOException("short download staging write"); }
            _written += (int)count; return (int)count;
        }
        public void Commit(int expected)
        {
            _cancel(); if (_closed || _file is null || expected != _written) { throw new IOException("download length mismatch or closed staging"); }
            if (!_file.TryFlush()) { throw new IOException("native download flush failed"); }
            _file.Dispose(); _file = null; _cancel(); CheckDestination();
            if (!VfsManager.TryRename(_temporary, _path)) { throw new IOException("native download commit failed"); }
            _committed = true; SystemMounts.SyncAll();
        }
        public void Dispose()
        {
            if (_closed) { return; } _closed = true;
            var caller = CredentialContext.Enter(_identity);
            try
            {
                try { _file?.Dispose(); } catch { } _file = null;
                if (!_committed) { try { if (File.Exists(_temporary)) { File.Delete(_temporary); } } catch { } }
                caller.Dispose();
            }
            catch { caller.Dispose(); throw; }
        }
    }
}
