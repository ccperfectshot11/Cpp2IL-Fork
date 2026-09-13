using System;
using System.Collections.Generic;
using System.IO;
using Cpp2IL.VerifyCore;

namespace Cpp2IL.VerifyPlan;

/// <summary>
/// Indexul jocului, citit de pe disc.
///
/// Aici este chiar principiul care conduce rescrierea: tot ce se poate afla pe disc se afla pe disc. Indexul
/// se scoate din joc O SINGURA DATA, intr-o sesiune care nu cheama nimic si nu poate cadea, si de atunci se
/// refoloseste. Vechea unealta il recladea la fiecare pornire, din 47.012 de randuri, si platea pretul la
/// fiecare dintre cele unsprezece reporniri care au produs, cu totul, treizeci si sapte de metode masurate.
/// </summary>
internal sealed class GameIndex
{
    public sealed class Entry
    {
        public string Key;
        public string Assembly;
        public string Type;
        public string Method;
        public bool IsStatic;
        public string ReceiverType;
        public string Flags;

        public bool Collided => Flags != null && Flags.IndexOf("collided", StringComparison.Ordinal) >= 0;
    }

    private readonly Dictionary<string, Entry> _byKey = new Dictionary<string, Entry>(StringComparer.Ordinal);

    /// <summary>Tip al jocului -> camp -> felul de frunza al campului. Numai campurile pe care le poate semana harnasul.</summary>
    private readonly Dictionary<string, Dictionary<string, LeafKind>> _fields =
        new Dictionary<string, Dictionary<string, LeafKind>>(StringComparer.Ordinal);

    /// <summary>Campurile de tip string, tinute separat: nu au fel de frunza, dar se pot semana la fel de bine.</summary>
    private readonly Dictionary<string, HashSet<string>> _textFields =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

    public int Count => _byKey.Count;
    public int TypesWithFields => _fields.Count + _textFields.Count;

    public bool TryGet(string key, out Entry entry) => _byKey.TryGetValue(key, out entry);

    public static GameIndex Read(string directory, out string problem)
    {
        problem = null;
        var index = new GameIndex();

        var indexPath = Path.Combine(directory, PlanFiles.GameIndexFile);
        if (!File.Exists(indexPath))
        {
            problem = "lipseste " + PlanFiles.GameIndexFile + " - porneste jocul o data in modul de index.";
            return null;
        }

        var first = true;
        foreach (var line in File.ReadLines(indexPath))
        {
            if (first)
            {
                first = false;

                // Verificarea care salveaza o sesiune de joc. Un index scris cu alta forma de cheie nu se
                // potriveste cu nimic, iar unealta ar raporta "nicio pereche" si ar arata exact ca o
                // unealta stricata.
                if (!PlanFiles.HeaderMatches(line))
                {
                    problem = PlanFiles.GameIndexFile + " este scris cu alta forma (" + Head(line)
                        + " in loc de " + PlanFiles.Schema + ") - trebuie refacut din joc.";
                    return null;
                }

                continue;
            }

            if (line.Length == 0)
                continue;

            var f = PlanFiles.Fields(line);
            if (f.Length <= PlanFiles.GameFlags)
                continue;

            index._byKey[f[PlanFiles.GameKey]] = new Entry
            {
                Key = f[PlanFiles.GameKey],
                Assembly = f[PlanFiles.GameAssembly],
                Type = f[PlanFiles.GameType],
                Method = f[PlanFiles.GameMethod],
                IsStatic = f[PlanFiles.GameStatic] == "1",
                ReceiverType = f[PlanFiles.GameReceiverType],
                Flags = f[PlanFiles.GameFlags],
            };
        }

        var fieldsPath = Path.Combine(directory, PlanFiles.GameFieldsFile);
        if (!File.Exists(fieldsPath))
        {
            // Nu este fatal, dar trebuie spus: fara campurile jocului nu se poate semana niciun receptor,
            // iar semanarea receptorului este singura observatie pe care o au cele 17.555 de metode void.
            problem = "lipseste " + PlanFiles.GameFieldsFile + " - se planifica fara semanarea receptorilor.";
            return index;
        }

        first = true;
        foreach (var line in File.ReadLines(fieldsPath))
        {
            if (first)
            {
                first = false;
                continue;
            }

            if (line.Length == 0)
                continue;

            var f = PlanFiles.Fields(line);
            if (f.Length <= PlanFiles.FieldLeafKind)
                continue;

            var type = f[PlanFiles.FieldType];
            var name = f[PlanFiles.FieldName];
            var kind = f[PlanFiles.FieldLeafKind];

            if (kind == "string")
            {
                if (!index._textFields.TryGetValue(type, out var texts))
                    index._textFields[type] = texts = new HashSet<string>(StringComparer.Ordinal);

                texts.Add(name);
                continue;
            }

            if (!TryLeafKind(kind, out var leaf))
                continue;

            if (!index._fields.TryGetValue(type, out var fields))
                index._fields[type] = fields = new Dictionary<string, LeafKind>(StringComparer.Ordinal);

            fields[name] = leaf;
        }

        return index;
    }

    /// <summary>
    /// Adevarat daca partea JOCULUI are un camp cu acest nume si cu acest fel de frunza.
    ///
    /// Se cere inainte de a planifica semanarea unui camp, si nu din prudenta: un camp semanat numai pe
    /// partea noastra face receptorii diferiti, iar de acolo orice diferenta masurata este o diferenta pe
    /// care am produs-o noi. Mai bine un camp nesemanat decat unul semanat pe jumatate.
    /// </summary>
    public bool HasField(string gameType, string field, LeafKind kind) =>
        _fields.TryGetValue(gameType, out var fields) && fields.TryGetValue(field, out var found) && found == kind;

    public bool HasTextField(string gameType, string field) =>
        _textFields.TryGetValue(gameType, out var fields) && fields.Contains(field);

    private static string Head(string line)
    {
        if (line == null)
            return "fara antet";

        var at = line.IndexOf(PlanFiles.Sep);
        return at > 0 ? line.Substring(0, at) : line;
    }

    private static bool TryLeafKind(string text, out LeafKind kind) => Enum.TryParse(text, false, out kind);
}
