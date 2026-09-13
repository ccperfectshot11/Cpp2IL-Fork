using System;

namespace Cpp2IL.VerifyCore;

// SplitMix64, spelled out rather than taken from the platform. System.Random is not a specification: its
// algorithm has changed between .NET versions and differs again on Mono, so a Phase 2 host running on
// the game's runtime would generate different "identical" inputs and every method would look like a
// mismatch. Ten lines of unchecked ulong arithmetic are the same everywhere.
public struct DeterministicRandom
{
    private ulong _state;

    public DeterministicRandom(ulong seed) => _state = seed;

    public ulong Next()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    // Seed derivation is part of the contract too: the seed for one method must depend only on the run
    // seed and the method key, never on its position in the list, or adding one method to the selection
    // would change every signature after it.
    public static ulong SeedFor(ulong runSeed, string key)
    {
        unchecked
        {
            var h = 0xCBF29CE484222325UL ^ runSeed;
            foreach (var c in key ?? "")
            {
                h ^= c;
                h *= 0x100000001B3UL;
            }

            return h;
        }
    }
}

// Value generation. Two sources feed a run:
//
//   * an edge sweep, which walks the interesting values of each leaf in a mixed-radix counter, so a
//     two-argument method gets every pair of them rather than a diagonal slice;
//   * a random sweep, whose per-leaf strategy is drawn from the PRNG.
//
// Uniform random bits alone would be close to useless here. A float built from 64 random bits is a NaN
// or a 1e300 nine times out of ten, so a branch on "is this value in the sane range" would never be
// taken and everything past it would go unmeasured. The strategies below deliberately spend most of
// their draws on values a game would actually produce.
public static class FuzzInputs
{
    // Photon FP is Q16.16 in a long: 65536 raw units are 1.0. Fuzzing FP with uniform longs therefore
    // means fuzzing with numbers of magnitude around 10^14, which every trig and normalise path in the
    // library saturates on. Seeding the integer strategies with multiples of Precision is what makes
    // FPMath.Sin actually run its polynomial instead of its overflow guard.
    private const long FixedPointPrecision = 65536;

    private static readonly long[] EdgeIntegers =
    [
        0, 1, -1, 2, -2, 3, 7, 10, -10, 255, 256, -256, 32767, -32768, 65535,
        FixedPointPrecision, -FixedPointPrecision, FixedPointPrecision / 2, FixedPointPrecision * 3,
        int.MaxValue, int.MinValue, long.MaxValue, long.MinValue,
    ];

    private static readonly double[] EdgeDoubles =
    [
        0.0, -0.0, 1.0, -1.0, 0.5, -0.5, 2.0, 3.0, 10.0, 0.1, -0.1,
        double.NaN, double.PositiveInfinity, double.NegativeInfinity,
        double.Epsilon, -double.Epsilon, double.MaxValue, double.MinValue,
        1e-300, 1e300, Math.PI, -Math.PI,
    ];

    private static readonly float[] EdgeSingles =
    [
        0f, -0f, 1f, -1f, 0.5f, -0.5f, 2f, 3f, 10f, 0.1f, -0.1f,
        float.NaN, float.PositiveInfinity, float.NegativeInfinity,
        float.Epsilon, -float.Epsilon, float.MaxValue, float.MinValue,
        1e-40f, 3.4e38f, 3.14159265f, -3.14159265f,
    ];

    private static readonly char[] EdgeChars = [(char)0, (char)65, (char)122, (char)48, (char)255, (char)65535];

    public static int EdgeCount(LeafKind kind) => kind switch
    {
        LeafKind.Bool => 2,
        LeafKind.Char => 6,
        LeafKind.Single => EdgeSingles.Length,
        LeafKind.Double => EdgeDoubles.Length,
        _ => EdgeIntegers.Length,
    };

    public static object EdgeValue(LeafKind kind, int index)
    {
        switch (kind)
        {
            case LeafKind.Bool: return index % 2 != 0;
            case LeafKind.Char: return EdgeChars[index % EdgeChars.Length];
            case LeafKind.Single: return EdgeSingles[index % EdgeSingles.Length];
            case LeafKind.Double: return EdgeDoubles[index % EdgeDoubles.Length];
            default: return Narrow(kind, EdgeIntegers[index % EdgeIntegers.Length]);
        }
    }

    public static object RandomValue(LeafKind kind, ref DeterministicRandom random)
    {
        var strategy = (int)(random.Next() % 8);
        var raw = random.Next();

        switch (kind)
        {
            case LeafKind.Bool:
                return (raw & 1) != 0;

            case LeafKind.Char:
                return (char)raw;

            case LeafKind.Single:
                return (float)RandomReal(strategy, raw, float.MaxValue, true);

            case LeafKind.Double:
                return RandomReal(strategy, raw, double.MaxValue, false);

            default:
                return Narrow(kind, RandomInteger(strategy, raw));
        }
    }

    // Valori BLANDE, pentru locurile unde o valoare urata nu imbogateste masuratoarea ci doar omoara
    // procesul. Doua astfel de locuri exista: campurile unui receptor fabricat si elementele unui tablou
    // fabricat.
    //
    // De ce sunt separate de RandomValue si nu doar "acelasi generator cu alta samanta". Un argument urat
    // este dat unei metode care il primeste pe fata si, in cel mai rau caz, iese pe o ramura de eroare. Un
    // camp de receptor este cu totul altceva: metoda il citeste ca pe starea ei si il crede. Un camp pus
    // pe 2.000.000.000 devine lungimea unei bucle sau indicele unui tablou, iar IL2CPP compilat pentru
    // livrare nu mai are verificari de interval - deci nu iese o exceptie, ci moare procesul, exact cum s-a
    // intamplat cu enum-urile fuzzate pe tot intervalul.
    //
    // De aceea intervalul de aici este mic si marginit dinadins: intregii stau intre -8 si 64 (plus cateva
    // puteri mici ale lui doi), iar numerele cu virgula sunt finite - niciun NaN si nicio infinitate.
    // Pentru Int64 se dau si multipli de 65536, fiindca un long al simularii Photon este virgula fixa
    // Q16.16 si un 7 acolo inseamna 0,0001, adica practic tot zero.
    //
    // Ce se pierde: ramurile care se deschid numai la valori extreme raman neatinse in campuri. Este
    // acelasi schimb constient ca la domeniul enum-urilor - o ramura nemasurata costa mai putin decat o
    // repornire de treizeci de secunde.
    public static object TameValue(LeafKind kind, ref DeterministicRandom random)
    {
        var raw = random.Next();

        switch (kind)
        {
            case LeafKind.Bool:
                return (raw & 1) != 0;

            case LeafKind.Char:
                return TameChars[(int)(raw % (ulong)TameChars.Length)];

            case LeafKind.Single:
                return (float)TameReals[(int)(raw % (ulong)TameReals.Length)];

            case LeafKind.Double:
                return TameReals[(int)(raw % (ulong)TameReals.Length)];

            case LeafKind.Int64:
            case LeafKind.UInt64:
            {
                // Un sfert din trageri sunt in unitati de virgula fixa; restul raman intregi mici, fiindca
                // un long este la fel de des un numarator obisnuit.
                var picked = TameIntegers[(int)((raw >> 8) % (ulong)TameIntegers.Length)];
                return Narrow(kind, TameSign(kind, (raw % 4) == 0 ? picked * FixedPointPrecision : picked));
            }

            default:
                return Narrow(kind, TameSign(kind, TameIntegers[(int)(raw % (ulong)TameIntegers.Length)]));
        }
    }

    /// <summary>
    /// Semnul, pentru felurile FARA semn. Fara pasul asta un -1 bland ar iesi din Narrow ca
    /// 4.294.967.295 pe uint si ca 18.446.744.073.709.551.615 pe ulong - adica exact numaratorul urias
    /// din care se naste o bucla fara sfarsit sau o citire in afara tabloului, tocmai ce incearca
    /// TameValue sa evite. Pe felurile cu semn valoarea trece neatinsa.
    /// </summary>
    private static long TameSign(LeafKind kind, long value)
    {
        if (value >= 0)
            return value;

        switch (kind)
        {
            case LeafKind.Byte:
            case LeafKind.UInt16:
            case LeafKind.UInt32:
            case LeafKind.UInt64:
                return -value;
            default:
                return value;
        }
    }

    private static readonly long[] TameIntegers =
    [
        0, 0, 1, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 16, 20, 24, 32, 48, 64,
        -1, -2, -3, -8, 100, 128, 256, 1000,
    ];

    private static readonly double[] TameReals =
    [
        0.0, 0.0, 1.0, -1.0, 0.5, -0.5, 0.25, 2.0, 3.0, 4.0, 10.0, 100.0,
        0.1, -0.1, 1.5, 3.25, -0.75, 0.001, 60.0, 0.0166015625,
    ];

    private static readonly char[] TameChars =
    [
        'a', 'b', 'c', 'z', 'A', 'B', 'Z', '0', '1', '9', ' ', '_', '-', '.', '/', ':',
    ];

    private static long RandomInteger(int strategy, ulong raw)
    {
        unchecked
        {
            switch (strategy)
            {
                case 0:
                case 1:
                    return (long)raw;                                        // whole range, ugly bit patterns included
                case 2:
                case 3:
                    return (long)(raw % 2049) - 1024;                        // the small integers real code counts and indexes with
                case 4:
                case 5:
                    // Fixed-point flavoured: a whole number of Q16.16 units plus a sub-unit jitter, which
                    // is what an FP that came out of gameplay looks like.
                    return (((long)(raw % 4001) - 2000) * FixedPointPrecision) + (long)((raw >> 32) % FixedPointPrecision);
                case 6:
                    return (long)(1UL << (int)(raw % 63)) + ((long)(raw >> 8) % 3) - 1;   // powers of two and their neighbours
                default:
                    return (raw & 1) == 0 ? long.MaxValue - (long)(raw % 4096) : long.MinValue + (long)(raw % 4096);
            }
        }
    }

    private static double RandomReal(int strategy, ulong raw, double max, bool single)
    {
        // [0,1) out of the top 53 bits, the standard trick: the low bits of a multiplicative generator
        // are the weak ones, and this way they never reach the mantissa.
        var unit = (raw >> 11) * (1.0 / 9007199254740992.0);

        switch (strategy)
        {
            case 0:
                // Straight reinterpretation, so NaN payloads, infinities and denormals get exercised.
                return single ? Bits.SingleFromBits((uint)raw) : BitConverter.Int64BitsToDouble((long)raw);
            case 1:
                return (long)(raw % 2001) - 1000;
            case 2:
                return (unit * 2.0) - 1.0;
            case 3:
                return ((unit * 2.0) - 1.0) * Math.Pow(10.0, (raw % 41) - 20.0);
            case 4:
                return ((long)(raw % 65) - 32) / 4.0;                         // exactly representable quarters
            case 5:
                return max * ((unit * 2.0) - 1.0);
            case 6:
                return (single ? float.Epsilon : double.Epsilon) * (1 + (raw % 1024));   // denormal territory
            default:
                return unit;
        }
    }

    // Truncation, not saturation: wrapping is what a cast in the recovered code does, and a signature is
    // only worth anything if the value fed in is the value the method would really have seen.
    private static object Narrow(LeafKind kind, long value)
    {
        unchecked
        {
            switch (kind)
            {
                case LeafKind.SByte: return (sbyte)value;
                case LeafKind.Byte: return (byte)value;
                case LeafKind.Int16: return (short)value;
                case LeafKind.UInt16: return (ushort)value;
                case LeafKind.Int32: return (int)value;
                case LeafKind.UInt32: return (uint)value;
                case LeafKind.Int64: return value;
                case LeafKind.UInt64: return (ulong)value;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
    }
}
