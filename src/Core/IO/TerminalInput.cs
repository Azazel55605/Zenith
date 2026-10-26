using System;

namespace Zenith.Core.IO;

internal interface ITerminalInput : IByteInput, IDisposable
{
    int GetMode();
    int SetMode(int flags);
}
