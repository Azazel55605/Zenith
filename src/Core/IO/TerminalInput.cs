using System;

namespace Zenith.Core.IO;

internal interface ITerminalInput : IByteInput, IDisposable
{
    int GetMode();
    int Size();
    int SetMode(int flags);
}
