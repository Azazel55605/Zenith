using System;
using System.Buffers.Binary;
using Zenith.Core.Configuration;
using Zenith.Core.Security;

namespace Zenith.Core.Processes;

internal static class ProcessScopedAppearance
{
    public const int SnapshotBytes = 512, RequestBytes = 48;
    public static byte[] Snapshot(AppearanceService system, PersonalAppearanceService personal, uint scope)
    {
        if (scope > 1) { throw new ArgumentException("unknown appearance scope"); }
        uint uid = CredentialContext.Current.Uid;
        if (scope == 1 && !personal.Available(uid)) { throw new UnauthorizedAccessException(); }
        var profile = scope == 1 ? personal.Snapshot(uid) : personal.ForDisplay(uid);
        var state = scope == 0 ? system.Snapshot() : new AppearanceState(profile.Saved, profile.Active, profile.System.Palette, profile.System.Resources);
        bool writable = scope == 0 ? CredentialContext.Current.IsRoot : personal.Available(uid);
        byte[] result = new byte[SnapshotBytes]; ProcessAppearance.Snapshot(state, scope == 0 ? system.Persistent : profile.Persistent, writable).CopyTo(result, 0);
        var b = result.AsSpan(448); var status = system.MaterialStatus;
        BinaryPrimitives.WriteUInt32LittleEndian(b, 3); BinaryPrimitives.WriteUInt32LittleEndian(b[4..], (writable ? 2U : 0U));
        BinaryPrimitives.WriteUInt64LittleEndian(b[8..], state.Saved.Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(b[16..], state.Saved.Materials); BinaryPrimitives.WriteUInt32LittleEndian(b[20..], status.Effective);
        BinaryPrimitives.WriteUInt32LittleEndian(b[24..], status.Available); BinaryPrimitives.WriteUInt32LittleEndian(b[28..], status.Reason);
        BinaryPrimitives.WriteUInt64LittleEndian(b[32..], state.Active.Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(b[40..], state.Saved.Translucency); BinaryPrimitives.WriteUInt32LittleEndian(b[44..], state.Active.Translucency);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(496), 1); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(500), scope);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(504), uid); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(508), profile.Inherited ? 1U : 0U); return result;
    }
    public static long Save(AppearanceService system, PersonalAppearanceService personal, ReadOnlySpan<byte> b)
    {
        if (b.Length != RequestBytes || BinaryPrimitives.ReadUInt32LittleEndian(b) != 1 || BinaryPrimitives.ReadUInt32LittleEndian(b[36..]) != 0
            || BinaryPrimitives.ReadUInt64LittleEndian(b[40..]) != 0) { return -22; }
        uint scope = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]), mode = BinaryPrimitives.ReadUInt32LittleEndian(b[16..]), accent = BinaryPrimitives.ReadUInt32LittleEndian(b[20..]);
        uint materials = BinaryPrimitives.ReadUInt32LittleEndian(b[24..]), level = BinaryPrimitives.ReadUInt32LittleEndian(b[28..]), inherit = BinaryPrimitives.ReadUInt32LittleEndian(b[32..]);
        ulong revision = BinaryPrimitives.ReadUInt64LittleEndian(b[8..]);
        if (scope > 1 || inherit > 1 || (scope == 0 && inherit != 0)) { return -22; }
        return scope == 0 ? system.SaveFull(mode, accent, materials, revision, level) : personal.Save(CredentialContext.Current.Uid, mode, accent, materials, level, revision, inherit != 0);
    }
}
