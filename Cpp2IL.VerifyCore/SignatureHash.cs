using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cpp2IL.VerifyCore;

// Rolling SHA-256 over every input/output pair a method saw, in order. The digest is the whole point of
// the exercise: two hosts that ran the same method over the same inputs and produced the same digest
// agree BIT FOR BIT, and if they disagree the method behaves differently - which is the failure mode no
// amount of "it compiles" can detect.
//
// Everything is absorbed as raw little-endian bytes, never as text. Formatting a float would fold -0.0
// into "0", collapse the NaN payloads, and round the last digits away, and each of those is a real
// behavioural difference that a fixed-point maths port can turn on.
public sealed class SignatureHash : IDisposable
{
    public const byte TagReturned = 0x00;
    public const byte TagThrew = 0x01;
    public const byte TagNull = 0x02;
    public const byte TagVoid = 0x03;
    public const byte TagInputs = 0x10;
    public const byte TagOutput = 0x11;

    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] _scratch = new byte[8];

    public void AbsorbTag(byte tag)
    {
        _scratch[0] = tag;
        _hash.AppendData(_scratch, 0, 1);
    }

    public void AbsorbText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? "");
        AbsorbInt32(bytes.Length);
        _hash.AppendData(bytes, 0, bytes.Length);
    }

    public void AbsorbInt32(int value)
    {
        _scratch[0] = (byte)value;
        _scratch[1] = (byte)(value >> 8);
        _scratch[2] = (byte)(value >> 16);
        _scratch[3] = (byte)(value >> 24);
        _hash.AppendData(_scratch, 0, 4);
    }

    public void AbsorbUInt64(ulong value)
    {
        for (var i = 0; i < 8; i++)
            _scratch[i] = (byte)(value >> (i * 8));

        _hash.AppendData(_scratch, 0, 8);
    }

    // A second, throwaway hash over the leaves absorbed since the last Restart, used to answer one
    // question the digest cannot: did the OUTPUT ever change? A recovered body that lost its logic and
    // returns the same value whatever it is handed is the archetypal silent failure - it runs, it never
    // throws, and it is wrong - and this is the only way Phase 1 can see it on its own.
    public ulong Running { get; private set; }

    public void RestartRunning() => Running = 0xCBF29CE484222325UL;

    // Every leaf goes in as its exact bit pattern widened to 64 bits, with the kind absorbed alongside it
    // so that a byte 1 and an int 1 can never hash the same.
    public void AbsorbLeaf(LeafKind kind, object value)
    {
        AbsorbTag((byte)kind);
        var bits = Bits.Of(kind, value);
        AbsorbUInt64(bits);
        unchecked
        {
            Running = (Running ^ (byte)kind) * 0x100000001B3UL;
            Running = (Running ^ bits) * 0x100000001B3UL;
        }
    }

    public string ToHex(int bytes)
    {
        var digest = _hash.GetHashAndReset();
        var builder = new StringBuilder(bytes * 2);
        for (var i = 0; i < bytes && i < digest.Length; i++)
            builder.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));

        return builder.ToString();
    }

    public void Dispose() => _hash.Dispose();
}

// Reinterpretation, not conversion. BitConverter.GetBytes is used for the floats because
// SingleToInt32Bits does not exist on netstandard2.0, and going through it keeps NaN payloads and the
// sign of zero intact - a float compared with == would call NaN != NaN and -0.0 == 0.0, which is exactly
// backwards for a differential test.
public static class Bits
{
    public static ulong Of(LeafKind kind, object value)
    {
        switch (kind)
        {
            case LeafKind.Bool: return (bool)value ? 1UL : 0UL;
            case LeafKind.Char: return (char)value;
            case LeafKind.SByte: return unchecked((ulong)(long)(sbyte)value);
            case LeafKind.Byte: return (byte)value;
            case LeafKind.Int16: return unchecked((ulong)(long)(short)value);
            case LeafKind.UInt16: return (ushort)value;
            case LeafKind.Int32: return unchecked((ulong)(long)(int)value);
            case LeafKind.UInt32: return (uint)value;
            case LeafKind.Int64: return unchecked((ulong)(long)value);
            case LeafKind.UInt64: return (ulong)value;
            case LeafKind.Single: return SingleBits((float)value);
            case LeafKind.Double: return unchecked((ulong)BitConverter.DoubleToInt64Bits((double)value));
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    public static uint SingleBits(float value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(bytes);

        return (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
    }

    public static float SingleFromBits(uint bits)
    {
        var bytes = new byte[4];
        bytes[0] = (byte)bits;
        bytes[1] = (byte)(bits >> 8);
        bytes[2] = (byte)(bits >> 16);
        bytes[3] = (byte)(bits >> 24);
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(bytes);

        return BitConverter.ToSingle(bytes, 0);
    }
}
