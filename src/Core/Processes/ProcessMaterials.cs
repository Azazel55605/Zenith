using System;
using System.Buffers.Binary;

namespace Zenith.Core.Processes;

internal readonly record struct ProcessMaterialFrame(ulong Pixels, ulong Length, ulong Regions, int Count)
{
    public const int Bytes = 48;
    public static bool TryRead(ReadOnlySpan<byte> b, out ProcessMaterialFrame r)
    {
        r = default;
        if (b.Length != Bytes || BinaryPrimitives.ReadUInt32LittleEndian(b) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(b[4..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(b[36..]) != 0
            || BinaryPrimitives.ReadUInt64LittleEndian(b[40..]) != 0) { return false; }
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(b[32..]);
        if (count > ProcessMaterialRegion.MaximumRegions) { return false; }
        r = new(BinaryPrimitives.ReadUInt64LittleEndian(b[8..]), BinaryPrimitives.ReadUInt64LittleEndian(b[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(b[24..]), (int)count);
        return count != 0 || r.Regions == 0;
    }
    public static byte[] Capabilities()
    {
        byte[] b = new byte[32];
        uint[] values = { 2, 15, ProcessMaterialRegion.MaximumRegions, ProcessMaterialRegion.MaximumRadius,
            ProcessMaterialRegion.MaximumArea, 4, 640, 480 };
        for (int i = 0; i < values.Length; i++) { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), values[i]); }
        return b;
    }
}

// These are material intentions, not colors, opacity or arbitrary shader programs.
// Host policy chooses the actual tint/blur budget. No backdrop is returned.
internal readonly record struct ProcessMaterialRegion(int X, int Y, int Width, int Height, int Radius, int Role, int Effect, int Opacity = 0)
{
    public const int Bytes = 32, MaximumRegions = 8, MaximumRadius = 24, MaximumArea = 131072;
    public bool Contains(int x, int y) => x >= X && y >= Y && x - X < Width && y - Y < Height;
    public static bool TryRead(ReadOnlySpan<byte> bytes, int count, out ProcessMaterialRegion[] regions)
    {
        regions = Array.Empty<ProcessMaterialRegion>();
        if (count < 0 || count > MaximumRegions || bytes.Length != count * Bytes) { return false; }
        var result = new ProcessMaterialRegion[count];
        for (int i = 0; i < count; i++)
        {
            var b = bytes.Slice(i * Bytes, Bytes);
            if (BinaryPrimitives.ReadInt32LittleEndian(b[28..]) != 0) { return false; }
            result[i] = new(BinaryPrimitives.ReadInt32LittleEndian(b), BinaryPrimitives.ReadInt32LittleEndian(b[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(b[8..]), BinaryPrimitives.ReadInt32LittleEndian(b[12..]),
                BinaryPrimitives.ReadInt32LittleEndian(b[16..]), BinaryPrimitives.ReadInt32LittleEndian(b[20..]),
                BinaryPrimitives.ReadInt32LittleEndian(b[24..]));
        }
        regions = result; return true;
    }
    public static bool Validate(ProcessMaterialRegion[] regions, int width, int height, bool background = false)
    {
        if (regions.Length > MaximumRegions) { return false; }
        long area = 0;
        for (int i = 0; i < regions.Length; i++)
        {
            var r = regions[i];
            if (r.X < 0 || r.Y < 0 || r.Width <= 0 || r.Height <= 0 || (long)r.X + r.Width > width
                || (long)r.Y + r.Height > height || r.Radius < 0 || r.Radius > MaximumRadius
                || r.Radius > Math.Min(r.Width, r.Height) / 2 || ((r.Role is < 3 or > 5) && !(background && r.Role == 1)) || r.Effect is < 0 or > 2 || r.Opacity != 0) { return false; }
            area += (long)r.Width * r.Height;
            if (area > (background ? 1920000 : MaximumArea)) { return false; }
            for (int j = 0; j < i; j++)
            {
                var other = regions[j];
                if (r.X < other.X + other.Width && other.X < r.X + r.Width
                    && r.Y < other.Y + other.Height && other.Y < r.Y + r.Height) { return false; }
            }
        }
        return true;
    }
    public static bool ValidForeground(byte[] bytes, int width, int height, ProcessMaterialRegion[] regions)
    {
        if ((long)width * height * 4 != bytes.Length) { return false; }
        for (int i = 0; i < bytes.Length; i += 4)
        {
            int alpha = bytes[i + 3];
            if (bytes[i] > alpha || bytes[i + 1] > alpha || bytes[i + 2] > alpha) { return false; }
            if (alpha == 255) { continue; }
            int pixel = i / 4, x = pixel % width, y = pixel / width; bool allowed = false;
            foreach (var r in regions) { if (r.Contains(x, y)) { allowed = true; break; } }
            if (!allowed) { return false; }
        }
        return true;
    }
}
