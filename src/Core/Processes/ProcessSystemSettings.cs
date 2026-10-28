using System;
using System.Buffers.Binary;
using System.Text;
using Zenith.Core.Configuration;
using Zenith.Core.Security;
using Zenith.Core.Time;
using Zenith.Core.Sessions;

namespace Zenith.Core.Processes;

// A bounded catalogue of actual shared-service choices, not paths or shell text.
internal static class ProcessSystemSettings
{
    public const int SnapshotBytes = 4096, RequestBytes = 32;
    public static byte[] Snapshot(SettingsService service, bool sessions = false)
    {
        if (KeyboardLayoutCatalog.Names.Length > 8 || TimeZones.All.Count > 48) { throw new InvalidOperationException("settings catalogue exceeds ABI bounds"); }
        var (state, revision) = service.Snapshot(); byte[] data = new byte[SnapshotBytes]; var b = data.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(b, sessions ? 2U : 1U);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], (service.Persistent ? 1U : 0U) | (CredentialContext.Current.IsRoot ? 2U : 0U));
        BinaryPrimitives.WriteUInt64LittleEndian(b[8..], revision);
        BinaryPrimitives.WriteUInt32LittleEndian(b[16..], (uint)KeyboardLayoutCatalog.Names.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b[20..], (uint)TimeZones.All.Count);
        uint keyboard = uint.MaxValue, zone = uint.MaxValue;
        for (int i = 0; i < KeyboardLayoutCatalog.Names.Length; i++)
        { Put(data, 128 + i * 32, 32, KeyboardLayoutCatalog.Names[i]); if (KeyboardLayoutCatalog.Names[i] == state.Keyboard) { keyboard = (uint)i; } }
        for (int i = 0; i < TimeZones.All.Count; i++)
        { Put(data, 384 + i * 64, 64, TimeZones.All[i].Name); if (TimeZones.All[i].Name == state.TimeZone) { zone = (uint)i; } }
        BinaryPrimitives.WriteUInt32LittleEndian(b[24..], keyboard);
        BinaryPrimitives.WriteUInt32LittleEndian(b[28..], zone);
        Put(data, 32, 64, state.TimeZone);
        if (sessions)
        {
            var choices = service.SessionChoices; if(choices.Count>3) { throw new InvalidOperationException("session catalogue overflow"); }
            BinaryPrimitives.WriteUInt32LittleEndian(b[3456..], (uint)choices.Count); uint selected=uint.MaxValue;
            for(int i=0;i<choices.Count;i++) { Put(data,3464+i*16,16,SessionModes.Name(choices[i])); if(choices[i]==state.DefaultSession) { selected=(uint)i; } }
            BinaryPrimitives.WriteUInt32LittleEndian(b[3460..],selected); Put(data,96,16,SessionModes.Name(state.DefaultSession));
        }
        return data;
    }
    private static void Put(byte[] data, int offset, int capacity, string value)
    {
        byte[] text = Encoding.UTF8.GetBytes(value);
        if (text.Length >= capacity) { throw new InvalidOperationException("settings name exceeds ABI bounds"); }
        text.CopyTo(data, offset);
    }
    public static long Save(SettingsService service, ReadOnlySpan<byte> b, bool sessions = false)
    {
        if (b.Length != RequestBytes || BinaryPrimitives.ReadUInt32LittleEndian(b) != (sessions ? 2U : 1U) || BinaryPrimitives.ReadUInt32LittleEndian(b[20..]) != 0
            || BinaryPrimitives.ReadUInt64LittleEndian(b[24..]) != 0) { return -22; }
        if(!sessions && BinaryPrimitives.ReadUInt32LittleEndian(b[4..])>2) { return -22; }
        return service.SaveChoice(BinaryPrimitives.ReadUInt32LittleEndian(b[4..]), BinaryPrimitives.ReadUInt32LittleEndian(b[16..]), BinaryPrimitives.ReadUInt64LittleEndian(b[8..]));
    }
}
