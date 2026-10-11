using System;

namespace Zenith.Core.Security;

/// <summary>Immutable identity. Environment variables never grant authority.</summary>
public sealed class Credentials
{
    public static readonly Credentials Root = new(0, 0);
    public static readonly Credentials Nobody = new(65534, 65534);
    private readonly uint[] _groups;

    public Credentials(uint uid, uint gid, params uint[] groups)
    {
        Uid = uid;
        Gid = gid;
        _groups = (uint[])groups.Clone();
    }

    public uint Uid { get; }
    public uint Gid { get; }
    public bool IsRoot => Uid == 0;
    public bool InGroup(uint gid)
    {
        if (Gid == gid) { return true; }
        foreach (uint group in _groups) { if (group == gid) { return true; } }
        return false;
    }
}

/// <summary>Explicit per-worker identity; scopes never flow implicitly to new threads.</summary>
public static class CredentialContext
{
    [ThreadStatic] private static Credentials? s_current;
    public static Credentials Current => s_current ?? Credentials.Nobody;

    public static Scope Enter(Credentials credentials) => new(credentials);

    public readonly struct Scope : IDisposable
    {
        private readonly Credentials? _previous;
        public Scope(Credentials credentials)
        {
            _previous = s_current;
            s_current = credentials ?? throw new ArgumentNullException(nameof(credentials));
        }
        public void Dispose() => s_current = _previous;
    }
}
