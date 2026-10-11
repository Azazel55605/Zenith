namespace Zenith.Core.Security;

/// <summary>Unix discretionary access policy, independent of VFS and UI.</summary>
public static class PermissionPolicy
{
    public const int Read = 4, Write = 2, Search = 1;

    public static bool Allows(Credentials caller, uint owner, uint group, ushort mode, int requested)
    {
        if (requested < 0 || (requested & ~7) != 0) { return false; }
        if (caller.IsRoot) { return true; }
        int shift = caller.Uid == owner ? 6 : caller.InGroup(group) ? 3 : 0;
        return (((mode >> shift) & 7) & requested) == requested;
    }

    public static bool CanRemove(Credentials caller, uint parentOwner, uint parentGroup,
        ushort parentMode, uint childOwner)
        => Allows(caller, parentOwner, parentGroup, parentMode, Write | Search)
            && ((parentMode & 0x200) == 0 || caller.IsRoot || caller.Uid == parentOwner || caller.Uid == childOwner);

    public static bool CanChmod(Credentials caller, uint owner) => caller.IsRoot || caller.Uid == owner;
    public static bool CanChown(Credentials caller) => caller.IsRoot;

    public static ushort CreationMode(ushort requested, ushort umask, ushort parentMode, bool directory)
    {
        // Never make new objects setuid; setgid directories inherit their group/mode.
        ushort mode = (ushort)((requested & 0x1ff) & ~(umask & 0x1ff));
        if (directory && (parentMode & 0x400) != 0) { mode |= 0x400; }
        return mode;
    }

    public static uint CreationGroup(Credentials caller, uint parentGroup, ushort parentMode)
        => (parentMode & 0x400) != 0 ? parentGroup : caller.Gid;
}
