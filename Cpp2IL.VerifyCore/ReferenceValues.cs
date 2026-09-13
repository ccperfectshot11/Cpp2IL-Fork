using System;

namespace Cpp2IL.VerifyCore;

/// <summary>
/// Continutul valorilor de REFERINTA, in forma lor de gazda: un sir, sau un tablou de primitive.
///
/// De ce statea aici o gaura pana acum. Un parametru de tip clasa primea null pe amandoua partile. Ca
/// test este corect - amandoua implementarile vad exact acelasi lucru - dar este steril, iar masuratoarea
/// o arata negru pe alb: pe randurile etichetate "null-reference" au iesit 91 THREW_BOTH si 67
/// AGREES_WEAK de felul "amandoua au intors null", adica amandoua s-au impiedicat de acelasi null inainte
/// sa apuce sa calculeze ceva. Pe randurile etichetate "generated", unde valorile chiar erau fabricate, au
/// iesit 54 AGREES si 29 DISAGREES - verdicte care spun ceva.
///
/// Ce se fabrica aici este numai CONTINUTUL, o singura data, in tipurile gazdei: un System.String, un
/// byte[], un string[]. Imbracarea lui in fiecare dintre cele doua universuri de tipuri - unde partea
/// jocului poate cere un Il2CppStructArray&lt;byte&gt; in loc de byte[] - se face in ReferenceArguments,
/// dinadins in alt fisier: aici nu se stie nimic despre Il2CppInterop, deci partea asta se poate citi si
/// verifica fara joc.
///
/// Regula de fier ramane aceeasi ca la frunzele primitive: valoarea se naste O SINGURA DATA si se toarna
/// in doua forme. Daca una dintre forme nu se poate turna, NU se foloseste niciuna - un argument diferit
/// intre parti ar produce un "nu se comporta identic" mincinos, care este mai rau decat o metoda
/// nemasurata.
/// </summary>
public static class ReferenceValues
{
    /// <summary>
    /// Cat de lung poate fi un tablou fabricat. Mic dinadins: un tablou lung nu masoara alta ramura decat
    /// unul scurt, dar inmulteste sansa ca o metoda sa il parcurga la nesfarsit intr-un cod nativ fara
    /// verificari de interval.
    /// </summary>
    private static readonly int[] Lengths = [0, 1, 1, 2, 2, 3, 4, 4, 5, 8];

    /// <summary>
    /// Siruri pe care un joc chiar le vede. Corpusul nu este ornamental: un sir aleatoriu de biti ar cadea
    /// pe prima ramura "nu arata a nimic" a oricarui analizor, la fel cum un float din 64 de biti aleatori
    /// este NaN de noua ori din zece. Aici stau chei de dictionar, nume de scena, cifre scrise ca text,
    /// culori, cai si bucati de JSON - adica formele pe care codul chiar le desface.
    ///
    /// Sirul gol este in lista si este trecut de doua ori: este cazul de margine cel mai des gresit -
    /// Substring(0), IndexOf, [0] - si nu costa nimic.
    /// </summary>
    private static readonly string[] Corpus =
    [
        "", "",
        "0", "1", "-1", "42", "3.5", "0.0", "100",
        "true", "false", "null",
        "a", "ab", "abc", "Player", "player_1", "Name", "name",
        "en", "en-US", "ro", "default", "none", "None",
        "Level", "Level_01", "MainMenu", "Lobby", "coin", "Coins",
        "#FF0000", "1|2|3", "a,b,c", "key=value",
        "{}", "[]", "{\"a\":1}", "[1,2]",
        "/", "a/b", "a/b/c", "file.txt", "Assets/Prefabs/Cube.prefab",
        " ", "  ", "\t", "\n",
        "0123456789abcdef",
        "\u00e9\u00e8\u00ea",
        "\u4f60\u597d",
    ];

    /// <summary>Alfabetul sirurilor construite pe loc. Numai caractere pe care orice codificare le duce intacte.</summary>
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-.";

    /// <summary>
    /// Un sir. Trei sferturi din trageri vin din corpus, un sfert se construieste pe loc din alfabet, ca
    /// sa existe si siruri pe care nimeni nu le-a prevazut.
    /// </summary>
    public static string Text(ref DeterministicRandom random)
    {
        var raw = random.Next();

        if ((raw % 4) != 0)
            return Corpus[(int)((raw >> 8) % (ulong)Corpus.Length)];

        var length = (int)((raw >> 8) % 17);
        if (length == 0)
            return "";

        var characters = new char[length];
        for (var i = 0; i < length; i++)
            characters[i] = Alphabet[(int)(random.Next() % (ulong)Alphabet.Length)];

        return new string(characters);
    }

    /// <summary>Lungimea unui tablou fabricat.</summary>
    public static int Length(ref DeterministicRandom random) =>
        Lengths[(int)(random.Next() % (ulong)Lengths.Length)];

    /// <summary>
    /// Un tablou de primitive, in tipul gazdei cerut de <paramref name="kind"/>. Elementele sunt valori
    /// BLANDE si nu fuzzate pe tot intervalul, din acelasi motiv pentru care sunt blande si campurile unui
    /// receptor: elementul unui tablou ajunge cel mai des indice sau numarator in metoda care il
    /// primeste.
    /// </summary>
    public static Array PrimitiveArray(LeafKind kind, int length, ref DeterministicRandom random)
    {
        var array = Array.CreateInstance(HostTypeOf(kind), length);
        for (var i = 0; i < length; i++)
            array.SetValue(FuzzInputs.TameValue(kind, ref random), i);

        return array;
    }

    /// <summary>Un tablou de siruri, cu continut adevarat in fiecare pozitie.</summary>
    public static string[] TextArray(int length, ref DeterministicRandom random)
    {
        var array = new string[length];
        for (var i = 0; i < length; i++)
            array[i] = Text(ref random);

        return array;
    }

    /// <summary>Tipul gazdei din spatele unui fel de frunza. Invers fata de ValueShape.KindOf.</summary>
    public static Type HostTypeOf(LeafKind kind)
    {
        switch (kind)
        {
            case LeafKind.Bool: return typeof(bool);
            case LeafKind.Char: return typeof(char);
            case LeafKind.SByte: return typeof(sbyte);
            case LeafKind.Byte: return typeof(byte);
            case LeafKind.Int16: return typeof(short);
            case LeafKind.UInt16: return typeof(ushort);
            case LeafKind.Int32: return typeof(int);
            case LeafKind.UInt32: return typeof(uint);
            case LeafKind.Int64: return typeof(long);
            case LeafKind.UInt64: return typeof(ulong);
            case LeafKind.Single: return typeof(float);
            case LeafKind.Double: return typeof(double);
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>
    /// Felul de frunza al unui tip gazda, sau null daca nu este primitiva cunoscuta. Exista aici, si nu se
    /// imprumuta din ValueShape, fiindca ValueShape.KindOf este privat si fiindca aici intrebarea este
    /// alta: nu "ce forma are tipul asta", ci "pot umple un tablou de tipul asta".
    /// </summary>
    public static LeafKind? KindOfElement(Type type)
    {
        if (type == typeof(bool)) return LeafKind.Bool;
        if (type == typeof(char)) return LeafKind.Char;
        if (type == typeof(sbyte)) return LeafKind.SByte;
        if (type == typeof(byte)) return LeafKind.Byte;
        if (type == typeof(short)) return LeafKind.Int16;
        if (type == typeof(ushort)) return LeafKind.UInt16;
        if (type == typeof(int)) return LeafKind.Int32;
        if (type == typeof(uint)) return LeafKind.UInt32;
        if (type == typeof(long)) return LeafKind.Int64;
        if (type == typeof(ulong)) return LeafKind.UInt64;
        if (type == typeof(float)) return LeafKind.Single;
        if (type == typeof(double)) return LeafKind.Double;
        return null;
    }
}
