using System;
using System.Buffers.Binary;

namespace Zenith.Core.Processes;

internal enum ProcessPathOperation : uint { Mkdir = 1, Unlink = 2, Rmdir = 3, Rename = 4, Chmod = 5, Chown = 6, Symlink = 7, CheckCopyDirectory = 8, CheckRemovalDirectory = 9, Touch = 10 }
internal readonly record struct ProcessPathRequest(ProcessPathOperation Operation, ulong Path, int Length,
    ulong Target, int TargetLength, ulong Value, ulong Extra)
{
    public const int Bytes = 56;
    public static bool TryRead(ReadOnlySpan<byte> bytes, out ProcessPathRequest request)
    {
        request = default;
        if (bytes.Length != Bytes || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 0) { return false; }
        uint op = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (op is < 1 or > 10) { return false; }
        ulong path = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]), length = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]), target = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]), targetLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]), value = BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]), extra = BinaryPrimitives.ReadUInt64LittleEndian(bytes[48..]);
        if (length is < 1 or > 1024 || path == 0) { return false; }
        bool second = op is 4 or 7 or 8;
        if (second ? target == 0 || targetLength is < 1 or > 1024 : target != 0 || targetLength != 0) { return false; }
        if (op is 1 or 5 ? value > 0xfff || extra != 0 : op == 6 ? value > uint.MaxValue || (extra > uint.MaxValue && extra != ulong.MaxValue) : value != 0 || extra != 0) { return false; }
        request = new((ProcessPathOperation)op, path, (int)length, target, (int)targetLength, value, extra); return true;
    }
}
internal interface IProcessNamespace
{
    int Mutate(ProcessPathOperation operation, string path, string? target, ulong value, ulong extra);
}
internal interface ISeekableProcessFile { long Seek(long offset, int origin); }
internal interface IProcessCopyFiles
{
    IProcessFile OpenCopyDestination(string path, IProcessFile source);
}

internal interface IProcessLinkMetadata { ProcessFileInfo LStat(string path); }
