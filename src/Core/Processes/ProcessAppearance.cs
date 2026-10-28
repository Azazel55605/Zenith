using System;
using System.Buffers.Binary;
using Zenith.Core.Configuration;
using Zenith.Core.Security;

namespace Zenith.Core.Processes;

internal static class ProcessAppearance
{
    public const int SnapshotBytes = 448, RequestBytes = 32;
    public static byte[] Snapshot(AppearanceService service)
    {
        var state = service.Snapshot(); byte[] bytes = new byte[SnapshotBytes]; var b = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
        uint flags = (service.Persistent ? 1U : 0U) | (CredentialContext.Current.IsRoot ? 2U : 0U)
            | (state.Saved.Revision != state.Active.Revision ? 4U : 0U) | (state.Palette.Packaged ? 8U : 0U);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], flags);
        BinaryPrimitives.WriteUInt64LittleEndian(b[8..], state.Saved.Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(b[16..], state.Saved.Mode);
        BinaryPrimitives.WriteUInt32LittleEndian(b[20..], state.Saved.Accent);
        BinaryPrimitives.WriteUInt32LittleEndian(b[24..], state.Active.Mode);
        BinaryPrimitives.WriteUInt32LittleEndian(b[28..], state.Active.Accent);
        BinaryPrimitives.WriteUInt64LittleEndian(b[32..], state.Active.Revision);
        BinaryPrimitives.WriteUInt64LittleEndian(b[40..], state.Resources);
        for (uint mode = 0; mode < 2; mode++) { for (uint accent = 0; accent < 6; accent++) { for (int role = 0; role < 8; role++)
        { BinaryPrimitives.WriteUInt32LittleEndian(b[(48 + ((int)mode * 6 + (int)accent) * 32 + role * 4)..], state.Palette.Get(mode, accent, role)); } } }
        return bytes;
    }
    public static long Save(AppearanceService service, ReadOnlySpan<byte> b)
    {
        if (b.Length != RequestBytes || BinaryPrimitives.ReadUInt32LittleEndian(b) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(b[4..]) != 0 || BinaryPrimitives.ReadUInt64LittleEndian(b[24..]) != 0) { return -22; }
        return service.Save(BinaryPrimitives.ReadUInt32LittleEndian(b[16..]), BinaryPrimitives.ReadUInt32LittleEndian(b[20..]),
            BinaryPrimitives.ReadUInt64LittleEndian(b[8..]));
    }
    public static byte[] MaterialSnapshot(AppearanceService service)
    {
        var state = service.Snapshot(); var status = service.MaterialStatus; byte[] bytes = new byte[48];var b = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], (service.Persistent ? 1U : 0U) | (CredentialContext.Current.IsRoot ? 2U : 0U));
        BinaryPrimitives.WriteUInt64LittleEndian(b[8..], state.Saved.Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(b[16..], state.Saved.Materials);
        BinaryPrimitives.WriteUInt32LittleEndian(b[20..], status.Effective);
        BinaryPrimitives.WriteUInt32LittleEndian(b[24..], status.Available);
        BinaryPrimitives.WriteUInt32LittleEndian(b[28..], status.Reason);
        BinaryPrimitives.WriteUInt64LittleEndian(b[32..], state.Active.Revision);return bytes;
    }
    public static long SaveMaterials(AppearanceService service, ReadOnlySpan<byte> b)
    {
        if (b.Length != 32 || BinaryPrimitives.ReadUInt32LittleEndian(b) != 1 || BinaryPrimitives.ReadUInt32LittleEndian(b[4..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(b[28..]) != 0) { return -22; }
        return service.SaveFull(BinaryPrimitives.ReadUInt32LittleEndian(b[16..]), BinaryPrimitives.ReadUInt32LittleEndian(b[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[24..]), BinaryPrimitives.ReadUInt64LittleEndian(b[8..]));
    }
}
