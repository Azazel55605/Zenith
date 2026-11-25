using System;
using System.Buffers.Binary;
using System.Text;

namespace Zenith.Gui.Shell;

/// <summary>The launcher IDs and labels offered in Meridian's pinned-app selector.</summary>
internal static class DesktopAppCatalog
{
    private const int NameBytes = 65;

    public static byte[] Encode()
    {
        var apps = AppRegistry.Visible();
        apps.RemoveAll(app => !ValidId(app.Name));
        var payload = new byte[8 + apps.Count * NameBytes * 2];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)apps.Count);
        int offset = 8;
        foreach (AppInfo app in apps)
        {
            Encoding.ASCII.GetBytes(app.Name, payload.AsSpan(offset, NameBytes));
            Encoding.ASCII.GetBytes(app.DisplayName, payload.AsSpan(offset + NameBytes, NameBytes));
            offset += NameBytes * 2;
        }
        return payload;
    }

    private static bool ValidId(string name)
    {
        foreach (char c in name) { if (c is < ' ' or > '~' or ',' or '=' or '"') { return false; } }
        return name.Length is > 0 and < NameBytes;
    }
}
