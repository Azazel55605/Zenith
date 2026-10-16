using System;
using System.Collections.Generic;
using System.IO;
using Zenith.Core.Network;

namespace Zenith.Tests;

public sealed class TcpConnectorTests
{
    private sealed class Socket { public bool Closed; }

    [Fact]
    public void LostHandshakeClosesFirstSocketBeforeCreatingFreshConnection()
    {
        var sockets = new List<Socket>(); int pauses = 0;
        Socket result = TcpConnector.Connect(() =>
        {
            if (sockets.Count > 0) { Assert.True(sockets[^1].Closed); }
            var socket = new Socket(); sockets.Add(socket); return socket;
        }, socket => { if (sockets.Count == 1) { throw new Exception("Failed to open TCP connection!"); } },
            socket => socket.Closed = true, () => { }, () => pauses++, _ => { }, "server:443 (10.0.10.3)");
        Assert.Equal(2, sockets.Count); Assert.Equal(1, pauses);
        Assert.Same(sockets[1], result); Assert.False(result.Closed);
    }

    [Fact]
    public void PersistentFailureIsBoundedAndNamesEndpoint()
    {
        int created = 0, closed = 0;
        var error = Assert.Throws<IOException>(() => TcpConnector.Connect(() => ++created,
            _ => throw new Exception("Failed to open TCP connection!"), _ => closed++,
            () => { }, () => { }, _ => { }, "server:443 (10.0.10.3)"));
        Assert.Equal(2, created); Assert.Equal(2, closed);
        Assert.Contains("server:443 (10.0.10.3)", error.Message);
    }

    [Fact]
    public void CancellationAfterConnectClosesSocketWithoutRetry()
    {
        int created = 0, closed = 0; bool connected = false;
        Assert.Throws<OperationCanceledException>(() => TcpConnector.Connect(() => ++created,
            _ => connected = true, _ => closed++,
            () => { if (connected) { throw new OperationCanceledException(); } },
            () => throw new Exception("unexpected retry"), _ => { }, "server:443"));
        Assert.Equal(1, created); Assert.Equal(1, closed);
    }

    [Fact]
    public void OtherExceptionsPreserveIdentityAndAreNotRetried()
    {
        int created = 0, closed = 0; var original = new InvalidOperationException("no IPv4 configuration");
        var error = Assert.Throws<InvalidOperationException>(() => TcpConnector.Connect(() => ++created,
            _ => throw original, _ => closed++, () => { }, () => { }, _ => { }, "server:443"));
        Assert.Same(original, error); Assert.Equal(1, created); Assert.Equal(1, closed);
    }
}
