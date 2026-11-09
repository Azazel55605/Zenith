using System;
using System.Buffers.Binary;
using System.Text;
using Zenith.Core.Configuration;
using Zenith.Core.Security;
namespace Zenith.Core.Processes;
internal static class ProcessDisplaySettings
{
    public const int SnapshotBytes = 512, RequestBytes = 40;
    public static byte[] Snapshot(DisplayService service, long now, int owner = 0)
    {
        DisplayState s = service.Snapshot(now); byte[] data = new byte[SnapshotBytes]; Span<byte> b = data;
        BinaryPrimitives.WriteUInt32LittleEndian(b,1);
        // Flag 8: the percent at 320 is current and requests may name 125/150/175 (scale field = percent).
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], (s.Persistent ? 1U : 0) | (s.Active && CredentialContext.Current.IsRoot ? 2U : 0) | (s.Phase!=0 && s.Owner==owner ? 4U : 0)
            | (service.Fractional ? 8U : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(b[320..], (uint)s.Current.Percent);
        BinaryPrimitives.WriteUInt64LittleEndian(b[8..],s.Revision);
        BinaryPrimitives.WriteUInt64LittleEndian(b[16..],s.Token);
        int[] values = {s.Current.Width,s.Current.Height,s.Current.Scale,s.Current.Percent > 0 ? s.Current.LogicalWidth : 0,s.Current.Percent > 0 ? s.Current.LogicalHeight : 0,s.Phase,s.Seconds,s.Error,s.Modes.Length};
        for(int i=0;i<values.Length;i++) { BinaryPrimitives.WriteInt32LittleEndian(b[(24+i*4)..],values[i]); }
        byte[] name = Encoding.ASCII.GetBytes(s.Driver); name.AsSpan(0,Math.Min(63,name.Length)).CopyTo(b[64..]);
        if(s.Modes.Length>24) { throw new InvalidOperationException("display catalogue overflow"); }
        for(int i=0;i<s.Modes.Length;i++)
        { BinaryPrimitives.WriteInt32LittleEndian(b[(128+i*8)..],s.Modes[i].Width); BinaryPrimitives.WriteInt32LittleEndian(b[(132+i*8)..],s.Modes[i].Height); }
        return data;
    }
    public static long Request(DisplayService service, ReadOnlySpan<byte> b, int uid, int owner)
    {
        if(b.Length!=RequestBytes || BinaryPrimitives.ReadUInt32LittleEndian(b)!=1 || BinaryPrimitives.ReadUInt64LittleEndian(b[32..])!=0) { return -22; }
        return service.Request(uid,owner,BinaryPrimitives.ReadUInt32LittleEndian(b[4..]),BinaryPrimitives.ReadUInt32LittleEndian(b[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[28..]),BinaryPrimitives.ReadUInt64LittleEndian(b[8..]),BinaryPrimitives.ReadUInt64LittleEndian(b[16..]));
    }
}
