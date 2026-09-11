using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Cpp2IL.VerifyCheck;

// Ce s-a intamplat cu o metoda cand recensamantul a incercat sa o cheme.
//
// Lista nu este cea a lui MethodFuzzResult si nu are de ce sa fie: acolo intrebarea este "se poate
// COMPARA", aici este "se poate EXECUTA". O metoda care arunca NullReferenceException la fiecare apel
// este un esec pentru comparatie si un succes partial aici - corpul a fost acceptat de JIT, a rulat si
// a luat o decizie. Categoriile sunt asezate in ordinea in care ne intereseaza: ce nu s-a incercat, ce
// a rulat, ce a rulat prost, ce a rupt procesul.
internal enum CensusOutcome
{
    Pending = 0,

    // Nu s-a ajuns niciodata la corp.
    NotAttemptedAnalysisError,      // selectorul a crapat analizand-o, deci nu avem nici macar token
    NotAttemptedLoadFailed,         // DLL-ul nu se incarca deloc, sau este unul detinut de gazda
    NotAttemptedResolveFailed,      // tokenul nu se rezolva in assembly-ul incarcat
    NotAttemptedGeneric,            // generica, si nicio instantiere ghicita nu a fost acceptata
    NotAttemptedStaticCtor,         // .cctor: nu se cheama direct, ruleaza singur la prima atingere a tipului
    NotAttemptedReceiver,           // nu se poate construi un "this" (tip abstract, interfata, ref struct)
    NotAttemptedArguments,          // un parametru pe care nu stim sa-l fabricam nici macar degenerat

    // S-a ajuns la corp.
    Ran,                            // cel putin un apel s-a intors normal
    ThrewManaged,                   // a aruncat o exceptie obisnuita la fiecare incercare
    MissingMember,                  // metadatele recuperate promit un membru care nu exista
    TypeInitFailed,                 // cctor-ul tipului - tot cod recuperat - a explodat
    RuntimeRefusedBody,             // InvalidProgram/BadImageFormat: IL-ul recuperat nu este IL valid

    // Nu s-a intors niciodata.
    Timeout,                        // a depasit bugetul si firul a fost abandonat
    Killed,                         // a omorat procesul; gasita in jurnal la repornire
}

// O metoda din universul recensamantului, redusa la ce trebuie ca sa fie chemata din nou dupa o
// repornire. Deliberat FARA nicio referinta AsmResolver: universul are ~72k de intrari, iar tinerea
// unui MethodDefinition pentru fiecare ar tine vii toate cele 150 de module pe o masina de 16 GB.
internal sealed class CensusTarget
{
    public string Identity;
    public string DllPath;
    public string Assembly;
    public string Type;
    public string Method;
    public uint Token;
    public bool IsStatic;
    public bool WasSelected;        // a trecut si de selectia stricta, cea care alimenteaza faza 2
    public string SelectorReason;   // primul motiv pentru care selectia stricta a refuzat-o
}

internal sealed class CensusRecord
{
    public string Identity;
    public string Assembly;
    public string Type;
    public string Method;
    public string Token;
    public bool IsStatic;
    public bool WasSelected;
    public string SelectorReason;
    public CensusOutcome Outcome;
    public string Detail;               // tipul exceptiei, sau motivul refuzului
    public string Quality;              // "none" | "faithful" | "degenerate"
    public string Degeneracies;         // ce anume am falsificat, separat prin bara verticala
    public bool ThrewOnSome;            // s-a intors pe unele intrari si a aruncat pe altele
    public int Attempts;
    public long ElapsedMs;
    public List<string> ExceptionKinds = new();
}

internal static class CensusOutcomes
{
    // Cand doua incercari dau raspunsuri diferite castiga cel mai informativ. "A rulat" bate orice
    // esec; intre esecuri castiga cel care spune ceva despre CORP, nu despre argumente.
    public static int Rank(CensusOutcome outcome) => outcome switch
    {
        CensusOutcome.Ran => 100,
        CensusOutcome.RuntimeRefusedBody => 90,
        CensusOutcome.MissingMember => 80,
        CensusOutcome.TypeInitFailed => 70,
        CensusOutcome.ThrewManaged => 60,
        CensusOutcome.Timeout => 50,
        CensusOutcome.Killed => 40,
        CensusOutcome.Pending => -1,
        _ => 10,
    };

    public static CensusOutcome Best(CensusOutcome a, CensusOutcome b) => Rank(b) > Rank(a) ? b : a;

    public static bool ReachedTheBody(CensusOutcome outcome) => outcome
        is CensusOutcome.Ran or CensusOutcome.ThrewManaged or CensusOutcome.RuntimeRefusedBody
        or CensusOutcome.TypeInitFailed or CensusOutcome.MissingMember
        or CensusOutcome.Timeout or CensusOutcome.Killed;

    // Ordinea in care categoriile se tiparesc. Fixa, ca doua rulari sa se poata compara linie cu linie
    // fara sa fie sortate dupa numere care se misca de la o rulare la alta.
    public static readonly CensusOutcome[] ReportOrder =
    [
        CensusOutcome.Ran,
        CensusOutcome.ThrewManaged,
        CensusOutcome.RuntimeRefusedBody,
        CensusOutcome.TypeInitFailed,
        CensusOutcome.MissingMember,
        CensusOutcome.Timeout,
        CensusOutcome.Killed,
        CensusOutcome.NotAttemptedGeneric,
        CensusOutcome.NotAttemptedReceiver,
        CensusOutcome.NotAttemptedArguments,
        CensusOutcome.NotAttemptedStaticCtor,
        CensusOutcome.NotAttemptedLoadFailed,
        CensusOutcome.NotAttemptedResolveFailed,
        CensusOutcome.NotAttemptedAnalysisError,
        CensusOutcome.Pending,
    ];

    public static string Label(CensusOutcome outcome) => outcome switch
    {
        CensusOutcome.Ran => "RAN (returned normally)",
        CensusOutcome.ThrewManaged => "threw a managed exception",
        CensusOutcome.RuntimeRefusedBody => "RUNTIME REFUSED THE BODY",
        CensusOutcome.TypeInitFailed => "type initializer failed",
        CensusOutcome.MissingMember => "missing type or member",
        CensusOutcome.Timeout => "timed out (thread abandoned)",
        CensusOutcome.Killed => "KILLED THE PROCESS",
        CensusOutcome.NotAttemptedGeneric => "not attempted: generic",
        CensusOutcome.NotAttemptedReceiver => "not attempted: no receiver",
        CensusOutcome.NotAttemptedArguments => "not attempted: arguments",
        CensusOutcome.NotAttemptedStaticCtor => "not attempted: static ctor",
        CensusOutcome.NotAttemptedLoadFailed => "not attempted: assembly load",
        CensusOutcome.NotAttemptedResolveFailed => "not attempted: token resolve",
        CensusOutcome.NotAttemptedAnalysisError => "not attempted: analysis error",
        _ => "pending",
    };
}

// Formatul de pe disc. Doua fisiere, din acelasi motiv pentru care VerifyCheck are doua: documentul
// JSON final nu se poate adauga la coada, iar o rulare care poate fi omorata in orice clipa trebuie sa
// scrie fiecare rezultat in momentul in care il are. Linia foloseste separatorul U+001F, exact ca
// SignatureJson.WriteResultLine - un caracter pe care niciun mesaj de exceptie nu il contine.
internal static class CensusFile
{
    private const char Sep = '\u001f';

    public static string WriteLine(CensusRecord r)
    {
        var fields = new[]
        {
            r.Identity, r.Assembly, r.Type, r.Method, r.Token,
            r.Outcome.ToString(), r.Detail, r.Quality, r.Degeneracies, r.SelectorReason,
            r.IsStatic ? "1" : "0",
            r.WasSelected ? "1" : "0",
            r.ThrewOnSome ? "1" : "0",
            r.Attempts.ToString(CultureInfo.InvariantCulture),
            r.ElapsedMs.ToString(CultureInfo.InvariantCulture),
            string.Join("|", r.ExceptionKinds ?? new List<string>()),
        };

        var line = new StringBuilder();
        foreach (var field in fields)
        {
            if (line.Length > 0)
                line.Append(Sep);

            line.Append(Clean(field));
        }

        return line.ToString();
    }

    public static CensusRecord ReadLine(string line)
    {
        var fields = line.Split(Sep);
        if (fields.Length < 16 || !Enum.TryParse<CensusOutcome>(fields[5], out var outcome))
            return null;

        return new CensusRecord
        {
            Identity = fields[0], Assembly = fields[1], Type = fields[2], Method = fields[3], Token = fields[4],
            Outcome = outcome, Detail = fields[6], Quality = fields[7], Degeneracies = fields[8], SelectorReason = fields[9],
            IsStatic = fields[10] == "1",
            WasSelected = fields[11] == "1",
            ThrewOnSome = fields[12] == "1",
            Attempts = Int(fields[13]),
            ElapsedMs = Int(fields[14]),
            ExceptionKinds = fields[15].Length == 0 ? new List<string>() : new List<string>(fields[15].Split('|')),
        };
    }

    // Universul, memorat langa rezultate. Fara el fiecare repornire ar reface selectia peste 150 de
    // DLL-uri inainte sa apuce sa cheme ceva, iar recensamantul se asteapta sa reporneasca de zeci de
    // ori - munca pierduta s-ar aduna la mai mult decat rularea insasi.
    public static void WriteUniverse(string path, string dllDir, string filter, List<CensusTarget> targets)
    {
        using var writer = new StreamWriter(path, false);
        writer.WriteLine("#census-universe" + Sep + "1" + Sep + Clean(dllDir) + Sep + Clean(filter ?? "") + Sep + targets.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var t in targets)
            writer.WriteLine(string.Join(Sep.ToString(), Clean(t.Identity), Clean(t.DllPath), Clean(t.Assembly),
                Clean(t.Type), Clean(t.Method), t.Token.ToString(CultureInfo.InvariantCulture),
                t.IsStatic ? "1" : "0", t.WasSelected ? "1" : "0", Clean(t.SelectorReason)));
    }

    public static List<CensusTarget> ReadUniverse(string path, string dllDir, string filter)
    {
        if (!File.Exists(path))
            return null;

        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
            return null;

        var header = lines[0].Split(Sep);

        // Un univers construit peste alt director sau alt filtru nu descrie aceeasi rulare, iar
        // reluarea peste el ar raporta metode care nu au fost niciodata cerute.
        if (header.Length < 5 || header[0] != "#census-universe" || header[1] != "1"
            || !string.Equals(header[2], Clean(dllDir), StringComparison.OrdinalIgnoreCase)
            || header[3] != Clean(filter ?? ""))
            return null;

        var targets = new List<CensusTarget>(lines.Length);
        for (var i = 1; i < lines.Length; i++)
        {
            var f = lines[i].Split(Sep);
            if (f.Length < 9)
                continue;

            targets.Add(new CensusTarget
            {
                Identity = f[0], DllPath = Blank(f[1]), Assembly = f[2], Type = f[3], Method = f[4],
                Token = uint.Parse(f[5], CultureInfo.InvariantCulture),
                IsStatic = f[6] == "1", WasSelected = f[7] == "1", SelectorReason = f[8],
            });
        }

        return targets.Count == 0 ? null : targets;
    }

    public static void WriteJson(TextWriter writer, string dllDir, int universeSize, List<CensusRecord> records)
    {
        writer.WriteLine("{");
        writer.WriteLine("  \"tool\": \"Cpp2IL.VerifyCheck --census\",");
        writer.WriteLine("  \"mode\": \"census\",");
        writer.WriteLine("  \"generatedUtc\": " + Quote(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)) + ",");
        writer.WriteLine("  \"source\": " + Quote(dllDir) + ",");
        writer.WriteLine("  \"universeSize\": " + universeSize.ToString(CultureInfo.InvariantCulture) + ",");
        writer.WriteLine("  \"attempted\": " + records.Count.ToString(CultureInfo.InvariantCulture) + ",");
        writer.WriteLine("  \"methods\": [");

        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var b = new StringBuilder("    {");
            b.Append("\"identity\": ").Append(Quote(r.Identity));
            b.Append(", \"assembly\": ").Append(Quote(r.Assembly));
            b.Append(", \"type\": ").Append(Quote(r.Type));
            b.Append(", \"method\": ").Append(Quote(r.Method));
            b.Append(", \"token\": ").Append(Quote(r.Token));
            b.Append(", \"static\": ").Append(r.IsStatic ? "true" : "false");
            b.Append(", \"outcome\": ").Append(Quote(r.Outcome.ToString()));
            b.Append(", \"detail\": ").Append(Quote(r.Detail));
            b.Append(", \"argumentQuality\": ").Append(Quote(r.Quality));
            b.Append(", \"degeneracies\": ").Append(Quote(r.Degeneracies));
            b.Append(", \"threwOnSomeInputs\": ").Append(r.ThrewOnSome ? "true" : "false");
            b.Append(", \"comparable\": ").Append(r.WasSelected ? "true" : "false");
            b.Append(", \"selectorReason\": ").Append(Quote(r.SelectorReason));
            b.Append(", \"attempts\": ").Append(r.Attempts.ToString(CultureInfo.InvariantCulture));
            b.Append(", \"elapsedMs\": ").Append(r.ElapsedMs.ToString(CultureInfo.InvariantCulture));
            b.Append(", \"exceptionKinds\": [");
            for (var e = 0; e < r.ExceptionKinds.Count; e++)
            {
                if (e > 0)
                    b.Append(", ");

                b.Append(Quote(r.ExceptionKinds[e]));
            }

            b.Append("]}");
            if (i < records.Count - 1)
                b.Append(',');

            writer.WriteLine(b.ToString());
        }

        writer.WriteLine("  ]");
        writer.WriteLine("}");
    }

    private static int Int(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static string Blank(string value) => value.Length == 0 ? null : value;

    // Un mesaj de exceptie poate contine orice, inclusiv linii noi - iar una singura ar rupe linia in
    // doua si ar muta toate campurile de dupa ea in coloana gresita la reluare.
    private static string Clean(string value) =>
        (value ?? "").Replace("\r", " ").Replace("\n", " ").Replace(Sep, ' ').Replace("\t", " ");

    private static string Quote(string value)
    {
        if (value == null)
            return "null";

        var b = new StringBuilder(value.Length + 2);
        b.Append('"');
        foreach (var c in value)
        {
            if (c == '"' || c == '\\')
                b.Append('\\').Append(c);
            else if (c < ' ')
                b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else
                b.Append(c);
        }

        b.Append('"');
        return b.ToString();
    }
}
