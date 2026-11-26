using System;
using System.Buffers.Binary;

namespace Zenith.Core.Processes;

internal enum ProcessNetworkOperation : uint { Status = 1, Resolve = 2, Echo = 3, Connect = 4, Download = 5, Error = 6, Commit = 7, ConnectBulk = 8, Entropy = 9, ReadCredential = 10, ConnectWeb = 11 }
internal readonly record struct ProcessNetworkRequest(ProcessNetworkOperation Operation, uint Flags,
    ulong Input, int InputLength, ulong Target, int TargetLength, ulong Output, int Capacity, ulong Value, ulong Extra)
{
    public const int Bytes = 80;
    public static bool TryRead(ReadOnlySpan<byte> bytes, out ProcessNetworkRequest request)
    {
        request = default;
        if (bytes.Length != Bytes) { return false; }
        uint op = BinaryPrimitives.ReadUInt32LittleEndian(bytes), flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        ulong input = Read(bytes, 8), length = Read(bytes, 16), target = Read(bytes, 24), targetLength = Read(bytes, 32), output = Read(bytes, 40), capacity = Read(bytes, 48), value = Read(bytes, 56), extra = Read(bytes, 64);
        if (op is < 1 or > 11 || Read(bytes, 72) != 0) { return false; }
        bool first = op is 2 or 3 or 4 or 5 or 8 or 10 or 11;
        bool second = op == 2 && targetLength != 0;
        bool result = op is 1 or 2 or 6 or 9 or 10;
        if (first ? input == 0 || length is < 1 or > 1024 : input != 0 || length != 0) { return false; }
        if (second ? target == 0 || targetLength > 253 : target != 0 || targetLength != 0) { return false; }
        if (result ? output == 0 || capacity is < 1 or > 4096 : output != 0 || capacity != 0) { return false; }
        if (op is 2 or 3 or 4 or 8 or 11 && length > 253) { return false; }
        if (op == 2 && capacity < 16 || op == 9 && capacity > 64) { return false; }
        if (flags > (op == 5 ? 3u : op is 4 or 11 ? 1u : 0u)) { return false; }
        if (op == 7 ? extra > ProcessNetwork.MaximumTransfer : extra != 0) { return false; }
        bool validValue = op switch
        {
            2 or 4 or 8 or 11 => value is >= 1 and <= 65535,
            5 => value <= ProcessNetwork.MaximumTransfer,
            7 => value is >= 10000 and <= int.MaxValue,
            _ => value == 0
        };
        if (!validValue) { return false; }
        request = new((ProcessNetworkOperation)op, flags, input, (int)length, target, (int)targetLength, output, (int)capacity, value, extra); return true;
    }
    private static ulong Read(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
}
