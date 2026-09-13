using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Cpp2IL.VerifyCore;

namespace Cpp2IL.VerifyPlan;

/// <summary>
/// Planificatorul: a doua dintre cele trei bucati, si singura care nu are nevoie de joc.
///
///   (a) dump-ul din joc scrie indexul metodelor jocului - o sesiune, niciun apel;
///   (b) AICI se citeste indexul acela plus DLL-urile recuperate si se produce lista de lucru: pentru
///       fiecare metoda, daca se poate chema, cu ce argumente exact, si daca nu se poate, de ce anume;
///   (c) harnasul din joc citeste lista de lucru si o executa. Atat.
///
/// Tot ce se hotaraste se hotaraste aici. Harnasul nu construieste indexuri, nu clasifica, nu planifica -
/// ceea ce inseamna ca o sesiune de joc se cheltuie pe apeluri si pe nimic altceva.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("folosire: Cpp2IL.VerifyPlan <directorul Mods> <directorul cu DLL-urile recuperate>");
            Console.Error.WriteLine("          [--seed N] [--no-prepare] [--limit N] [--only fragmente] [--skip fragmente]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Directorul Mods este cel de langa joc: acolo sta indexul scris de mod si tot acolo");
            Console.Error.WriteLine("  se scriu lista de lucru si verdictele date pe disc.");
            return 2;
        }

        var mods = args[0];
        var dlls = args[1];
        var seed = 1UL;
        var prepare = true;
        var limit = 0;
        var only = "";
        var skip = "";

        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seed" when i + 1 < args.Length:
                    ulong.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out seed);
                    break;

                case "--no-prepare":
                    prepare = false;
                    break;

                case "--limit" when i + 1 < args.Length:
                    int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out limit);
                    break;

                case "--only" when i + 1 < args.Length:
                    only = args[++i];
                    break;

                case "--skip" when i + 1 < args.Length:
                    skip = args[++i];
                    break;
            }
        }

        if (!Directory.Exists(mods))
        {
            Console.Error.WriteLine("nu exista directorul: " + mods);
            return 2;
        }

        if (!Directory.Exists(dlls))
        {
            Console.Error.WriteLine("nu exista directorul cu DLL-uri recuperate: " + dlls);
            return 2;
        }

        void Log(string message) => Console.WriteLine(message);

        var game = GameIndex.Read(mods, out var problem);
        if (game == null)
        {
            Console.Error.WriteLine(problem);
            return 3;
        }

        if (problem != null)
            Log("ATENTIE: " + problem);

        Log("Indexul jocului: " + game.Count + " metode, " + game.TypesWithFields + " tipuri cu campuri semanabile.");

        var recovered = new RecoveredContext(dlls);
        var assemblies = new List<string>();
        foreach (var file in Directory.GetFiles(dlls, "*.dll"))
            assemblies.Add(Path.GetFileNameWithoutExtension(file));

        assemblies.Sort(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<Candidate>();
        var blocked = new List<BlockedCall>();

        Log("Clasific " + assemblies.Count + " assembly-uri recuperate...");
        Planner.Classify(recovered, game, assemblies.ToArray(), only, skip, candidates, blocked, Log);
        Log("Candidati: " + candidates.Count + ", blocati pe disc: " + blocked.Count + ".");

        if (limit > 0 && candidates.Count > limit)
        {
            Log("--limit " + limit + ": se pastreaza primii " + limit + " candidati.");
            candidates.RemoveRange(limit, candidates.Count - limit);
        }

        Dictionary<string, PrepareOracle.Outcome> prepared = null;
        if (prepare)
        {
            Log("Cer JIT-ului sa compileze " + candidates.Count + " corpuri recuperate. Asta poate omori procesul;");
            Log("jurnalul este " + PrepareOracle.JournalFile + ", iar o repornire continua de unde a ramas.");
            prepared = PrepareOracle.Run(mods, candidates, Log);
        }
        else
        {
            Log("--no-prepare: nu se cere nicio compilare. Lista de lucru va cuprinde si corpuri pe care JIT-ul le refuza.");
        }

        var planned = new List<PlannedCall>();
        var refusedByJit = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (prepared != null && prepared.TryGetValue(candidate.Key, out var outcome)
                && outcome.Reason != null && outcome.Reason.Length > 0)
            {
                blocked.Add(new BlockedCall
                {
                    Key = candidate.Key,
                    Assembly = candidate.Assembly,
                    Reason = outcome.Reason,
                    Detail = outcome.Detail,
                });

                refusedByJit.TryGetValue(outcome.Reason, out var count);
                refusedByJit[outcome.Reason] = count + 1;
                continue;
            }

            try
            {
                planned.Add(Planner.Plan(candidate, game, seed));
            }
            catch (Exception ex)
            {
                blocked.Add(new BlockedCall
                {
                    Key = candidate.Key,
                    Assembly = candidate.Assembly,
                    Reason = Blocked.NoBody,
                    Detail = "planificarea a aruncat: " + ex.GetType().Name + ": " + ex.Message,
                });
            }
        }

        // Sortate dupa greutate descrescator: intai metodele ale caror argumente sunt cele mai aproape de
        // ceva adevarat. O sesiune de joc care se opreste la jumatate se opreste atunci dupa ce a masurat
        // partea care valoreaza cel mai mult, nu dupa o felie alfabetica.
        planned.Sort((a, b) =>
        {
            var byWeight = b.Weight.CompareTo(a.Weight);
            return byWeight != 0 ? byWeight : string.CompareOrdinal(a.Key, b.Key);
        });

        Write(Path.Combine(mods, PlanFiles.WorklistFile), PlanFiles.WorklistColumns, planned, p => PlanFiles.Row(
            p.Key, p.Assembly, p.Receiver, p.ReceiverSeed, p.Arguments, p.ReturnPlan, p.Quality, PlanFiles.Int(p.Weight)));

        Write(Path.Combine(mods, PlanFiles.BlockedFile), PlanFiles.BlockedColumns, blocked, b => PlanFiles.Row(
            b.Key, b.Assembly, b.Reason, b.Detail));

        Report(mods, planned, blocked, refusedByJit, Log);
        return 0;
    }

    private static void Write<T>(string path, string[] columns, List<T> rows, Func<T, string> format)
    {
        using var writer = new StreamWriter(path, false);
        writer.WriteLine(PlanFiles.Header(columns));

        foreach (var row in rows)
            writer.WriteLine(format(row));
    }

    /// <summary>
    /// Raportul. Numai numaratori pe ce s-a produs chiar acum - niciun "ar fi", niciun "s-ar califica".
    ///
    /// Regula asta este scrisa aici fiindca a fost incalcata de trei ori la rand in proiect, cu un pret de
    /// fiecare data: previziunile writerilor au iesit de zece pana la o suta de ori mai mari decat ce s-a
    /// masurat. Fisierele de mai jos se pot numara cu un grep, deci orice cifra din raport se poate
    /// verifica fara sa mai creada nimeni pe cuvant.
    /// </summary>
    private static void Report(string mods, List<PlannedCall> planned, List<BlockedCall> blocked,
        Dictionary<string, int> refusedByJit, Action<string> log)
    {
        var byQuality = new Dictionary<string, int>(StringComparer.Ordinal);
        var byReturn = new Dictionary<string, int>(StringComparer.Ordinal);
        var seededReceivers = 0;

        foreach (var call in planned)
        {
            Bump(byQuality, call.Quality);
            Bump(byReturn, call.ReturnPlan);

            if (call.Receiver == "seeded")
                seededReceivers++;
        }

        var byReason = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var block in blocked)
            Bump(byReason, block.Reason);

        var lines = new List<string>
        {
            "Lista de lucru: " + planned.Count + " metode.",
            "  din care cu receptor semanat: " + seededReceivers,
            "",
            "Calitatea argumentelor (numarata pe lista scrisa acum):",
        };

        foreach (var pair in Sorted(byQuality))
            lines.Add("  " + pair.Value.ToString(CultureInfo.InvariantCulture).PadLeft(7) + "  " + pair.Key);

        lines.Add("");
        lines.Add("Ce se poate privi dupa apel:");
        foreach (var pair in Sorted(byReturn))
            lines.Add("  " + pair.Value.ToString(CultureInfo.InvariantCulture).PadLeft(7) + "  " + pair.Key);

        lines.Add("");
        lines.Add("Blocate pe disc, fara joc: " + blocked.Count);
        foreach (var pair in Sorted(byReason))
            lines.Add("  " + pair.Value.ToString(CultureInfo.InvariantCulture).PadLeft(7) + "  " + pair.Key);

        if (refusedByJit.Count > 0)
        {
            lines.Add("");
            lines.Add("Refuzate de JIT, pe disc (sesiuni de joc economisite):");
            foreach (var pair in Sorted(refusedByJit))
                lines.Add("  " + pair.Value.ToString(CultureInfo.InvariantCulture).PadLeft(7) + "  " + pair.Key);
        }

        // Tipurile pentru care nu s-a gasit nimic mai bun decat null. Este chiar lista de lucru a
        // urmatoarei imbunatatiri a argumentelor, asa ca se scrie ordonata dupa cat costa fiecare.
        var nulls = Sorted(Planner.NullsByType);
        if (nulls.Count > 0)
        {
            lines.Add("");
            lines.Add("Tipuri care au primit null fiindca nimic nu se poate fabrica identic pe ambele parti (primele 30):");
            for (var i = 0; i < nulls.Count && i < 30; i++)
                lines.Add("  " + nulls[i].Value.ToString(CultureInfo.InvariantCulture).PadLeft(7) + "  " + nulls[i].Key);
        }

        foreach (var line in lines)
            log(line);

        File.WriteAllLines(Path.Combine(mods, "verify-plan-summary.txt"), lines);
    }

    private static void Bump(Dictionary<string, int> counts, string key)
    {
        counts.TryGetValue(key ?? "", out var count);
        counts[key ?? ""] = count + 1;
    }

    private static List<KeyValuePair<string, int>> Sorted(Dictionary<string, int> counts)
    {
        var list = new List<KeyValuePair<string, int>>(counts);
        list.Sort((a, b) =>
        {
            var byCount = b.Value.CompareTo(a.Value);
            return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
        });

        return list;
    }
}
