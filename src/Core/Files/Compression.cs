using System;
using System.IO;

namespace Zenith.Core.Files;

/// <summary>CRC-32 (IEEE 802.3), as zip and gzip use it.</summary>
internal static class Crc32
{
    private static readonly uint[] s_table = Table();
    private static uint[] Table()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) { c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; } t[n] = c; }
        return t;
    }
    /// <summary>Continues a CRC: start from 0, pass the previous result for later chunks.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint c = ~crc;
        foreach (byte b in data) { c = s_table[(c ^ b) & 0xff] ^ (c >> 8); }
        return ~c;
    }
}

/// <summary>DEFLATE decoder (RFC 1951) over a stream, with a 32 KiB window: stored, fixed and dynamic blocks.
/// Output goes to a sink in chunks; <c>limit</c> bounds it (a zip bomb stops with an error).</summary>
internal sealed class Inflater
{
    private readonly Stream _input;
    private readonly Action<byte[], int, int> _sink;
    private readonly long _limit;
    private readonly byte[] _in = new byte[16384], _window = new byte[32768], _out = new byte[32768];
    private int _inPos, _inLen, _bits, _bitCount, _outLen;
    private long _written;
    /// <summary>Compressed bytes taken from the input so far (progress).</summary>
    public long Consumed { get; private set; }
    public long Written => _written;

    public Inflater(Stream input, Action<byte[], int, int> sink, long limit) { _input = input; _sink = sink; _limit = limit; }

    private int Byte()
    {
        if (_inPos == _inLen)
        {
            _inLen = _input.Read(_in, 0, _in.Length); _inPos = 0;
            if (_inLen <= 0) { throw new InvalidDataException("the compressed data ends early"); }
        }
        Consumed++;
        return _in[_inPos++];
    }
    private int Bits(int n)
    {
        while (_bitCount < n) { _bits |= Byte() << _bitCount; _bitCount += 8; }
        int value = _bits & ((1 << n) - 1); _bits >>= n; _bitCount -= n; return value;
    }
    /// <summary>After the last block: the next whole byte of input (gzip's trailer follows the data).</summary>
    public int AlignedByte()
    {
        int drop = _bitCount % 8; _bits >>= drop; _bitCount -= drop;
        if (_bitCount >= 8) { int b = _bits & 0xff; _bits >>= 8; _bitCount -= 8; return b; }
        return Byte();
    }
    private void Put(byte b)
    {
        _window[(int)(_written & 32767)] = b; _out[_outLen++] = b;
        if (++_written > _limit) { throw new InvalidDataException("the data expands past its limit"); }
        if (_outLen == _out.Length) { _sink(_out, 0, _outLen); _outLen = 0; }
    }

    private sealed class Huffman { public readonly short[] Count = new short[16]; public readonly short[] Symbol; public Huffman(int n) { Symbol = new short[n]; } }
    private static Huffman Build(ReadOnlySpan<byte> lengths)
    {
        var h = new Huffman(lengths.Length);
        foreach (byte l in lengths) { h.Count[l]++; }
        h.Count[0] = 0;
        int left = 1;
        for (int len = 1; len < 16; len++) { left <<= 1; left -= h.Count[len]; if (left < 0) { throw new InvalidDataException("bad Huffman lengths"); } }
        var offsets = new short[16];
        for (int len = 1; len < 15; len++) { offsets[len + 1] = (short)(offsets[len] + h.Count[len]); }
        for (int s = 0; s < lengths.Length; s++) { if (lengths[s] != 0) { h.Symbol[offsets[lengths[s]]++] = (short)s; } }
        return h;
    }
    private int Decode(Huffman h)
    {
        int code = 0, first = 0, index = 0;
        for (int len = 1; len < 16; len++)
        {
            code |= Bits(1);
            int count = h.Count[len];
            if (code - count < first) { return h.Symbol[index + (code - first)]; }
            index += count; first += count; first <<= 1; code <<= 1;
        }
        throw new InvalidDataException("bad Huffman code");
    }

    private static readonly short[] LengthBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
    private static readonly short[] LengthExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
    private static readonly short[] DistanceBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
    private static readonly short[] DistanceExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
    private static Huffman? s_fixedLength, s_fixedDistance;

    /// <summary>Decodes every block up to the last one; returns the bytes written.</summary>
    public long Run()
    {
        bool last;
        do
        {
            last = Bits(1) == 1;
            int type = Bits(2);
            if (type == 0) { Stored(); }
            else if (type == 1)
            {
                if (s_fixedLength is null)
                {
                    var l = new byte[288];
                    for (int i = 0; i < 288; i++) { l[i] = (byte)(i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8); }
                    var d = new byte[30]; Array.Fill(d, (byte)5);
                    s_fixedLength = Build(l); s_fixedDistance = Build(d);
                }
                Codes(s_fixedLength, s_fixedDistance!);
            }
            else if (type == 2) { Dynamic(); }
            else { throw new InvalidDataException("bad block type"); }
        } while (!last);
        if (_outLen > 0) { _sink(_out, 0, _outLen); _outLen = 0; }
        return _written;
    }
    private void Stored()
    {
        _bits = 0; _bitCount = 0;   // to a byte boundary
        int len = Byte() | Byte() << 8, nlen = Byte() | Byte() << 8;
        if (len != (~nlen & 0xffff)) { throw new InvalidDataException("bad stored block"); }
        for (int i = 0; i < len; i++) { Put((byte)Byte()); }
    }
    private static readonly byte[] Order = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };
    private void Dynamic()
    {
        int nlen = Bits(5) + 257, ndist = Bits(5) + 1, ncode = Bits(4) + 4;
        if (nlen > 286 || ndist > 30) { throw new InvalidDataException("bad dynamic block"); }
        var lengths = new byte[320];
        for (int i = 0; i < ncode; i++) { lengths[Order[i]] = (byte)Bits(3); }
        var codes = Build(lengths.AsSpan(0, 19));
        Array.Clear(lengths);
        for (int index = 0; index < nlen + ndist;)
        {
            int symbol = Decode(codes);
            if (symbol < 16) { lengths[index++] = (byte)symbol; continue; }
            byte value = 0; int repeat;
            if (symbol == 16) { if (index == 0) { throw new InvalidDataException("repeat with no length"); } value = lengths[index - 1]; repeat = 3 + Bits(2); }
            else if (symbol == 17) { repeat = 3 + Bits(3); }
            else { repeat = 11 + Bits(7); }
            if (index + repeat > nlen + ndist) { throw new InvalidDataException("too many lengths"); }
            while (repeat-- > 0) { lengths[index++] = value; }
        }
        if (lengths[256] == 0) { throw new InvalidDataException("no end-of-block code"); }
        Codes(Build(lengths.AsSpan(0, nlen)), Build(lengths.AsSpan(nlen, ndist)));
    }
    private void Codes(Huffman lengthCodes, Huffman distanceCodes)
    {
        while (true)
        {
            int symbol = Decode(lengthCodes);
            if (symbol < 256) { Put((byte)symbol); continue; }
            if (symbol == 256) { return; }
            symbol -= 257;
            if (symbol >= 29) { throw new InvalidDataException("bad length code"); }
            int length = LengthBase[symbol] + Bits(LengthExtra[symbol]);
            int d = Decode(distanceCodes);
            if (d >= 30) { throw new InvalidDataException("bad distance code"); }
            int distance = DistanceBase[d] + Bits(DistanceExtra[d]);
            if (distance > _written) { throw new InvalidDataException("distance before the start"); }
            for (int i = 0; i < length; i++) { Put(_window[(int)((_written - distance) & 32767)]); }
        }
    }

    /// <summary>A whole raw DEFLATE stream from <paramref name="input"/> to <paramref name="output"/>.</summary>
    public static long Decompress(Stream input, Stream output, long limit)
        => new Inflater(input, (b, o, n) => output.Write(b, o, n), limit).Run();
}

/// <summary>DEFLATE encoder: greedy LZ77 over a 32 KiB window with hash chains, one fixed-Huffman block.
/// Fast, small and always valid; dynamic Huffman tables would compress text a little better.</summary>
internal sealed class Deflater
{
    private const int Window = 32768, Lookahead = 258, MaxChain = 48, HashBits = 15;
    private readonly Action<byte[], int, int> _sink;
    private readonly byte[] _buffer = new byte[Window * 2 + Lookahead * 2];
    private readonly int[] _head = new int[1 << HashBits], _prev = new int[Window];
    private int _start, _end;   // _buffer[_start.._end) not yet encoded; earlier bytes are the window
    private readonly byte[] _out = new byte[16384];
    private int _outLen; private uint _bits; private int _bitCount;
    private bool _finished;

    public Deflater(Action<byte[], int, int> sink)
    {
        _sink = sink; Array.Fill(_head, -1);
        PutBits(1, 1); PutBits(1, 2);   // BFINAL=1, BTYPE=01 (fixed): one block for the whole stream
    }

    private void PutBits(uint value, int count)
    {
        _bits |= value << _bitCount; _bitCount += count;
        while (_bitCount >= 8) { _out[_outLen++] = (byte)_bits; _bits >>= 8; _bitCount -= 8; if (_outLen == _out.Length) { _sink(_out, 0, _outLen); _outLen = 0; } }
    }
    private static uint Reverse(uint code, int length) { uint r = 0; for (int i = 0; i < length; i++) { r = (r << 1) | (code & 1); code >>= 1; } return r; }
    private void Literal(int symbol)
    {
        if (symbol < 144) { PutBits(Reverse((uint)(0x30 + symbol), 8), 8); }
        else if (symbol < 256) { PutBits(Reverse((uint)(0x190 + symbol - 144), 9), 9); }
        else if (symbol < 280) { PutBits(Reverse((uint)(symbol - 256), 7), 7); }
        else { PutBits(Reverse((uint)(0xC0 + symbol - 280), 8), 8); }
    }
    private static readonly short[] LengthBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
    private static readonly short[] LengthExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
    private static readonly int[] DistanceBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
    private static readonly short[] DistanceExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
    private void Match(int length, int distance)
    {
        int l = 28; while (LengthBase[l] > length) { l--; }
        Literal(257 + l); if (LengthExtra[l] > 0) { PutBits((uint)(length - LengthBase[l]), LengthExtra[l]); }
        int d = 29; while (DistanceBase[d] > distance) { d--; }
        PutBits(Reverse((uint)d, 5), 5); if (DistanceExtra[d] > 0) { PutBits((uint)(distance - DistanceBase[d]), DistanceExtra[d]); }
    }
    private int Hash(int p) => ((_buffer[p] << 10) ^ (_buffer[p + 1] << 5) ^ _buffer[p + 2]) & ((1 << HashBits) - 1);

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_finished) { throw new InvalidOperationException("deflater finished"); }
        while (data.Length > 0)
        {
            if (_end == _buffer.Length) { Slide(); }
            int n = Math.Min(data.Length, _buffer.Length - _end);
            data[..n].CopyTo(_buffer.AsSpan(_end)); _end += n; data = data[n..];
            Encode(Lookahead);
        }
    }
    public void Finish()
    {
        if (_finished) { return; }
        Encode(0);
        Literal(256);   // end of block
        if (_bitCount > 0) { PutBits(0, 8 - _bitCount); }
        if (_outLen > 0) { _sink(_out, 0, _outLen); _outLen = 0; }
        _finished = true;
    }
    /// <summary>Keeps the last window of history; positions in the hash chains move with it.</summary>
    private void Slide()
    {
        // Whole windows only: a position keeps its slot in _prev (p & (Window - 1)) after the shift.
        int shift = (_start - Window) / Window * Window; if (shift <= 0) { return; }
        Array.Copy(_buffer, shift, _buffer, 0, _end - shift);
        _start -= shift; _end -= shift;
        for (int i = 0; i < _head.Length; i++) { _head[i] = _head[i] >= shift ? _head[i] - shift : -1; }
        for (int i = 0; i < _prev.Length; i++) { _prev[i] = _prev[i] >= shift ? _prev[i] - shift : -1; }
    }
    private void Insert(int p)
    {
        if (p + 2 >= _end) { return; }
        int h = Hash(p); _prev[p & (Window - 1)] = _head[h]; _head[h] = p;
    }
    /// <summary>Encodes while at least <paramref name="keep"/> bytes stay ahead (0: everything).</summary>
    private void Encode(int keep)
    {
        while (_end - _start > keep)
        {
            if (_start >= _buffer.Length - Lookahead - 1 && keep > 0) { Slide(); if (_end - _start <= keep) { return; } }
            int best = 0, distance = 0, available = Math.Min(258, _end - _start);
            if (available >= 3)
            {
                int candidate = _head[Hash(_start)], chain = MaxChain;
                while (candidate >= 0 && _start - candidate <= Window && chain-- > 0)
                {
                    if (candidate < _start && _buffer[candidate + best] == _buffer[_start + best])
                    {
                        int length = 0; while (length < available && _buffer[candidate + length] == _buffer[_start + length]) { length++; }
                        if (length > best) { best = length; distance = _start - candidate; if (length == available) { break; } }
                    }
                    int next = _prev[candidate & (Window - 1)]; if (next >= candidate) { break; } candidate = next;
                }
            }
            if (best >= 3)
            {
                Match(best, distance);
                for (int i = 0; i < best; i++) { Insert(_start + i); }
                _start += best;
            }
            else { Literal(_buffer[_start]); Insert(_start); _start++; }
        }
    }

    public static void Compress(Stream input, Stream output)
    {
        var d = new Deflater((b, o, n) => output.Write(b, o, n));
        var chunk = new byte[65536]; int n;
        while ((n = input.Read(chunk, 0, chunk.Length)) > 0) { d.Write(chunk.AsSpan(0, n)); }
        d.Finish();
    }
}
