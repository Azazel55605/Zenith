using System;
using System.Text;

namespace Zenith.Core.Security;

/// <summary>Versioned PBKDF2-HMAC-SHA256 records; no OS crypto P/Invoke dependencies.</summary>
internal static class PasswordHash
{
    public const int Iterations = 600000;
    private const string Scheme = "zenith-pbkdf2-sha256";

    public static string Create(string password, IEntropySource entropy)
    {
        Validate(password);
        byte[] salt = new byte[16];
        byte[] hash = new byte[32];
        try
        {
            if (!entropy.TryFill(salt)) { throw new InvalidOperationException("secure entropy unavailable; password unchanged"); }
            Derive(password, salt, Iterations, hash);
            return "$" + Scheme + "$" + Iterations + "$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(hash);
        }
        finally { salt.AsSpan().Clear(); hash.AsSpan().Clear(); }
    }

    public static bool Verify(string password, string record)
    {
        if (password.Length == 0 || password.IndexOf('\0') >= 0 || Encoding.UTF8.GetByteCount(password) > 1024 || record.Length > 160) { return false; }
        string[] fields = record.Split('$');
        if (fields.Length != 5 || fields[0].Length != 0 || fields[1] != Scheme
            || !int.TryParse(fields[2], out int rounds) || rounds < Iterations || rounds > Iterations * 2
            || fields[2] != rounds.ToString() || fields[3].Length != 24 || fields[4].Length != 44) { return false; }
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(fields[3]); expected = Convert.FromBase64String(fields[4]); }
        catch (FormatException) { return false; }
        byte[] actual = new byte[32];
        try
        {
            if (salt.Length != 16 || expected.Length != 32
                || Convert.ToBase64String(salt) != fields[3] || Convert.ToBase64String(expected) != fields[4]) { return false; }
            Derive(password, salt, rounds, actual);
            int difference = 0;
            for (int i = 0; i < 32; i++) { difference |= actual[i] ^ expected[i]; }
            return difference == 0;
        }
        finally { salt.AsSpan().Clear(); expected.AsSpan().Clear(); actual.AsSpan().Clear(); }
    }

    private static void Validate(string password)
    {
        if (password.Length == 0 || Encoding.UTF8.GetByteCount(password) > 1024 || password.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("password must contain 1..1024 UTF-8 bytes without NUL");
        }
    }

    internal static void Derive(string password, ReadOnlySpan<byte> salt, int rounds, Span<byte> result)
    {
        if (rounds < 1 || salt.Length > 1024 || result.Length != 32) { throw new ArgumentException("invalid KDF parameters"); }
        Validate(password);
        byte[] key = Encoding.UTF8.GetBytes(password);
        Span<byte> keyBlock = stackalloc byte[64]; keyBlock.Clear();
        Span<byte> inner = stackalloc byte[64 + 1028]; inner.Clear();
        Span<byte> outer = stackalloc byte[96]; outer.Clear();
        Span<byte> u = stackalloc byte[32];
        try
        {
            if (key.Length > 64) { Sha256.Hash(key, keyBlock.Slice(0, 32)); }
            else { key.CopyTo(keyBlock); }
            for (int i = 0; i < 64; i++) { inner[i] = (byte)(keyBlock[i] ^ 0x36); outer[i] = (byte)(keyBlock[i] ^ 0x5c); }
            salt.CopyTo(inner.Slice(64));
            inner.Slice(64 + salt.Length, 4).Clear(); inner[64 + salt.Length + 3] = 1;
            Sha256.Hash(inner.Slice(0, 64 + salt.Length + 4), outer.Slice(64));
            Sha256.Hash(outer, u);
            u.CopyTo(result);
            for (int i = 1; i < rounds; i++)
            {
                u.CopyTo(inner.Slice(64));
                Sha256.Hash(inner.Slice(0, 96), outer.Slice(64));
                Sha256.Hash(outer, u);
                for (int j = 0; j < 32; j++) { result[j] ^= u[j]; }
            }
        }
        finally { key.AsSpan().Clear(); keyBlock.Clear(); inner.Clear(); outer.Clear(); u.Clear(); }
    }
}

/// <summary>FIPS 180-4 SHA-256 compression; tested against independent platform crypto.</summary>
