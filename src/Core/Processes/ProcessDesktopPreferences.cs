using System;
using System.Buffers.Binary;
using System.Text;
using Zenith.Core.Configuration;

namespace Zenith.Core.Processes;

internal static class ProcessDesktopPreferences
{
    public const int Bytes = 640;
    private const int EntryOffset = 64, EntryBytes = 72, NameBytes = 65;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private static uint Read(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
    private static void Write(Span<byte> b, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b[at..], value);

    public static byte[] Snapshot(SettingsService service, uint page, bool writable)
    {
        var (prefs, taskbar, revision) = service.DesktopSnapshot();
        var data = new byte[Bytes];
        Write(data, 0, 1);
        Write(data, 4, page);
        Write(data, 8, writable ? 1u : 0u);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), revision);
        uint[] choices = prefs.Choices(page, taskbar);
        for (int i = 0; i < choices.Length; i++) { Write(data, 24 + i * 4, choices[i]); }
        DesktopEntry[] entries = prefs.Entries(page);
        Write(data, 12, (uint)entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            int at = EntryOffset + i * EntryBytes;
            Write(data, at, entries[i].Id);
            Utf8.GetBytes(entries[i].Name.AsSpan(), data.AsSpan(at + 4, NameBytes));
        }
        return data;
    }

    public static int Save(SettingsService service, ReadOnlySpan<byte> data)
    {
        if (data.Length != Bytes || Read(data, 0) != 1 || Read(data, 8) != 0 || Read(data, 56) != 0 || Read(data, 60) != 0) { return -22; }
        uint page = Read(data, 4), count = Read(data, 12);
        int fields = page switch { 1 => 2, 2 => 8, 3 => 4, 4 => 1, _ => 0 };
        if (fields == 0 || count > DesktopPreferences.MaximumDesktops) { return -22; }
        var values = new uint[fields];
        for (int i = 0; i < 8; i++)
        {
            uint value = Read(data, 24 + i * 4);
            if (i < fields) { values[i] = value; }
            else if (value != 0) { return -22; }
        }
        var entries = new DesktopEntry[count];
        for (int i = 0; i < entries.Length; i++)
        {
            int at = EntryOffset + i * EntryBytes;
            ReadOnlySpan<byte> name = data.Slice(at + 4, NameBytes);
            int end = name.IndexOf((byte)0);
            if (end < 1 || data.Slice(at + 4 + end, EntryBytes - 4 - end).IndexOfAnyExcept((byte)0) >= 0) { return -22; }
            try { entries[i] = new(Read(data, at), Utf8.GetString(name[..end])); }
            catch (DecoderFallbackException) { return -22; }
        }
        if (data[(EntryOffset + entries.Length * EntryBytes)..].IndexOfAnyExcept((byte)0) >= 0) { return -22; }
        return service.SaveDesktopPreferences(page, values, entries, BinaryPrimitives.ReadUInt64LittleEndian(data[16..]));
    }
}
