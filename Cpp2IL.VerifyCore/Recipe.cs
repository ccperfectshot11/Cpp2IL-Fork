using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Cpp2IL.VerifyCore;

/// <summary>
/// Ce fel de valoare este o reteta. Numele sunt scrise ca atare in lista de lucru, deci se pot numara cu
/// un grep fara sa stie nimeni ce inseamna enumul de aici.
/// </summary>
public enum RecipeKind
{
    /// <summary>Nu se poate fabrica nimic - byref, pointer, generic deschis. Nu are ce cauta in lista.</summary>
    Cannot,

    /// <summary>Null pe amandoua partile. Ultima solutie, si se numara ca atare in calitate.</summary>
    Null,

    /// <summary>default(T) pentru o structura cu referinte inauntru: zero pe amandoua partile.</summary>
    Zero,

    /// <summary>Frunze: primitiva, enum sau structura numai-primitive. Aceiasi biti pe amandoua partile.</summary>
    Bits,

    /// <summary>Un sir adevarat. Acelasi obiect System.String pe amandoua partile.</summary>
    Text,

    /// <summary>Un tablou de primitive sau de enum-uri, cu continut.</summary>
    Array,

    /// <summary>Un tablou de siruri, cu continut.</summary>
    TextArray,
}

/// <summary>
/// O valoare descrisa in text, generata O SINGURA DATA pe disc si materializata separat in fiecare dintre
/// cele doua universuri de tipuri.
///
/// Asta este constrangerea care nu se negociaza in toata unealta. Metoda jocului si a noastra traiesc in
/// universuri diferite - Il2Cppquantum.Foo contra quantum.Foo, contexte de incarcare diferite - deci nu se
/// poate da acelasi obiect amandurora. Ce se poate: acelasi SIR DE BITI, turnat de doua ori. O reteta este
/// exact sirul acela de biti plus reteta de turnare, si de aceea trece prin fisier ca text: se genereaza
/// intr-un proces, pe disc, si se citeste in altul, in joc.
///
/// Regula care decurge de aici, si care este mai importanta decat orice castig: daca o valoare nu se poate
/// materializa IDENTIC pe amandoua partile, nu se foloseste deloc. Un argument diferit intre parti produce
/// un "nu se comporta la fel" mincinos, care este mai rau decat o metoda nemasurata - pe cel din urma il
/// vezi in numaratoare, pe cel dintai il crezi.
/// </summary>
public sealed class Recipe
{
    /// <summary>Desparte retetele intre ele in coloana de argumente. 0x1E, adica separatorul de
    /// inregistrare, ales ca pereche a lui 0x1F pe care il foloseste fisierul pentru coloane: niciunul nu
    /// poate aparea intr-un sir, fiindca sirurile trec prin Escape.</summary>
    public const char Separator = '\u001e';

    private Recipe(RecipeKind kind) => Kind = kind;

    public RecipeKind Kind { get; private set; }

    /// <summary>Frunzele, ca biti. Ordinea este ordinea de declarare a campurilor, aceeasi pe amandoua partile.</summary>
    public long[] Leaves { get; private set; }

    /// <summary>Felul fiecarei frunze, cat sa se poata reface valoarea din biti fara tipul tinta.</summary>
    public LeafKind[] Kinds { get; private set; }

    /// <summary>Continutul, pentru Text. Null este o valoare legala aici.</summary>
    public string Text { get; private set; }

    /// <summary>Continutul, pentru TextArray. Null inseamna tablou nul.</summary>
    public string[] Texts { get; private set; }

    public static readonly Recipe OfNull = new Recipe(RecipeKind.Null);
    public static readonly Recipe OfZero = new Recipe(RecipeKind.Zero);
    public static readonly Recipe OfCannot = new Recipe(RecipeKind.Cannot);

    public static Recipe OfBits(LeafKind[] kinds, long[] leaves) =>
        new Recipe(RecipeKind.Bits) { Kinds = kinds, Leaves = leaves };

    public static Recipe OfText(string text) => new Recipe(RecipeKind.Text) { Text = text };

    public static Recipe OfArray(LeafKind kind, long[] leaves) =>
        new Recipe(RecipeKind.Array) { Kinds = new[] { kind }, Leaves = leaves };

    public static Recipe OfTextArray(string[] texts) => new Recipe(RecipeKind.TextArray) { Texts = texts };

    /// <summary>
    /// Cat de mult spune o reteta despre metoda care o primeste.
    ///
    /// Serveste la o singura intrebare, dar la cea mai importanta: "cat valoreaza rezultatul asta". Un
    /// acord obtinut cu null peste tot si un acord obtinut cu siruri si numere adevarate NU se aduna, si
    /// fara nota de mai jos raportul le-ar aduna tacut.
    /// </summary>
    public int Weight => Kind switch
    {
        RecipeKind.Bits => 2,
        RecipeKind.Text => Text == null ? 1 : 2,
        RecipeKind.Array => 2,
        RecipeKind.TextArray => 2,
        RecipeKind.Zero => 1,
        RecipeKind.Null => 0,
        _ => 0,
    };

    // ------------------------------------------------------------------------------------------------
    // Textul
    // ------------------------------------------------------------------------------------------------

    public override string ToString()
    {
        switch (Kind)
        {
            case RecipeKind.Null:
                return "null";

            case RecipeKind.Zero:
                return "zero";

            case RecipeKind.Bits:
            {
                var builder = new StringBuilder("bits:");
                for (var i = 0; i < Leaves.Length; i++)
                {
                    if (i > 0)
                        builder.Append(',');

                    builder.Append(Kinds[i].ToString()).Append('=')
                        .Append(Leaves[i].ToString(CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }

            case RecipeKind.Text:
                return Text == null ? "str:~" : "str:" + Escape(Text);

            case RecipeKind.Array:
            {
                if (Leaves == null)
                    return "arr:" + Kinds[0] + ":~";

                var builder = new StringBuilder("arr:").Append(Kinds[0]).Append(':');
                for (var i = 0; i < Leaves.Length; i++)
                {
                    if (i > 0)
                        builder.Append(',');

                    builder.Append(Leaves[i].ToString(CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }

            case RecipeKind.TextArray:
            {
                if (Texts == null)
                    return "sarr:~";

                var builder = new StringBuilder("sarr:");
                for (var i = 0; i < Texts.Length; i++)
                {
                    if (i > 0)
                        builder.Append(',');

                    builder.Append(Texts[i] == null ? "~" : Escape(Texts[i]));
                }

                return builder.ToString();
            }

            default:
                return "cannot";
        }
    }

    public static Recipe Parse(string text)
    {
        if (string.IsNullOrEmpty(text) || text == "cannot")
            return OfCannot;

        if (text == "null")
            return OfNull;

        if (text == "zero")
            return OfZero;

        if (text.StartsWith("str:", StringComparison.Ordinal))
        {
            var payload = text.Substring(4);
            return OfText(payload == "~" ? null : Unescape(payload));
        }

        if (text.StartsWith("bits:", StringComparison.Ordinal))
        {
            var body = text.Substring(5);
            if (body.Length == 0)
                return OfBits(new LeafKind[0], new long[0]);

            var parts = body.Split(',');
            var kinds = new LeafKind[parts.Length];
            var leaves = new long[parts.Length];

            for (var i = 0; i < parts.Length; i++)
            {
                var at = parts[i].IndexOf('=');
                if (at < 0)
                    return OfCannot;

                if (!TryLeafKind(parts[i].Substring(0, at), out kinds[i]))
                    return OfCannot;

                if (!long.TryParse(parts[i].Substring(at + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out leaves[i]))
                    return OfCannot;
            }

            return OfBits(kinds, leaves);
        }

        if (text.StartsWith("arr:", StringComparison.Ordinal))
        {
            var body = text.Substring(4);
            var at = body.IndexOf(':');
            if (at < 0 || !TryLeafKind(body.Substring(0, at), out var kind))
                return OfCannot;

            var payload = body.Substring(at + 1);
            if (payload == "~")
                return new Recipe(RecipeKind.Array) { Kinds = new[] { kind }, Leaves = null };

            if (payload.Length == 0)
                return OfArray(kind, new long[0]);

            var parts = payload.Split(',');
            var leaves = new long[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                if (!long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out leaves[i]))
                    return OfCannot;

            return OfArray(kind, leaves);
        }

        if (text.StartsWith("sarr:", StringComparison.Ordinal))
        {
            var payload = text.Substring(5);
            if (payload == "~")
                return OfTextArray(null);

            if (payload.Length == 0)
                return OfTextArray(new string[0]);

            var parts = payload.Split(',');
            var texts = new string[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                texts[i] = parts[i] == "~" ? null : Unescape(parts[i]);

            return OfTextArray(texts);
        }

        return OfCannot;
    }

    public static string Join(IList<Recipe> recipes)
    {
        if (recipes == null || recipes.Count == 0)
            return "";

        var builder = new StringBuilder();
        for (var i = 0; i < recipes.Count; i++)
        {
            if (i > 0)
                builder.Append(Separator);

            builder.Append(recipes[i]);
        }

        return builder.ToString();
    }

    public static Recipe[] Split(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new Recipe[0];

        var parts = text.Split(Separator);
        var recipes = new Recipe[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            recipes[i] = Parse(parts[i]);

        return recipes;
    }

    // Virgula si doua puncte despart campurile retetei, deci nu au voie sa apara brute intr-un sir; la fel
    // separatorii de fisier si sfarsitul de rand. Escaparea este pe litere si nu pe base64 dinadins:
    // fisierele astea se citesc cu ochiul si se numara cu grep, iar un sir ilizibil ar strica si una si
    // alta.
    public static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case ',': builder.Append("\\c"); break;
                case ':': builder.Append("\\d"); break;
                case '~': builder.Append("\\t"); break;
                case '\r': builder.Append("\\r"); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\h"); break;
                case Separator: builder.Append("\\s"); break;
                case '\u001f': builder.Append("\\f"); break;
                default: builder.Append(c); break;
            }
        }

        return builder.ToString();
    }

    public static string Unescape(string text)
    {
        if (text.IndexOf('\\') < 0)
            return text;

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                builder.Append(text[i]);
                continue;
            }

            switch (text[++i])
            {
                case '\\': builder.Append('\\'); break;
                case 'c': builder.Append(','); break;
                case 'd': builder.Append(':'); break;
                case 't': builder.Append('~'); break;
                case 'r': builder.Append('\r'); break;
                case 'n': builder.Append('\n'); break;
                case 'h': builder.Append('\t'); break;
                case 's': builder.Append(Separator); break;
                case 'f': builder.Append('\u001f'); break;
                default: builder.Append('\\').Append(text[i]); break;
            }
        }

        return builder.ToString();
    }

    private static bool TryLeafKind(string text, out LeafKind kind)
    {
#if NETSTANDARD2_0
        try
        {
            kind = (LeafKind)Enum.Parse(typeof(LeafKind), text, false);
            return true;
        }
        catch (Exception)
        {
            kind = LeafKind.Int32;
            return false;
        }
#else
        return Enum.TryParse(text, false, out kind);
#endif
    }
}
