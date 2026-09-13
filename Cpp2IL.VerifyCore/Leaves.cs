using System;

namespace Cpp2IL.VerifyCore;

// SplitMix64, scris pe litere si nu luat de la platforma. System.Random nu este o specificatie:
// algoritmul lui s-a schimbat intre versiunile .NET si difera iar pe Mono. Aici conteaza mai mult decat
// inainte - valorile se genereaza pe DISC, in planificator, si se citesc in JOC, in alt proces si pe alt
// runtime. Zece randuri de aritmetica pe ulong dau acelasi sir peste tot.
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

    // Samanta unei metode atarna NUMAI de samanta rularii si de cheia ei, niciodata de locul ei in lista:
    // altfel adaugarea unei metode ar schimba argumentele tuturor celor de dupa ea, iar doua liste de
    // lucru facute din acelasi univers n-ar mai fi comparabile.
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

// Valorile de frunza: bitii propriu-zisi pe care ii primeste o primitiva, un enum sau o structura
// numai-primitive.
//
// Bitii aleatori uniformi ar fi aproape inutili aici. Un float cladit din 64 de biti aleatori este NaN sau
// 1e300 de noua ori din zece, deci o ramura "valoarea asta este in intervalul cu sens" nu s-ar lua
// niciodata si tot ce sta dincolo de ea ar ramane nemasurat. Strategiile de mai jos isi cheltuie dinadins
// cele mai multe trageri pe valori pe care un joc chiar le produce.
public static class Leaves
{
    // FP-ul Photon este Q16.16 intr-un long: 65536 de unitati brute inseamna 1.0. Fuzzarea unui FP cu
    // long-uri uniforme inseamna deci fuzzare cu numere de ordinul 10^14, pe care fiecare drum de
    // trigonometrie si de normalizare din biblioteca le satureaza. Semintele in multipli de Precision sunt
    // ce face ca FPMath.Sin sa-si ruleze chiar polinomul, nu garda de depasire.
    private const long FixedPointPrecision = 65536;

    public static object Random(LeafKind kind, ref DeterministicRandom random)
    {
        var strategy = (int)(random.Next() % 8);
        var raw = random.Next();

        switch (kind)
        {
            case LeafKind.Bool: return (raw & 1) != 0;
            case LeafKind.Char: return (char)raw;
            case LeafKind.Single: return (float)RandomReal(strategy, raw, float.MaxValue, true);
            case LeafKind.Double: return RandomReal(strategy, raw, double.MaxValue, false);
            default: return Narrow(kind, RandomInteger(strategy, raw));
        }
    }

    private static long RandomInteger(int strategy, ulong raw)
    {
        unchecked
        {
            switch (strategy)
            {
                case 0:
                case 1:
                    // tot intervalul, cu tot cu tipare urate de biti
                    return (long)raw;
                case 2:
                case 3:
                    // intregii mici cu care codul adevarat numara si indexeaza
                    return (long)(raw % 2049) - 1024;
                case 4:
                case 5:
                    // Cu gust de virgula fixa: un numar intreg de unitati Q16.16 plus o zdruncinatura
                    // sub-unitate, adica exact cum arata un FP iesit din joc.
                    return (((long)(raw % 4001) - 2000) * FixedPointPrecision) + (long)((raw >> 32) % FixedPointPrecision);
                case 6:
                    // puteri ale lui doi si vecinii lor
                    return (long)(1UL << (int)(raw % 63)) + ((long)(raw >> 8) % 3) - 1;
                default:
                    return (raw & 1) == 0 ? long.MaxValue - (long)(raw % 4096) : long.MinValue + (long)(raw % 4096);
            }
        }
    }

    private static double RandomReal(int strategy, ulong raw, double max, bool single)
    {
        // [0,1) din cei mai de sus 53 de biti, siretlicul obisnuit: bitii de jos ai unui generator
        // multiplicativ sunt cei slabi, iar asa nu ajung niciodata in mantisa.
        var unit = (raw >> 11) * (1.0 / 9007199254740992.0);

        switch (strategy)
        {
            case 0:
                // Reinterpretare directa, ca sa fie exersate si incarcaturile de NaN, infinitii si denormalele.
                return single ? SingleFromBits((uint)raw) : BitConverter.Int64BitsToDouble((long)raw);
            case 1:
                return (long)(raw % 2001) - 1000;
            case 2:
                return (unit * 2.0) - 1.0;
            case 3:
                return ((unit * 2.0) - 1.0) * Math.Pow(10.0, (raw % 41) - 20.0);
            case 4:
                // sferturi reprezentabile exact
                return ((long)(raw % 65) - 32) / 4.0;
            case 5:
                return max * ((unit * 2.0) - 1.0);
            case 6:
                // taramul denormalelor
                return (single ? float.Epsilon : double.Epsilon) * (1 + (raw % 1024));
            default:
                return unit;
        }
    }

    // Trunchiere, nu saturare: infasurarea este ce face o conversie din codul recuperat, iar o masuratoare
    // valoreaza ceva numai daca valoarea data este valoarea pe care metoda chiar ar fi vazut-o.
    public static object Narrow(LeafKind kind, long value)
    {
        unchecked
        {
            switch (kind)
            {
                case LeafKind.Bool: return (value & 1) != 0;
                case LeafKind.Char: return (char)value;
                case LeafKind.SByte: return (sbyte)value;
                case LeafKind.Byte: return (byte)value;
                case LeafKind.Int16: return (short)value;
                case LeafKind.UInt16: return (ushort)value;
                case LeafKind.Int32: return (int)value;
                case LeafKind.UInt32: return (uint)value;
                case LeafKind.Int64: return value;
                case LeafKind.UInt64: return (ulong)value;
                case LeafKind.Single: return SingleFromBits((uint)value);
                case LeafKind.Double: return BitConverter.Int64BitsToDouble(value);
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
    }

    /// <summary>
    /// Bitii unei frunze ca long, ca sa poata fi scrisi in lista de lucru si cititi inapoi in joc identic.
    ///
    /// Drumul asta este singurul prin care trece o valoare de la planificator la harnas, si de aceea merge
    /// pe BITI si nu pe text. Un float scris ca "0.1" si citit inapoi poate sa nu mai fie acelasi float,
    /// iar "-0" si NaN-urile cu incarcatura se pierd cu totul la formatare - si fiecare dintre ele este o
    /// diferenta de comportare adevarata pe care un port in virgula fixa chiar o poate scoate la iveala.
    /// </summary>
    public static long Bits(LeafKind kind, object value)
    {
        unchecked
        {
            switch (kind)
            {
                case LeafKind.Bool: return (bool)value ? 1 : 0;
                case LeafKind.Char: return (char)value;
                case LeafKind.SByte: return (sbyte)value;
                case LeafKind.Byte: return (byte)value;
                case LeafKind.Int16: return (short)value;
                case LeafKind.UInt16: return (ushort)value;
                case LeafKind.Int32: return (int)value;
                case LeafKind.UInt32: return (uint)value;
                case LeafKind.Int64: return (long)value;
                case LeafKind.UInt64: return (long)(ulong)value;
                case LeafKind.Single: return SingleToBits((float)value);
                case LeafKind.Double: return BitConverter.DoubleToInt64Bits((double)value);
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
    }

    // BitConverter.SingleToInt32Bits nu exista in netstandard2.0, iar blocurile unsafe nu sunt pornite in
    // biblioteca asta. Drumul prin GetBytes da aceiasi biti si merge peste tot.
    public static long SingleToBits(float value) => BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);

    public static float SingleFromBits(uint bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);

    /// <summary>
    /// Siruri de proba.
    ///
    /// Exista fiindca pana acum FIECARE parametru de tip string primea null - masurat pe dump-ul real,
    /// 3.318 parametri din universul chemabil - iar un null dat unei metode care despica un sir da acelasi
    /// NullReferenceException pe ambele parti si nu spune nimic despre corpul ei.
    ///
    /// Un sir se poate da, spre deosebire de aproape orice alta referinta: System.String este imprumutat de
    /// la gazda pe AMANDOUA partile - contextul codului recuperat cere mscorlib de la gazda, iar un invelis
    /// Il2CppInterop primeste tot System.String si il marshaleaza el - deci cele doua metode primesc
    /// LITERALMENTE acelasi obiect, nu doua obiecte despre care speram ca sunt la fel.
    ///
    /// Continutul nu este aleatoriu: sunt sirurile pe care codul unui joc chiar le imparte, le compara si
    /// le numara, plus cele care sparg presupunerile obisnuite - gol, spatii, cifre, cale, separator,
    /// diacritice, pereche surogat.
    /// </summary>
    private static readonly string[] Corpus =
    {
        "",
        " ",
        "0",
        "1",
        "-1",
        "true",
        "false",
        "null",
        "name",
        "Player",
        "player_1",
        "a,b,c",
        "a|b|c",
        "key=value",
        "/path/to/file.txt",
        "en-US",
        "{}",
        "[]",
        "{\"a\":1}",
        "00000000-0000-0000-0000-000000000000",
        "1.5",
        "3.14159265358979",
        "2147483648",
        "  spatii la margini  ",
        "linia unu\nlinia doi",
        "tab\tseparat",
        "diacritice: aaisttAAIST",
        "surogat: 😀",
        "foarte lung: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    };

    public static string RandomText(ref DeterministicRandom random)
    {
        // Un null ramane in domeniu si se trage rar: o metoda care nu-si verifica argumentul TREBUIE sa
        // aiba ocazia sa arate ca nu-l verifica, pe amandoua partile deodata. Rar, fiindca inainte era
        // singura valoare posibila si asta e chiar ce se repara aici.
        var draw = random.Next() % 32;
        return draw == 0 ? null : Corpus[(int)(random.Next() % (ulong)Corpus.Length)];
    }
}
