using System;

namespace Zenith.Core.Processes;

internal static class NativeProcessGraphics
{
    private static readonly object Gate = new();
    private static Func<uint, IProcessGraphicsHost?>? _factory;
    public static void Register(Func<uint, IProcessGraphicsHost?>? factory)
    { lock (Gate) { _factory = factory; } }
    public static ProcessGraphics Create(uint uid)
    {
        lock (Gate) { return new ProcessGraphics(_factory?.Invoke(uid)); }
    }
}
