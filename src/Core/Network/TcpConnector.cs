using System;
using System.IO;

namespace Zenith.Core.Network;

internal static class TcpConnector
{
    // Only retry the pinned transport's connect failure, never cancellation,
    // DNS, TLS, application writes or an arbitrary exception.
    public static T Connect<T>(Func<T> create, Action<T> connect, Action<T> close,
        Action cancel, Action pause, Action<string> report, string endpoint)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            cancel();
            T socket = create();
            bool retry = false;
            try { connect(socket); cancel(); return socket; }
            catch (Exception error)
            {
                close(socket);
                if (error.Message != "Failed to open TCP connection!") { throw; }
                if (attempt == 2)
                { throw new IOException("TCP connect failed: " + endpoint + " after 2 attempts (5s each)"); }
                retry = true;
            }
            if (retry)
            {
                cancel();
                report("TCP connect retry 2/2: " + endpoint);
                pause();
            }
        }
        throw new IOException("TCP connect attempts exhausted");
    }
}
