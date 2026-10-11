using System;

namespace Zenith.Core.Security;

internal static class Sha256
{
    private static readonly uint[] K =
    {
        0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
        0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
        0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
        0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
        0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
        0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
        0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
        0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2,
    };

    internal static void Hash(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (output.Length != 32) { throw new ArgumentException("SHA-256 output must have 32 bytes"); }
        Span<uint> state = stackalloc uint[8] { 0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19 };
        Span<uint> schedule = stackalloc uint[64];
        int offset = 0;
        while (input.Length - offset >= 64) { Compress(input.Slice(offset, 64), state, schedule); offset += 64; }
        Span<byte> tail = stackalloc byte[128]; tail.Clear();
        input.Slice(offset).CopyTo(tail); tail[input.Length - offset] = 0x80;
        int length = input.Length - offset < 56 ? 64 : 128;
        ulong bits = (ulong)input.Length * 8;
        for (int i = 0; i < 8; i++) { tail[length - 1 - i] = (byte)(bits >> (i * 8)); }
        Compress(tail.Slice(0, 64), state, schedule);
        if (length == 128) { Compress(tail.Slice(64), state, schedule); }
        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < 4; j++) { output[i * 4 + j] = (byte)(state[i] >> (24 - j * 8)); }
        }
        state.Clear(); schedule.Clear(); tail.Clear();
    }

    private static uint R(uint x, int n) => (x >> n) | (x << (32 - n));
    private static void Compress(ReadOnlySpan<byte> block, Span<uint> h, Span<uint> w)
    {
        unchecked
        {
            for (int i = 0; i < 16; i++) { int p = i * 4; w[i] = (uint)block[p] << 24 | (uint)block[p + 1] << 16 | (uint)block[p + 2] << 8 | block[p + 3]; }
            for (int i = 16; i < 64; i++)
            {
                uint a = w[i - 15], b = w[i - 2];
                w[i] = w[i - 16] + (R(a,7) ^ R(a,18) ^ (a >> 3)) + w[i - 7] + (R(b,17) ^ R(b,19) ^ (b >> 10));
            }
            uint a0=h[0], b0=h[1], c=h[2], d=h[3], e=h[4], f=h[5], g=h[6], hh=h[7];
            for (int i = 0; i < 64; i++)
            {
                uint t1 = hh + (R(e,6)^R(e,11)^R(e,25)) + ((e&f)^(~e&g)) + K[i] + w[i];
                uint t2 = (R(a0,2)^R(a0,13)^R(a0,22)) + ((a0&b0)^(a0&c)^(b0&c));
                hh=g; g=f; f=e; e=d+t1; d=c; c=b0; b0=a0; a0=t1+t2;
            }
            h[0]+=a0; h[1]+=b0; h[2]+=c; h[3]+=d; h[4]+=e; h[5]+=f; h[6]+=g; h[7]+=hh;
        }
    }
}
