using System;
using System.IO;

namespace Zenith.Core.Files;

/// <summary>Small previews of images for Horizon (HZ4, call 660): PNG (interlaced too from HZ5), BMP (24/32-bit) and
/// baseline or progressive JPEG. Every decoder hands rows to a box-filter <see cref="Shrinker"/>, so memory stays at the size of
/// the thumbnail plus one source row; large JPEGs decode only each block's DC value (an exact 1/8 image).
/// Output is ARGB with straight alpha, at most <c>max</c> pixels on the longer side.</summary>
internal static class Thumbnails
{
    public const int MaximumSide = 16384;
    public const long MaximumPixels = 100_000_000;

    public sealed class Result { public int Width, Height; public uint[] Pixels = Array.Empty<uint>(); }

    /// <summary>Null with <paramref name="why"/> set when the image is not one this can read.</summary>
    public static Result? Make(Stream input, int max, out string why)
    {
        why = "";
        var head = new byte[8]; int got = Fill(input, head, 8);
        input.Position = 0;
        try
        {
            if (got >= 8 && head[0] == 0x89 && head[1] == (byte)'P' && head[2] == (byte)'N' && head[3] == (byte)'G') { return Png(input, max); }
            if (got >= 2 && head[0] == (byte)'B' && head[1] == (byte)'M') { return Bmp(input, max); }
            if (got >= 3 && head[0] == 0xff && head[1] == 0xd8 && head[2] == 0xff) { return new Jpeg(input, max).Decode(); }
            why = "not a PNG, BMP or JPEG image"; return null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException)
        { why = e.Message; return null; }
    }

    private static int Fill(Stream s, byte[] b, int n) { int done = 0; while (done < n) { int r = s.Read(b, done, n - done); if (r <= 0) { break; } done += r; } return done; }
    private static void Exactly(Stream s, byte[] b, int n) { if (Fill(s, b, n) != n) { throw new InvalidDataException("the image ends early"); } }
    private static void Check(int w, int h)
    {
        if (w <= 0 || h <= 0 || w > MaximumSide || h > MaximumSide || (long)w * h > MaximumPixels) { throw new NotSupportedException("the image is too large or empty"); }
    }

    /// <summary>Averages source rows into a thumbnail; rows may arrive in any order (BMP stores them upside down).</summary>
    private sealed class Shrinker
    {
        private readonly int _w, _h, _tw, _th;
        private readonly ulong[] _sum; private readonly uint[] _count;
        public Shrinker(int w, int h, int max)
        {
            _w = w; _h = h; double scale = Math.Min(1.0, (double)max / Math.Max(w, h));
            _tw = Math.Max(1, (int)(w * scale)); _th = Math.Max(1, (int)(h * scale));
            _sum = new ulong[_tw * _th * 4]; _count = new uint[_tw * _th];
        }
        /// <summary>One source row of ARGB pixels (straight alpha).</summary>
        public void Row(int y, ReadOnlySpan<uint> row)
        {
            int ty = (int)((long)y * _th / _h) * _tw;
            for (int x = 0; x < _w; x++)
            {
                int t = ty + (int)((long)x * _tw / _w); uint p = row[x]; uint a = p >> 24;
                // Premultiplied sums, so transparent pixels do not darken the average.
                _sum[t * 4] += a; _sum[t * 4 + 1] += (p >> 16 & 255) * a; _sum[t * 4 + 2] += (p >> 8 & 255) * a; _sum[t * 4 + 3] += (p & 255) * a; _count[t]++;
            }
        }
        /// <summary>One source pixel (interlaced PNG passes deliver pixels scattered over the image).</summary>
        public void Point(int x, int y, uint p)
        {
            int t = (int)((long)y * _th / _h) * _tw + (int)((long)x * _tw / _w); uint a = p >> 24;
            _sum[t * 4] += a; _sum[t * 4 + 1] += (p >> 16 & 255) * a; _sum[t * 4 + 2] += (p >> 8 & 255) * a; _sum[t * 4 + 3] += (p & 255) * a; _count[t]++;
        }
        public Result Finish()
        {
            var r = new Result { Width = _tw, Height = _th, Pixels = new uint[_tw * _th] };
            for (int i = 0; i < r.Pixels.Length; i++)
            {
                if (_count[i] == 0 || _sum[i * 4] == 0) { continue; }
                ulong a = _sum[i * 4];
                uint alpha = (uint)(a / _count[i]), red = (uint)(_sum[i * 4 + 1] / a), green = (uint)(_sum[i * 4 + 2] / a), blue = (uint)(_sum[i * 4 + 3] / a);
                r.Pixels[i] = alpha << 24 | red << 16 | green << 8 | blue;
            }
            return r;
        }
    }

    // ---------- PNG.
    /// <summary>The concatenated IDAT data of a PNG, after the zlib header.</summary>
    private sealed class IdatStream : Stream
    {
        private readonly Stream _s; private long _left; private bool _done;
        public IdatStream(Stream s, long first) { _s = s; _left = first; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            while (_left == 0 && !_done)
            {
                var crc = new byte[4]; Exactly(_s, crc, 4);
                var head = new byte[8]; Exactly(_s, head, 8);
                if (head[4] == 'I' && head[5] == 'D' && head[6] == 'A' && head[7] == 'T') { _left = (uint)(head[0] << 24 | head[1] << 16 | head[2] << 8 | head[3]); }
                else { _done = true; }
            }
            if (_done) { return 0; }
            int n = _s.Read(buffer, offset, (int)Math.Min(count, _left)); if (n <= 0) { throw new InvalidDataException("the image ends early"); }
            _left -= n; return n;
        }
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long v) => throw new NotSupportedException();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    }

    private static Result Png(Stream s, int max)
    {
        var b = new byte[8]; Exactly(s, b, 8);
        int w = 0, h = 0, depth = 0, color = 0; uint[] palette = new uint[256]; for (int i = 0; i < 256; i++) { palette[i] = 0xff000000u; }
        int transparentGray = -1; int tr = -1, tg = -1, tb = -1; bool interlaced = false;
        while (true)
        {
            var head = new byte[8]; Exactly(s, head, 8);
            long length = (uint)(head[0] << 24 | head[1] << 16 | head[2] << 8 | head[3]);
            string type = System.Text.Encoding.ASCII.GetString(head, 4, 4);
            if (type == "IDAT") { if (w == 0) { throw new InvalidDataException("no image header"); } return PngData(s, length, w, h, depth, color, palette, transparentGray, tr, tg, tb, max, interlaced); }
            if (length > 1 << 24) { throw new InvalidDataException("damaged PNG"); }
            var data = new byte[length]; Exactly(s, data, (int)length);
            var crc = new byte[4]; Exactly(s, crc, 4);
            if (type == "IHDR")
            {
                w = data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3]; h = data[4] << 24 | data[5] << 16 | data[6] << 8 | data[7];
                depth = data[8]; color = data[9];
                interlaced = data[12] == 1;
                if (data[12] > 1) { throw new NotSupportedException("unsupported PNG interlace method"); }
                if (color is not (0 or 2 or 3 or 4 or 6) || depth is not (1 or 2 or 4 or 8 or 16) || (color is 2 or 4 or 6 && depth < 8)) { throw new NotSupportedException("unsupported PNG format"); }
                Check(w, h);
            }
            else if (type == "PLTE") { for (int i = 0; i + 2 < data.Length && i / 3 < 256; i += 3) { palette[i / 3] = 0xff000000u | (uint)data[i] << 16 | (uint)data[i + 1] << 8 | data[i + 2]; } }
            else if (type == "tRNS")
            {
                if (color == 3) { for (int i = 0; i < data.Length && i < 256; i++) { palette[i] = palette[i] & 0xffffff | (uint)data[i] << 24; } }
                else if (color == 0 && data.Length >= 2) { transparentGray = data[0] << 8 | data[1]; }
                else if (color == 2 && data.Length >= 6) { tr = data[0] << 8 | data[1]; tg = data[2] << 8 | data[3]; tb = data[4] << 8 | data[5]; }
            }
            else if (type == "IEND") { throw new InvalidDataException("no image data"); }
        }
    }
    // Adam7: x start, y start, x step, y step of the seven passes.
    private static readonly int[] Adam7 = { 0, 0, 8, 8, 4, 0, 8, 8, 0, 4, 4, 8, 2, 0, 4, 4, 0, 2, 2, 4, 1, 0, 2, 2, 0, 1, 1, 2 };
    private static Result PngData(Stream s, long first, int w, int h, int depth, int color, uint[] palette, int transparentGray, int tr, int tg, int tb, int max, bool interlaced = false)
    {
        if (interlaced) { return PngInterlaced(s, first, w, h, depth, color, palette, transparentGray, tr, tg, tb, max); }
        var idat = new IdatStream(s, first);
        var zlib = new byte[2]; Exactly(idat, zlib, 2);
        if ((zlib[0] & 15) != 8) { throw new InvalidDataException("damaged PNG data"); }
        int channels = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int bpp = Math.Max(1, channels * depth / 8), stride = (w * channels * depth + 7) / 8;
        byte[] previous = new byte[stride], line = new byte[stride]; uint[] row = new uint[w];
        var shrink = new Shrinker(w, h, max);
        int y = 0, fill = 0, filter = -1;
        new Inflater(idat, (data, offset, count) =>
        {
            for (int i = 0; i < count && y < h; i++)
            {
                byte value = data[offset + i];
                if (filter < 0) { filter = value; if (filter > 4) { throw new InvalidDataException("damaged PNG filter"); } continue; }
                line[fill++] = value;
                if (fill < stride) { continue; }
                Unfilter(filter, line, previous, bpp);
                for (int x = 0; x < w; x++) { row[x] = PngPixel(line, x, depth, color, palette, transparentGray, tr, tg, tb); }
                shrink.Row(y++, row);
                (previous, line) = (line, previous); fill = 0; filter = -1;
            }
        }, (long)(stride + 1) * h).Run();
        if (y < h) { throw new InvalidDataException("the image ends early"); }
        return shrink.Finish();
    }
    /// <summary>An Adam7 PNG: seven reduced images one after another, each with its own filtered rows.</summary>
    private static Result PngInterlaced(Stream s, long first, int w, int h, int depth, int color, uint[] palette, int transparentGray, int tr, int tg, int tb, int max)
    {
        var idat = new IdatStream(s, first);
        var zlib = new byte[2]; Exactly(idat, zlib, 2);
        if ((zlib[0] & 15) != 8) { throw new InvalidDataException("damaged PNG data"); }
        int channels = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int bpp = Math.Max(1, channels * depth / 8);
        var shrink = new Shrinker(w, h, max);
        int pass = -1, pw = 0, ph = 0, stride = 0, y = 0, fill = 0, filter = -1; long total = 0;
        byte[] previous = Array.Empty<byte>(), line = Array.Empty<byte>();
        for (int p = 0; p < 7; p++)
        {
            int cw = w > Adam7[p * 4] ? (w - Adam7[p * 4] + Adam7[p * 4 + 2] - 1) / Adam7[p * 4 + 2] : 0;
            int ch = h > Adam7[p * 4 + 1] ? (h - Adam7[p * 4 + 1] + Adam7[p * 4 + 3] - 1) / Adam7[p * 4 + 3] : 0;
            if (cw > 0 && ch > 0) { total += (long)((cw * channels * depth + 7) / 8 + 1) * ch; }
        }
        bool Next()   // the next pass with pixels; false after the last
        {
            while (++pass < 7)
            {
                pw = w > Adam7[pass * 4] ? (w - Adam7[pass * 4] + Adam7[pass * 4 + 2] - 1) / Adam7[pass * 4 + 2] : 0;
                ph = h > Adam7[pass * 4 + 1] ? (h - Adam7[pass * 4 + 1] + Adam7[pass * 4 + 3] - 1) / Adam7[pass * 4 + 3] : 0;
                if (pw == 0 || ph == 0) { continue; }
                stride = (pw * channels * depth + 7) / 8; previous = new byte[stride]; line = new byte[stride]; y = 0; fill = 0; filter = -1;
                return true;
            }
            return false;
        }
        bool more = Next();
        new Inflater(idat, (data, offset, count) =>
        {
            for (int i = 0; i < count && more; i++)
            {
                byte value = data[offset + i];
                if (filter < 0) { filter = value; if (filter > 4) { throw new InvalidDataException("damaged PNG filter"); } continue; }
                line[fill++] = value;
                if (fill < stride) { continue; }
                Unfilter(filter, line, previous, bpp);
                int sy = Adam7[pass * 4 + 1] + y * Adam7[pass * 4 + 3];
                for (int x = 0; x < pw; x++) { shrink.Point(Adam7[pass * 4] + x * Adam7[pass * 4 + 2], sy, PngPixel(line, x, depth, color, palette, transparentGray, tr, tg, tb)); }
                (previous, line) = (line, previous); fill = 0; filter = -1;
                if (++y == ph) { more = Next(); }
            }
        }, total).Run();
        if (more) { throw new InvalidDataException("the image ends early"); }
        return shrink.Finish();
    }
    private static void Unfilter(int filter, byte[] line, byte[] up, int bpp)
    {
        for (int i = 0; i < line.Length; i++)
        {
            int a = i >= bpp ? line[i - bpp] : 0, b = up[i], c = i >= bpp ? up[i - bpp] : 0;
            int p = filter switch
            {
                1 => a, 2 => b, 3 => (a + b) / 2,
                4 => Paeth(a, b, c),
                _ => 0,
            };
            line[i] = (byte)(line[i] + p);
        }
    }
    private static int Paeth(int a, int b, int c) { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
    private static uint PngPixel(byte[] l, int x, int depth, int color, uint[] palette, int transparentGray, int tr, int tg, int tb)
    {
        int Sample(int index)   // 8- or 16-bit channel value scaled to 8 bits; raw for small depths
        {
            if (depth == 16) { return l[index * 2]; }
            if (depth == 8) { return l[index]; }
            int bit = index * depth; return l[bit / 8] >> (8 - depth - bit % 8) & ((1 << depth) - 1);
        }
        int Raw16(int index) => depth == 16 ? l[index * 2] << 8 | l[index * 2 + 1] : Sample(index);
        switch (color)
        {
            case 3: return palette[Sample(x)];
            case 0:
            {
                int v = Sample(x), g = depth < 8 ? v * 255 / ((1 << depth) - 1) : v;
                uint a = transparentGray >= 0 && Raw16(x) == transparentGray ? 0u : 255u;
                return a << 24 | (uint)g << 16 | (uint)g << 8 | (uint)g;
            }
            case 4: { int g = Sample(x * 2); return (uint)Sample(x * 2 + 1) << 24 | (uint)g << 16 | (uint)g << 8 | (uint)g; }
            case 2:
            {
                uint a = tr >= 0 && Raw16(x * 3) == tr && Raw16(x * 3 + 1) == tg && Raw16(x * 3 + 2) == tb ? 0u : 255u;
                return a << 24 | (uint)Sample(x * 3) << 16 | (uint)Sample(x * 3 + 1) << 8 | (uint)Sample(x * 3 + 2);
            }
            default: return (uint)Sample(x * 4 + 3) << 24 | (uint)Sample(x * 4) << 16 | (uint)Sample(x * 4 + 1) << 8 | (uint)Sample(x * 4 + 2);
        }
    }

    // ---------- BMP.
    private static Result Bmp(Stream s, int max)
    {
        var head = new byte[54]; Exactly(s, head, 54);
        int offset = BitConverter.ToInt32(head, 10), width = BitConverter.ToInt32(head, 18), height = BitConverter.ToInt32(head, 22);
        int bits = BitConverter.ToInt16(head, 28), compression = BitConverter.ToInt32(head, 30);
        bool topDown = height < 0; height = Math.Abs(height);
        if (bits is not (24 or 32) || compression is not (0 or 3)) { throw new NotSupportedException("only 24- and 32-bit BMP images have previews"); }
        Check(width, height);
        int stride = (width * bits / 8 + 3) & ~3;
        s.Position = offset;
        var line = new byte[stride]; var row = new uint[width]; var shrink = new Shrinker(width, height, max);
        for (int i = 0; i < height; i++)
        {
            Exactly(s, line, stride);
            for (int x = 0; x < width; x++)
            {
                int p = x * bits / 8;
                uint a = bits == 32 && compression == 0 ? 255u : bits == 32 ? line[p + 3] : 255u;
                row[x] = a << 24 | (uint)line[p + 2] << 16 | (uint)line[p + 1] << 8 | line[p];
            }
            shrink.Row(topDown ? i : height - 1 - i, row);
        }
        return shrink.Finish();
    }

    // ---------- Baseline JPEG.
    private sealed class Jpeg
    {
        private readonly Stream _s; private readonly int _max;
        private readonly ushort[][] _quant = new ushort[4][];
        private readonly Huff?[] _dc = new Huff?[4], _ac = new Huff?[4];
        private int _width, _height, _restart;
        private Component[] _components = Array.Empty<Component>();
        private int _hmax, _vmax;
        private int _bits, _bitCount, _markerValue = -1; private bool _marker, _progressive;
        public Jpeg(Stream s, int max) { _s = s; _max = max; }

        private sealed class Huff { public readonly int[] MaxCode = new int[18], ValPtr = new int[17]; public readonly int[] MinCode = new int[17]; public byte[] Values = Array.Empty<byte>(); }
        private sealed class Component { public int Id, H, V, Q, Dc, Ac, Prediction, BlocksPerLine, BlockLines, BlocksWide, BlocksHigh; public byte[] Plane = Array.Empty<byte>(); public int PlaneWidth; public short[] Coefficients = Array.Empty<short>(); }

        private int Byte() { int b = _s.ReadByte(); if (b < 0) { throw new InvalidDataException("the image ends early"); } return b; }
        private int Word() => Byte() << 8 | Byte();

        public Result Decode()
        {
            if (Byte() != 0xff || Byte() != 0xd8) { throw new InvalidDataException("not a JPEG"); }
            bool scanned = false;
            while (true)
            {
                int marker;
                if (_markerValue >= 0) { marker = _markerValue; _markerValue = -1; }   // met at the end of a progressive scan
                else
                {
                    marker = Byte(); if (marker != 0xff) { continue; }
                    do { marker = Byte(); } while (marker == 0xff);
                }
                if (marker == 0xd9) { if (_progressive && scanned) { return ProgressiveFinish(); } throw new InvalidDataException("no image data"); }
                if (marker is >= 0xd0 and <= 0xd7) { continue; }
                int length = Word() - 2; if (length < 0) { throw new InvalidDataException("damaged JPEG"); }
                switch (marker)
                {
                    case 0xdb: Quant(length); break;
                    case 0xc4: Tables(length); break;
                    case 0xdd: _restart = Word(); break;
                    case 0xc0: case 0xc1: Frame(); break;
                    case 0xc2: _progressive = true; Frame(); break;
                    case 0xc3: case 0xc5: case 0xc6: case 0xc7: case 0xc9: case 0xca: case 0xcb: case 0xcd: case 0xce: case 0xcf:
                        throw new NotSupportedException("lossless and arithmetic-coded JPEG images have no preview yet");
                    case 0xda: if (!_progressive) { return Scan(); } ProgressiveScan(); scanned = true; break;
                    default: for (int i = 0; i < length; i++) { Byte(); } break;
                }
            }
        }
        private void Quant(int length)
        {
            while (length > 0)
            {
                int pq = Byte(); int id = pq & 3; bool wide = pq >> 4 != 0; var q = new ushort[64];
                for (int i = 0; i < 64; i++) { q[Zigzag[i]] = (ushort)(wide ? Word() : Byte()); }
                _quant[id] = q; length -= 1 + (wide ? 128 : 64);
            }
        }
        private void Tables(int length)
        {
            while (length > 0)
            {
                int tc = Byte(); var counts = new int[17]; int total = 0;
                for (int i = 1; i <= 16; i++) { counts[i] = Byte(); total += counts[i]; }
                var h = new Huff { Values = new byte[total] };
                for (int i = 0; i < total; i++) { h.Values[i] = (byte)Byte(); }
                int code = 0, k = 0;
                for (int l = 1; l <= 16; l++)
                {
                    h.ValPtr[l] = k; h.MinCode[l] = code; code += counts[l]; k += counts[l];
                    h.MaxCode[l] = counts[l] > 0 ? code - 1 : -1; code <<= 1;
                }
                h.MaxCode[17] = int.MaxValue;
                if ((tc >> 4) == 0) { _dc[tc & 3] = h; } else { _ac[tc & 3] = h; }
                length -= 17 + total;
            }
        }
        private void Frame()
        {
            if (Byte() != 8) { throw new NotSupportedException("12-bit JPEG"); }
            _height = Word(); _width = Word(); Check(_width, _height);
            int n = Byte(); if (n is not (1 or 3)) { throw new NotSupportedException("only grey and colour JPEG images have previews"); }
            _components = new Component[n];
            for (int i = 0; i < n; i++)
            {
                int id = Byte(), hv = Byte(), q = Byte();
                _components[i] = new Component { Id = id, H = Math.Max(1, hv >> 4), V = Math.Max(1, hv & 15), Q = q & 3 };
                _hmax = Math.Max(_hmax, _components[i].H); _vmax = Math.Max(_vmax, _components[i].V);
            }
        }
        private int Bit()
        {
            if (_bitCount == 0)
            {
                if (_marker) { _bits = 0; _bitCount = 8; }
                else
                {
                    int b = Byte();
                    if (b == 0xff) { int next = Byte(); while (next == 0xff) { next = Byte(); } if (next != 0) { _marker = true; _markerValue = next is >= 0xd0 and <= 0xd7 ? -1 : next; b = 0; } }
                    _bits = b; _bitCount = 8;
                }
            }
            _bitCount--; return _bits >> _bitCount & 1;
        }
        private int Receive(int n) { int v = 0; for (int i = 0; i < n; i++) { v = v << 1 | Bit(); } return v; }
        private static int Extend(int v, int n) => n == 0 ? 0 : v < 1 << (n - 1) ? v - (1 << n) + 1 : v;
        private int DecodeHuff(Huff h)
        {
            int code = Bit(), l = 1;
            while (l <= 16 && code > h.MaxCode[l]) { code = code << 1 | Bit(); l++; }
            if (l > 16) { throw new InvalidDataException("damaged JPEG data"); }
            return h.Values[h.ValPtr[l] + code - h.MinCode[l]];
        }
        private void Restart()
        {
            // Skip to the RSTn marker, then start the bit stream and the DC predictions afresh.
            _bitCount = 0;
            if (!_marker) { int b; do { b = Byte(); } while (b != 0xff); int m; do { m = Byte(); } while (m == 0xff); }
            _marker = false; _markerValue = -1; _eobRun = 0;
            foreach (var c in _components) { c.Prediction = 0; }
        }

        // ---------- Progressive JPEG: coefficients collected over several scans, transformed once at the end.
        private int _eobRun; private bool _dcOnly;
        private void Prepare()
        {
            if (_components[0].Coefficients.Length > 0) { return; }
            // Large images keep the DC value of each block only (each 8x8 block becomes one pixel); AC scans are skipped.
            _dcOnly = Math.Max(_width, _height) > 2 * _max * 2;
            int mcusX = (_width + 8 * _hmax - 1) / (8 * _hmax), mcusY = (_height + 8 * _vmax - 1) / (8 * _vmax);
            foreach (var c in _components)
            {
                if (_quant[c.Q] is null) { throw new InvalidDataException("missing JPEG tables"); }
                c.BlocksPerLine = mcusX * c.H; c.BlockLines = mcusY * c.V;
                c.BlocksWide = ((_width * c.H + _hmax - 1) / _hmax + 7) / 8; c.BlocksHigh = ((_height * c.V + _vmax - 1) / _vmax + 7) / 8;
                c.Coefficients = new short[c.BlocksPerLine * c.BlockLines * (_dcOnly ? 1 : 64)];
            }
        }
        private void ProgressiveScan()
        {
            Prepare();
            int n = Byte(); var scan = new Component[n];
            for (int i = 0; i < n; i++)
            {
                int id = Byte(), tables = Byte();
                Component? found = null; foreach (var c in _components) { if (c.Id == id) { found = c; } }
                scan[i] = found ?? throw new InvalidDataException("damaged JPEG scan");
                found.Dc = tables >> 4; found.Ac = tables & 15;
            }
            int ss = Byte(), se = Byte(), a = Byte(), ah = a >> 4, al = a & 15;
            if (ss > se || se > 63 || (ss == 0 && se != 0) || (ss > 0 && n != 1) || al > 13) { throw new InvalidDataException("damaged JPEG scan"); }
            _bitCount = 0; _marker = false; _eobRun = 0; foreach (var c in _components) { c.Prediction = 0; }
            if (ss > 0 && _dcOnly) { SkipScan(); return; }
            foreach (var c in scan)
            {
                if (ss == 0 && ah == 0 && _dc[c.Dc] is null) { throw new InvalidDataException("missing JPEG tables"); }
                if (ss > 0 && _ac[c.Ac] is null) { throw new InvalidDataException("missing JPEG tables"); }
            }
            int stride = _dcOnly ? 1 : 64, units = 0;
            void Unit(Component c, int bx, int by)
            {
                int at = (by * c.BlocksPerLine + bx) * stride;
                if (ss == 0) { if (ah == 0) { DcFirst(c, at, al); } else if (Bit() != 0) { c.Coefficients[at] |= (short)(1 << al); } }
                else if (ah == 0) { AcFirst(c, at, ss, se, al); }
                else { AcRefine(c, at, ss, se, al); }
            }
            void Count() { if (_restart > 0 && units > 0 && units % _restart == 0) { Restart(); } units++; }
            if (n == 1)
            {
                // Non-interleaved: the component's own blocks in raster order, without the MCU padding.
                var c = scan[0];
                for (int by = 0; by < c.BlocksHigh; by++) { for (int bx = 0; bx < c.BlocksWide; bx++) { Count(); Unit(c, bx, by); } }
            }
            else
            {
                for (int my = 0; my < _components[0].BlockLines / _components[0].V; my++)
                {
                    for (int mx = 0; mx < _components[0].BlocksPerLine / _components[0].H; mx++)
                    {
                        Count();
                        foreach (var c in scan) { for (int by = 0; by < c.V; by++) { for (int bx = 0; bx < c.H; bx++) { Unit(c, mx * c.H + bx, my * c.V + by); } } }
                    }
                }
            }
            _bitCount = 0;
            if (_marker) { _marker = false; if (_markerValue < 0) { SkipScan(); } }   // ended on a restart marker: find the next real one
        }
        private void DcFirst(Component c, int at, int al)
        {
            int t = DecodeHuff(_dc[c.Dc]!); int diff = t == 0 ? 0 : Extend(Receive(t), t);
            c.Prediction += diff; c.Coefficients[at] = (short)(c.Prediction << al);
        }
        private void AcFirst(Component c, int at, int ss, int se, int al)
        {
            if (_eobRun > 0) { _eobRun--; return; }
            var ac = _ac[c.Ac]!;
            for (int k = ss; k <= se;)
            {
                int rs = DecodeHuff(ac), r = rs >> 4, s = rs & 15;
                if (s == 0)
                {
                    if (r < 15) { _eobRun = (1 << r) - 1; if (r > 0) { _eobRun += Receive(r); } break; }
                    k += 16; continue;
                }
                k += r; if (k > 63) { throw new InvalidDataException("damaged JPEG block"); }
                c.Coefficients[at + k] = (short)(Extend(Receive(s), s) << al); k++;
            }
        }
        private void AcRefine(Component c, int at, int ss, int se, int al)
        {
            short p1 = (short)(1 << al), m1 = (short)(-1 << al); var co = c.Coefficients;
            void Refine(int i) { if (co[i] != 0 && Bit() != 0 && (co[i] & p1) == 0) { co[i] += co[i] >= 0 ? p1 : m1; } }
            int k = ss;
            if (_eobRun > 0) { _eobRun--; for (; k <= se; k++) { Refine(at + k); } return; }
            var ac = _ac[c.Ac]!;
            while (k <= se)
            {
                int rs = DecodeHuff(ac), r = rs >> 4, s = rs & 15; short value = 0;
                if (s == 0)
                {
                    if (r < 15) { _eobRun = (1 << r) - 1; if (r > 0) { _eobRun += Receive(r); } r = 64; }   // refine the rest of this block, then the run continues
                }
                else { if (s != 1) { throw new InvalidDataException("damaged JPEG block"); } value = Bit() != 0 ? p1 : m1; }
                while (k <= se)
                {
                    int i = at + k++;
                    if (co[i] != 0) { Refine(i); }
                    else { if (r == 0) { if (value != 0) { co[i] = value; } break; } r--; }
                }
            }
        }
        /// <summary>Skips entropy-coded data up to the next marker other than a restart.</summary>
        private void SkipScan()
        {
            while (true)
            {
                if (Byte() != 0xff) { continue; }
                int m = Byte(); while (m == 0xff) { m = Byte(); }
                if (m == 0 || m is >= 0xd0 and <= 0xd7) { continue; }
                _markerValue = m; return;
            }
        }
        private Result ProgressiveFinish()
        {
            int scale = _dcOnly ? 8 : 1;
            int mcuH = 8 * _vmax, mcusX = _components[0].BlocksPerLine / _components[0].H, mcusY = _components[0].BlockLines / _components[0].V;
            int outW = (_width + scale - 1) / scale, outH = (_height + scale - 1) / scale;
            foreach (var c in _components) { c.PlaneWidth = mcusX * c.H * 8 / scale; c.Plane = new byte[c.PlaneWidth * c.V * 8 / scale]; }
            var shrink = new Shrinker(outW, outH, _max); var row = new uint[outW]; var block = new int[64];
            for (int my = 0; my < mcusY; my++)
            {
                foreach (var c in _components)
                {
                    var q = _quant[c.Q];
                    for (int by = 0; by < c.V; by++)
                    {
                        for (int bx = 0; bx < c.BlocksPerLine; bx++)
                        {
                            int at = ((my * c.V + by) * c.BlocksPerLine + bx) * (_dcOnly ? 1 : 64);
                            if (_dcOnly) { c.Plane[by * c.PlaneWidth + bx] = Clamp((c.Coefficients[at] * q[0] >> 3) + 128); continue; }
                            for (int k = 0; k < 64; k++) { block[Zigzag[k]] = c.Coefficients[at + k] * q[Zigzag[k]]; }
                            Idct(block, c.Plane, by * 8 * c.PlaneWidth + bx * 8, c.PlaneWidth);
                        }
                    }
                }
                EmitRows(my, mcuH / scale, outW, outH, shrink, row);
            }
            return shrink.Finish();
        }
        /// <summary>Colour converts one MCU row of the component planes into thumbnail rows.</summary>
        private void EmitRows(int my, int rows, int outW, int outH, Shrinker shrink, uint[] row)
        {
            for (int r = 0; r < rows; r++)
            {
                int y = my * rows + r; if (y >= outH) { break; }
                for (int x = 0; x < outW; x++)
                {
                    if (_components.Length == 1) { int g = Sample(_components[0], x, r); row[x] = 0xff000000u | (uint)(g << 16 | g << 8 | g); continue; }
                    int Y = Sample(_components[0], x, r), Cb = Sample(_components[1], x, r) - 128, Cr = Sample(_components[2], x, r) - 128;
                    int R = Clamp(Y + ((91881 * Cr) >> 16)), G = Clamp(Y - ((22554 * Cb + 46802 * Cr) >> 16)), B = Clamp(Y + ((116130 * Cb) >> 16));
                    row[x] = 0xff000000u | (uint)(R << 16 | G << 8 | B);
                }
                shrink.Row(y, row);
            }
        }

        private Result Scan()
        {
            int n = Byte();
            for (int i = 0; i < n; i++)
            {
                int id = Byte(), tables = Byte();
                foreach (var c in _components) { if (c.Id == id) { c.Dc = tables >> 4; c.Ac = tables & 15; } }
            }
            Byte(); Byte(); Byte();   // spectral selection and approximation: fixed for baseline
            if (n != _components.Length) { throw new NotSupportedException("JPEG images with several scans have no preview yet"); }
            // Large images: DC only (each 8x8 block becomes one pixel); small ones: the full inverse DCT.
            bool dcOnly = Math.Max(_width, _height) > 2 * _max * 2;
            int scale = dcOnly ? 8 : 1;
            int mcuW = 8 * _hmax, mcuH = 8 * _vmax, mcusX = (_width + mcuW - 1) / mcuW, mcusY = (_height + mcuH - 1) / mcuH;
            int outW = (_width + scale - 1) / scale, outH = (_height + scale - 1) / scale;
            foreach (var c in _components)
            {
                c.PlaneWidth = mcusX * c.H * 8 / scale;
                c.Plane = new byte[c.PlaneWidth * c.V * 8 / scale];   // one MCU row of this component
                if (_quant[c.Q] is null || _dc[c.Dc] is null || _ac[c.Ac] is null) { throw new InvalidDataException("missing JPEG tables"); }
            }
            var shrink = new Shrinker(outW, outH, _max); var row = new uint[outW];
            var block = new int[64]; int mcus = 0;
            for (int my = 0; my < mcusY; my++)
            {
                for (int mx = 0; mx < mcusX; mx++)
                {
                    if (_restart > 0 && mcus > 0 && mcus % _restart == 0) { Restart(); }
                    mcus++;
                    foreach (var c in _components)
                    {
                        for (int by = 0; by < c.V; by++)
                        {
                            for (int bx = 0; bx < c.H; bx++)
                            {
                                DecodeBlock(c, block, dcOnly);
                                int px = (mx * c.H + bx) * 8 / scale, py = by * 8 / scale;
                                if (dcOnly) { c.Plane[py * c.PlaneWidth + px] = Clamp((block[0] >> 3) + 128); }
                                else { Idct(block, c.Plane, py * c.PlaneWidth + px, c.PlaneWidth); }
                            }
                        }
                    }
                }
                // Colour convert this MCU row into thumbnail rows.
                int rows = mcuH / scale;
                for (int r = 0; r < rows; r++)
                {
                    int y = my * rows + r; if (y >= outH) { break; }
                    for (int x = 0; x < outW; x++)
                    {
                        if (_components.Length == 1) { int g = Sample(_components[0], x, r); row[x] = 0xff000000u | (uint)(g << 16 | g << 8 | g); continue; }
                        int Y = Sample(_components[0], x, r), Cb = Sample(_components[1], x, r) - 128, Cr = Sample(_components[2], x, r) - 128;
                        int R = Clamp(Y + ((91881 * Cr) >> 16)), G = Clamp(Y - ((22554 * Cb + 46802 * Cr) >> 16)), B = Clamp(Y + ((116130 * Cb) >> 16));
                        row[x] = 0xff000000u | (uint)(R << 16 | G << 8 | B);
                    }
                    shrink.Row(y, row);
                }
            }
            return shrink.Finish();
        }
        private int Sample(Component c, int x, int r) => c.Plane[(r * c.V / _vmax) * c.PlaneWidth + Math.Min(c.PlaneWidth - 1, x * c.H / _hmax)];
        private void DecodeBlock(Component c, int[] block, bool dcOnly)
        {
            Array.Clear(block);
            int t = DecodeHuff(_dc[c.Dc]!); int diff = t == 0 ? 0 : Extend(Receive(t), t);
            c.Prediction += diff; var q = _quant[c.Q];
            block[0] = c.Prediction * q[0];
            var ac = _ac[c.Ac]!;
            for (int k = 1; k < 64;)
            {
                int rs = DecodeHuff(ac), r = rs >> 4, s = rs & 15;
                if (s == 0) { if (r == 15) { k += 16; continue; } break; }
                k += r; if (k > 63) { throw new InvalidDataException("damaged JPEG block"); }
                int value = Extend(Receive(s), s);
                if (!dcOnly) { block[Zigzag[k]] = value * q[Zigzag[k]]; }
                k++;
            }
        }
        private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        private static readonly int[] Zigzag = { 0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63 };
        private static readonly int[] Cos = CosTable();
        private static int[] CosTable()
        {
            // c(u) * cos((2x+1) u pi / 16) in 1/4096 units.
            var t = new int[64];
            for (int x = 0; x < 8; x++) { for (int u = 0; u < 8; u++) { double cu = u == 0 ? Math.Sqrt(0.5) : 1; t[x * 8 + u] = (int)Math.Round(4096 * cu * Math.Cos((2 * x + 1) * u * Math.PI / 16)); } }
            return t;
        }
        private static void Idct(int[] block, byte[] plane, int at, int stride)
        {
            var tmp = new int[64];
            for (int y = 0; y < 8; y++) { for (int x = 0; x < 8; x++) { long s = 0; for (int u = 0; u < 8; u++) { s += (long)Cos[x * 8 + u] * block[y * 8 + u]; } tmp[y * 8 + x] = (int)(s >> 12); } }
            for (int x = 0; x < 8; x++) { for (int y = 0; y < 8; y++) { long s = 0; for (int v = 0; v < 8; v++) { s += (long)Cos[y * 8 + v] * tmp[v * 8 + x]; } plane[at + y * stride + x] = Clamp((int)(s >> 14) + 128); } }
        }
    }
}
