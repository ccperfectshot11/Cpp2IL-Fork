using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Cpp2IL.VerifyCore;

namespace Cpp2IL.VerifyCheck;

// RECENSAMANT: cate dintre metodele recuperate se pot executa, indiferent daca se pot compara.
//
// Restul acestei unelte raspunde la "se poarta codul recuperat ca originalul", iar ca sa poata raspunde
// trebuie sa construiasca aceleasi argumente in DOUA procese. Conditia este corecta si costa: numai
// 1.545 din cele ~72.200 de metode cu corp o indeplinesc, adica aproximativ 2%. Despre celelalte 98% nu
// stim nimic - nu daca ruleaza, nu daca arunca, nu daca omoara procesul.
//
// Recensamantul renunta la simetrie fiindca nu are ce compara. Un argument clasa poate fi null, un
// string poate fi gol, un array poate avea lungime zero, un "this" poate fi un obiect alocat fara
// constructor. Niciuna dintre aceste valori nu ar fi acceptabila pentru o comparatie; toate sunt
// acceptabile pentru intrebarea "a acceptat runtime-ul corpul si a rulat el pana la capat".
//
// Doua reguli tin masuratoarea cinstita:
//   * fiecare falsificare este numita in rezultat, iar raportul desparte "a rulat cu argumente reale"
//     de "a rulat cu argumente degenerate" - al doilea este o dovada mult mai slaba decat pare;
//   * nimic de aici nu atinge selectia stricta. Selector.Select este chemat exact cum il cheama si
//     rularea de comparatie, si REZULTATUL lui este citit, nu modificat: recensamantul foloseste
//     lista "All" (tot ce are corp) acolo unde comparatia foloseste "Selected".
//
// Fisierele au alt nume decat cele ale comparatiei, deci si .partial, .inflight si .skip sunt separate.
// Asta nu este cosmetic: daca cele doua rulari ar imparti lista de sarite, metodele care omoara
// recensamantul ar disparea tacut din masuratoarea de comportament.
internal static class Census
{
    private static volatile string _phase = "idle";
    private static string _journalPath;

    public static int Run(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--census-report")
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: Cpp2IL.VerifyCheck --census-report <verifycheck-census.json.partial>");
                return 2;
            }

            return ReportFromFile(args[1]);
        }

        var rest = args.Length >= 1 && args[0] == "--census" ? args.Skip(1).ToArray() : args;
        var positional = rest.TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (positional.Length == 0)
        {
            Usage();
            return 2;
        }

        var dllDir = positional[0];
        var filter = positional.Length > 1 ? positional[1] : null;

        if (!Directory.Exists(dllDir))
        {
            Console.Error.WriteLine("no such directory: " + dllDir);
            return 2;
        }

        // Absolut de aici incolo, din acelasi motiv ca in Program.Main: AssemblyLoadContext refuza o
        // cale relativa, si o refuza pentru fiecare metoda in parte.
        dllDir = Path.GetFullPath(dllDir);

        var outPath = Option(rest, "--out") ?? "verifycheck-census.json";
        var max = Int(Option(rest, "--max"), int.MaxValue);
        var sample = Int(Option(rest, "--sample"), 0);
        var attempts = Math.Max(1, Int(Option(rest, "--attempts"), 3));
        var timeout = TimeSpan.FromSeconds(double.Parse(Option(rest, "--timeout") ?? "5", CultureInfo.InvariantCulture));
        var maxHung = Math.Max(1, Int(Option(rest, "--max-hung"), 4));
        var seed = ulong.Parse(Option(rest, "--seed") ?? "20260910", CultureInfo.InvariantCulture);
        var allowGenerics = !rest.Contains("--no-generics");
        var allowDegenerate = !rest.Contains("--no-degenerate");
        var refresh = rest.Contains("--refresh-universe");

        var watch = Stopwatch.StartNew();
        var universePath = outPath + ".universe";
        var targets = refresh ? null : CensusFile.ReadUniverse(universePath, dllDir, filter);
        if (targets == null)
        {
            targets = BuildUniverse(dllDir, filter);
            CensusFile.WriteUniverse(universePath, dllDir, filter, targets);

            // Selectia tine vii toate cele 150 de module AsmResolver. Universul nu are nicio referinta
            // catre ele, deci imediat ce cadrul lui BuildUniverse s-a inchis memoria se poate elibera -
            // si pe o masina de 16 GB merita ceruta, nu asteptata.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Console.Error.WriteLine($"[universe] {targets.Count:N0} methods with a body, cached in {Path.GetFileName(universePath)} ({watch.Elapsed.TotalSeconds:F0}s)");
        }
        else
        {
            Console.Error.WriteLine($"[universe] {targets.Count:N0} methods reloaded from {Path.GetFileName(universePath)} (selection skipped)");
        }

        var todo = Narrow(targets, max, sample);
        Console.Error.WriteLine($"[census] attempting {todo.Count:N0} of {targets.Count:N0}   attempts={attempts} timeout={timeout.TotalSeconds:F0}s generics={(allowGenerics ? "on" : "off")} degenerate={(allowDegenerate ? "on" : "off")}");

        var records = Sweep(dllDir, todo, outPath, attempts, timeout, maxHung, seed, allowGenerics, allowDegenerate);

        using (var writer = new StreamWriter(outPath, false))
            CensusFile.WriteJson(writer, dllDir, targets.Count, records);

        Report(records, targets.Count, outPath, watch.Elapsed);
        return 0;
    }

    private static void Usage()
    {
        Console.Error.WriteLine("usage: Cpp2IL.VerifyCheck --census <dllDir> [dllNameSubstring] [options]");
        Console.Error.WriteLine("  --out FILE          census json, default verifycheck-census.json");
        Console.Error.WriteLine("  --max N             attempt only the first N methods");
        Console.Error.WriteLine("  --sample N          attempt N methods spread evenly over the whole universe");
        Console.Error.WriteLine("  --attempts N        invocations per method, default 3 (first all-zero, rest fuzzed)");
        Console.Error.WriteLine("  --timeout S         seconds one method may run before its thread is abandoned, default 5");
        Console.Error.WriteLine("  --max-hung N        abandoned threads tolerated before the process restarts itself, default 4");
        Console.Error.WriteLine("  --seed N            argument seed, default 20260910");
        Console.Error.WriteLine("  --no-generics       do not guess generic instantiations");
        Console.Error.WriteLine("  --no-degenerate     only attempt methods whose arguments can be built faithfully");
        Console.Error.WriteLine("  --refresh-universe  rebuild the cached selection instead of reloading it");
        Console.Error.WriteLine("  Cpp2IL.VerifyCheck --census-report <file.partial>   report on a run in progress and stop");
    }

    // Universul: FIECARE metoda cu corp, nu doar cele fuzzabile. Selector.Select este chemat neschimbat
    // si numai citit - "All" contine cate un Candidate pentru fiecare metoda cu corp, cu motivul pentru
    // care selectia stricta a refuzat-o, iar motivul acela este exact incrucisarea pe care o vrem in
    // raport: din metodele respinse ca "decompiler marker", cate ruleaza totusi?
    private static List<CensusTarget> BuildUniverse(string dllDir, string filter)
    {
        var selection = Selector.Select(dllDir, filter, allowStatics: true);
        var selected = new HashSet<string>(selection.Selected.Select(c => c.Key), StringComparer.Ordinal);
        var targets = new List<CensusTarget>(selection.All.Count);

        foreach (var candidate in selection.All)
        {
            targets.Add(new CensusTarget
            {
                Identity = candidate.AssemblyName + "::" + candidate.TypeName + "::" + candidate.MethodName
                           + " (0x" + candidate.Token.ToString("X8") + ")",
                DllPath = candidate.DllPath,
                Assembly = candidate.AssemblyName ?? "",
                Type = candidate.TypeName ?? "",
                Method = candidate.MethodName ?? "",
                Token = candidate.Token,
                IsStatic = candidate.IsStatic,
                WasSelected = selected.Contains(candidate.Key),
                SelectorReason = string.IsNullOrEmpty(candidate.Reason) ? "(selected)" : candidate.Reason,
            });
        }

        return targets;
    }

    // O mostra ASEZATA UNIFORM, nu primele N. Primele N sunt primele DLL-uri in ordine alfabetica, deci
    // o rulare de proba peste ele masoara Assembly-CSharp doar daca are noroc la litera. Pasul este
    // calculat din indici, deci aceeasi mostra iese la fiecare repornire si reluarea functioneaza.
    private static List<CensusTarget> Narrow(List<CensusTarget> targets, int max, int sample)
    {
        if (sample > 0 && sample < targets.Count)
        {
            var picked = new List<CensusTarget>(sample);
            for (var i = 0; i < sample; i++)
                picked.Add(targets[(int)((long)i * targets.Count / sample)]);

            return picked;
        }

        return max < targets.Count ? targets.GetRange(0, max) : targets;
    }

    private static List<CensusRecord> Sweep(string dllDir, List<CensusTarget> todo, string outPath, int attempts, TimeSpan timeout, int maxHung, ulong seed, bool allowGenerics, bool allowDegenerate)
    {
        var records = new List<CensusRecord>(todo.Count);
        var context = new RecoveredAssemblyContext(dllDir);

        // Concurente, nu obisnuite: un fir abandonat pe timeout ramane viu si poate inca sa scrie in
        // ele in timp ce firul nou citeste. Un Dictionary scris din doua fire se poate strica tacut si
        // atunci restul recensamantului ar raporta esecuri care nu sunt ale codului recuperat.
        var assemblies = new ConcurrentDictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        var loadFailures = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var partialPath = outPath + ".partial";
        var skipPath = outPath + ".skip";
        _journalPath = outPath + ".inflight";

        var completed = LoadCompleted(partialPath);
        var skip = LoadSkip(skipPath);

        // Ce a ramas in jurnal de la rularea precedenta este exact metoda care a omorat-o. Un access
        // violation nu se poate prinde si un stack overflow este fatal prin proiectare, deci singurul
        // loc unde numele mai poate exista dupa moarte este discul, scris INAINTE de apel.
        if (File.Exists(_journalPath))
        {
            var crashed = File.ReadAllText(_journalPath).Trim();
            if (crashed.Length > 0 && !skip.ContainsKey(crashed))
            {
                skip[crashed] = "killed";
                AppendSkip(skipPath, "killed", crashed);
                Console.Error.WriteLine("!! previous run died inside " + crashed + " - recorded as KILLED and skipped from now on");
            }

            File.Delete(_journalPath);
        }

        var invoker = new Invoker();
        var hung = 0;
        var done = 0;
        var tally = new Dictionary<CensusOutcome, int>();

        foreach (var target in todo)
        {
            done++;
            if (completed.TryGetValue(target.Identity, out var already))
            {
                records.Add(already);
                Count(tally, already.Outcome);
                continue;
            }

            if (skip.TryGetValue(target.Identity, out var reason))
            {
                // Scrisa in rezultate, nu doar sarita. Intr-un recensamant o metoda lipsa este o
                // minciuna: "nu apare in raport" si "nu a fost incercata" arata identic, iar cea care
                // a omorat procesul este tocmai cea despre care trebuie sa se stie.
                var killed = NewRecord(target);
                killed.Outcome = reason == "timeout" ? CensusOutcome.Timeout : CensusOutcome.Killed;
                killed.Detail = reason == "timeout" ? "abandoned after the per-method timeout" : "took the process down";
                killed.Quality = "none";
                Finish(records, tally, partialPath, killed);
                continue;
            }

            if (done % 1000 == 0)
                Console.Error.WriteLine($"[{done:N0}/{todo.Count:N0}] {Summarise(tally)}  ...{Shorten(target.Type, 44)}::{target.Method}");

            if (target.DllPath == null)
            {
                var broken = NewRecord(target);
                broken.Outcome = CensusOutcome.NotAttemptedAnalysisError;
                broken.Detail = target.SelectorReason;
                broken.Quality = "none";
                Finish(records, tally, partialPath, broken);
                continue;
            }

            if (RecoveredAssemblyContext.IsHostOwned(target.Assembly))
            {
                var hosted = NewRecord(target);
                hosted.Outcome = CensusOutcome.NotAttemptedLoadFailed;
                hosted.Detail = "recovered core library: cannot be hosted next to the runtime's own";
                hosted.Quality = "none";
                Finish(records, tally, partialPath, hosted);
                continue;
            }

            _phase = "load";
            File.WriteAllText(_journalPath, target.Identity);

            var captured = target;
            var ok = invoker.TryRun(() => Attempt(captured, context, assemblies, loadFailures, attempts, seed, allowGenerics, allowDegenerate), timeout, out var entry, out var error);

            if (!ok)
            {
                var phase = _phase;
                hung++;
                var stuck = NewRecord(target);
                stuck.Outcome = CensusOutcome.Timeout;
                stuck.Detail = "no return after " + timeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s in phase " + phase;
                stuck.Quality = "none";
                stuck.ElapsedMs = (long)timeout.TotalMilliseconds;
                AppendSkip(skipPath, "timeout", target.Identity);
                Finish(records, tally, partialPath, stuck);

                // .NET nu are cum sa opreasca un fir care nu vrea sa se opreasca: Thread.Abort arunca
                // PlatformNotSupported pe core. Firul ramane deci in viata, de fundal, arzand un nucleu,
                // iar rularea continua pe unul nou - asta evita o repornire completa pentru fiecare
                // bucla infinita. Numai pana la un punct: peste plafon procesul se reporneste, fiindca
                // patru fire care se invart nu mai lasa masuratoarea sa insemne ceva.
                invoker = new Invoker();
                File.Delete(_journalPath);

                if (phase != "invoke" || hung >= maxHung)
                {
                    // Un fir inghetat in faza de incarcare tine lacul contextului de assembly-uri, deci
                    // tot ce urmeaza ar astepta dupa el si ar expira pe rand. Repornirea este singura
                    // iesire, si lista de sarite garanteaza ca repornirea trece de el.
                    Console.Error.WriteLine($"!! {hung} hung method(s), last in phase {phase} - restarting the process so the run keeps making progress");
                    Console.Error.WriteLine("   re-run the same command: everything done so far is in " + Path.GetFileName(partialPath));
                    Environment.Exit(5);
                }

                continue;
            }

            if (entry == null)
            {
                var faulted = NewRecord(target);
                faulted.Outcome = CensusOutcome.NotAttemptedResolveFailed;
                faulted.Detail = error == null ? "harness returned nothing" : error.GetType().Name + ": " + Shorten(error.Message, 160);
                faulted.Quality = "none";
                Finish(records, tally, partialPath, faulted);
                continue;
            }

            Finish(records, tally, partialPath, entry);
        }

        if (File.Exists(_journalPath))
            File.Delete(_journalPath);

        return records;
    }

    // Tot ce se intampla unei metode se intampla PE FIRUL DE LUCRU: incarcarea, rezolvarea tokenului,
    // planul si apelurile. Daca oricare dintre ele se blocheaza, un singur cronometru le acopera pe
    // toate - iar faza spusa de _phase decide daca blocajul mai permite continuarea sau nu.
    private static CensusRecord Attempt(CensusTarget target, RecoveredAssemblyContext context, ConcurrentDictionary<string, Assembly> assemblies, ConcurrentDictionary<string, string> loadFailures, int attempts, ulong seed, bool allowGenerics, bool allowDegenerate)
    {
        var entry = NewRecord(target);
        var watch = Stopwatch.StartNew();

        _phase = "load";
        if (loadFailures.TryGetValue(target.DllPath, out var known))
        {
            entry.Outcome = CensusOutcome.NotAttemptedLoadFailed;
            entry.Detail = known;
            entry.Quality = "none";
            return entry;
        }

        if (!assemblies.TryGetValue(target.DllPath, out var assembly))
        {
            try
            {
                assemblies[target.DllPath] = assembly = context.LoadFromAssemblyPath(target.DllPath);
            }
            catch (Exception ex)
            {
                // Memorat pe DLL. Fara asta un assembly care nu se incarca ar fi reincercat o data
                // pentru fiecare din cele cateva mii de metode ale lui.
                var detail = ex.GetType().Name + ": " + Shorten(ex.Message, 160);
                loadFailures[target.DllPath] = detail;
                entry.Outcome = CensusOutcome.NotAttemptedLoadFailed;
                entry.Detail = detail;
                entry.Quality = "none";
                return entry;
            }
        }

        _phase = "resolve";
        MethodBase method;
        try
        {
            // Dupa token, nu dupa nume si semnatura: build-ul recuperat este plin de supraincarcari pe
            // care reflectia le tipareste identic, si aleasa gresit s-ar masura alta metoda.
            method = assembly.ManifestModule.ResolveMethod((int)target.Token);
        }
        catch (Exception ex)
        {
            entry.Outcome = CensusOutcome.NotAttemptedResolveFailed;
            entry.Detail = ex.GetType().Name + ": " + Shorten(ex.Message, 160);
            entry.Quality = "none";
            return entry;
        }

        if (method == null)
        {
            entry.Outcome = CensusOutcome.NotAttemptedResolveFailed;
            entry.Detail = "token did not resolve";
            entry.Quality = "none";
            return entry;
        }

        _phase = "plan";
        CensusPlan plan;
        try
        {
            plan = CensusArguments.Plan(method, allowGenerics, allowDegenerate);
        }
        catch (Exception ex)
        {
            // Planul atinge metadatele recuperate (tipuri de parametri, campuri de structuri), si acolo
            // orice este posibil - inclusiv un TypeLoadException in mijlocul lui GetParameters.
            entry.Outcome = CensusOutcome.NotAttemptedArguments;
            entry.Detail = "planning threw " + ex.GetType().Name + ": " + Shorten(ex.Message, 140);
            entry.Quality = "none";
            return entry;
        }

        entry.Quality = plan.Quality;
        entry.Degeneracies = string.Join("|", plan.Degeneracies);
        if (!plan.Ok)
        {
            entry.Outcome = plan.Refusal;
            entry.Detail = plan.RefusalDetail;
            return entry;
        }

        _phase = "invoke";
        var random = new DeterministicRandom(DeterministicRandom.SeedFor(seed, target.Identity));
        var best = CensusOutcome.Pending;
        var returned = false;
        var threw = false;

        for (var a = 0; a < attempts; a++)
        {
            // Prima incercare cu zerouri peste tot: este intrarea cu cele mai mari sanse sa treaca de
            // garzile unei metode. Urmatoarele cu valori din acelasi generator ca al comparatiei, ca un
            // corp care iese devreme pe zero sa apuce totusi sa ruleze.
            if (!CensusArguments.TryBuild(plan, ref random, a > 0, out var receiver, out var args, out var why))
            {
                entry.Outcome = CensusOutcome.NotAttemptedArguments;
                entry.Detail = why;
                return entry;
            }

            entry.Attempts++;
            CensusOutcome outcome;
            try
            {
                plan.Method.Invoke(receiver, args);
                outcome = CensusOutcome.Ran;
                returned = true;
            }
            catch (Exception ex)
            {
                var fromBody = ex is TargetInvocationException;
                var inner = fromBody && ex.InnerException != null ? ex.InnerException : ex;
                var kind = inner.GetType().FullName;
                if (entry.ExceptionKinds.Count < 6 && !entry.ExceptionKinds.Contains(kind))
                    entry.ExceptionKinds.Add(kind);

                outcome = Classify(inner, fromBody);
                if (outcome != CensusOutcome.NotAttemptedArguments)
                    threw = true;

                if (string.IsNullOrEmpty(entry.Detail))
                    entry.Detail = kind + ": " + Shorten(inner.Message, 140);
            }

            best = CensusOutcomes.Best(best, outcome);

            // Un refuz al JIT-ului sau un membru lipsa sunt proprietati ale CORPULUI, nu ale intrarii:
            // alte argumente dau exact acelasi raspuns si costa cate o exceptie fiecare.
            if (outcome is CensusOutcome.RuntimeRefusedBody or CensusOutcome.MissingMember
                or CensusOutcome.TypeInitFailed or CensusOutcome.NotAttemptedArguments)
                break;
        }

        entry.Outcome = best;
        entry.ThrewOnSome = returned && threw;
        entry.ElapsedMs = watch.ElapsedMilliseconds;
        _phase = "idle";
        return entry;
    }

    private static CensusOutcome Classify(Exception inner, bool fromBody)
    {
        switch (inner.GetType().FullName)
        {
            case "System.InvalidProgramException":
            case "System.BadImageFormatException":
            case "System.Security.VerificationException":
                // Cea mai actionabila categorie din tot recensamantul: nu inseamna ca metoda s-a purtat
                // urat, inseamna ca ce a scris Cpp2IL nu este IL valid si nu va rula niciodata.
                return CensusOutcome.RuntimeRefusedBody;

            case "System.TypeInitializationException":
                return inner.InnerException is InvalidProgramException or BadImageFormatException
                    ? CensusOutcome.RuntimeRefusedBody
                    : CensusOutcome.TypeInitFailed;

            case "System.MissingMethodException":
            case "System.MissingFieldException":
            case "System.MissingMemberException":
            case "System.TypeLoadException":
            case "System.EntryPointNotFoundException":
            case "System.DllNotFoundException":
                return CensusOutcome.MissingMember;
        }

        // Fara TargetInvocationException in jurul ei, exceptia nu vine din corp: reflectia a refuzat
        // apelul la legarea argumentelor, si asta este o limita a harnesului, nu un defect al codului
        // recuperat. Numarata separat, altfel ar umfla categoria "a aruncat".
        if (!fromBody && inner is ArgumentException or TargetException or NotSupportedException
            or InvalidOperationException or MemberAccessException)
            return CensusOutcome.NotAttemptedArguments;

        return CensusOutcome.ThrewManaged;
    }

    private static CensusRecord NewRecord(CensusTarget target) => new()
    {
        Identity = target.Identity,
        Assembly = target.Assembly,
        Type = target.Type,
        Method = target.Method,
        Token = "0x" + target.Token.ToString("X8"),
        IsStatic = target.IsStatic,
        WasSelected = target.WasSelected,
        SelectorReason = target.SelectorReason,
        Quality = "none",
        Degeneracies = "",
    };

    private static void Finish(List<CensusRecord> records, Dictionary<CensusOutcome, int> tally, string partialPath, CensusRecord entry)
    {
        records.Add(entry);
        Count(tally, entry.Outcome);
        Append(partialPath, entry);
    }

    private static void Count(Dictionary<CensusOutcome, int> tally, CensusOutcome outcome) =>
        tally[outcome] = tally.GetValueOrDefault(outcome) + 1;

    private static string Summarise(Dictionary<CensusOutcome, int> tally) =>
        $"ran {tally.GetValueOrDefault(CensusOutcome.Ran):N0}  threw {tally.GetValueOrDefault(CensusOutcome.ThrewManaged):N0}  invalid-il {tally.GetValueOrDefault(CensusOutcome.RuntimeRefusedBody):N0}  refused {tally.GetValueOrDefault(CensusOutcome.NotAttemptedArguments) + tally.GetValueOrDefault(CensusOutcome.NotAttemptedReceiver) + tally.GetValueOrDefault(CensusOutcome.NotAttemptedGeneric):N0}";

    // Un rezultat pe linie, scris in clipa in care exista. Peste 72.000 de metode si zeci de reporniri
    // asta este singura diferenta intre o rulare care se termina si una care o ia de la capat la
    // nesfarsit - exact lectia pe care rularea de comparatie a invatat-o cu 15 reporniri pe 1.383.
    private static void Append(string path, CensusRecord entry)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(CensusFile.WriteLine(entry));
    }

    private static Dictionary<string, CensusRecord> LoadCompleted(string path)
    {
        var completed = new Dictionary<string, CensusRecord>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return completed;

        foreach (var line in File.ReadLines(path))
        {
            // O linie taiata in doua de moartea procesului nu se poate relua; metoda se cheama din nou,
            // adica exact cat ar fi costat sa nu existe deloc fisierul pentru intrarea aceea.
            if (CensusFile.ReadLine(line) is { } entry && !string.IsNullOrEmpty(entry.Identity))
                completed[entry.Identity] = entry;
        }

        Console.Error.WriteLine($"[resume] {completed.Count:N0} methods already classified");
        return completed;
    }

    private static Dictionary<string, string> LoadSkip(string path)
    {
        var skip = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return skip;

        foreach (var line in File.ReadAllLines(path))
        {
            var tab = line.IndexOf('\t');
            if (tab > 0)
                skip[line.Substring(tab + 1)] = line.Substring(0, tab);
        }

        return skip;
    }

    private static void AppendSkip(string path, string reason, string identity) =>
        File.AppendAllText(path, reason + "\t" + identity + Environment.NewLine);

    private static int ReportFromFile(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("no such file: " + path);
            return 2;
        }

        var records = new List<CensusRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
            if (CensusFile.ReadLine(line) is { } entry && seen.Add(entry.Identity))
                records.Add(entry);

        if (records.Count == 0)
        {
            Console.Error.WriteLine("no census records in " + path + " (is it the .partial file?)");
            return 1;
        }

        // Dimensiunea universului sta in fisierul de langa. Fara ea procentele s-ar raporta la mostra
        // si o rulare de 2.000 de metode ar arata ca o acoperire de 100%, adica exact minciuna pe care
        // acest mod a fost facut sa o desfiinteze.
        var universeSize = records.Count;
        var universePath = path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
            ? path.Substring(0, path.Length - ".partial".Length) + ".universe"
            : path + ".universe";

        if (File.Exists(universePath))
            universeSize = Math.Max(universeSize, File.ReadLines(universePath).Count() - 1);

        Report(records, universeSize, path, TimeSpan.Zero);
        return 0;
    }

    private static void Report(List<CensusRecord> records, int universeSize, string outPath, TimeSpan elapsed)
    {
        double P(long a, long b) => b == 0 ? 0 : 100.0 * a / b;
        var n = records.Count;
        var byOutcome = records.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count());
        var reached = records.Count(r => CensusOutcomes.ReachedTheBody(r.Outcome));
        var ran = records.Where(r => r.Outcome == CensusOutcome.Ran).ToList();

        Console.WriteLine();
        Console.WriteLine("================ CENSUS: WHAT IS EVEN EXECUTABLE ================");
        Console.WriteLine("This is NOT the behaviour figure. Nothing here was compared with the game;");
        Console.WriteLine("it only says whether the recovered code can be made to run at all.");
        Console.WriteLine();
        Console.WriteLine($"Methods with a body        : {universeSize:N0}");
        Console.WriteLine($"Attempted in this report   : {n:N0}  ({P(n, universeSize):F1}% of the universe)");
        Console.WriteLine($"REACHED THE BODY           : {reached:N0}  ({P(reached, n):F1}% of attempted)   <== the runtime got as far as executing it");
        Console.WriteLine();

        foreach (var outcome in CensusOutcomes.ReportOrder)
        {
            var count = byOutcome.GetValueOrDefault(outcome);
            if (count == 0)
                continue;

            Console.WriteLine($"  {CensusOutcomes.Label(outcome),-34} {count,8:N0}  {P(count, n),6:F2}%");
        }

        Console.WriteLine();
        Console.WriteLine("-- how good were the arguments? --");
        Console.WriteLine("   A method that 'ran clean' on all-null, all-empty, all-zero inputs is much weaker");
        Console.WriteLine("   evidence than one that ran on real values. These two lines are not the same claim.");
        Console.WriteLine($"  RAN, nothing to construct  : {ran.Count(r => r.Quality == "none"):N0}   (static, no parameters - the strongest evidence available)");
        Console.WriteLine($"  RAN, faithful arguments    : {ran.Count(r => r.Quality == "faithful"):N0}   (same argument types the comparison path would build)");
        Console.WriteLine($"  RAN, degenerate arguments  : {ran.Count(r => r.Quality == "degenerate"):N0}   <== read this one with suspicion");
        Console.WriteLine($"  ran on some inputs, threw on others: {records.Count(r => r.ThrewOnSome):N0}");

        var degeneracies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in records.Where(r => !string.IsNullOrEmpty(r.Degeneracies)))
        foreach (var kind in entry.Degeneracies.Split('|'))
            degeneracies[kind] = degeneracies.GetValueOrDefault(kind) + 1;

        if (degeneracies.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("-- which fabrications were used, over every attempted method --");
            foreach (var kv in degeneracies.OrderByDescending(k => k.Value))
                Console.WriteLine($"  {kv.Key,-28} {kv.Value,8:N0}");
        }

        Console.WriteLine();
        Console.WriteLine("-- by assembly (top 20 by methods attempted) --");
        Console.WriteLine($"  {"assembly",-34} {"tried",8} {"ran",8} {"threw",8} {"invalidIL",10} {"refused",9}");
        foreach (var group in records.GroupBy(r => r.Assembly).OrderByDescending(g => g.Count()).Take(20))
            Console.WriteLine($"  {Shorten(group.Key, 34),-34} {group.Count(),8:N0} {group.Count(r => r.Outcome == CensusOutcome.Ran),8:N0} {group.Count(r => r.Outcome == CensusOutcome.ThrewManaged),8:N0} {group.Count(r => r.Outcome == CensusOutcome.RuntimeRefusedBody),10:N0} {group.Count(r => !CensusOutcomes.ReachedTheBody(r.Outcome)),9:N0}");

        var invalid = records.Where(r => r.Outcome == CensusOutcome.RuntimeRefusedBody).ToList();
        if (invalid.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"-- THE RUNTIME REFUSED THE BODY: {invalid.Count:N0} methods, worst declaring types (top 25) --");
            Console.WriteLine("   These are recovered bodies that are not valid IL. The most actionable output here.");
            foreach (var group in invalid.GroupBy(r => r.Type).OrderByDescending(g => g.Count()).Take(25))
                Console.WriteLine($"  {group.Count(),6:N0}  {Shorten(group.Key, 70)}");
        }

        var typeInit = records.Where(r => r.Outcome == CensusOutcome.TypeInitFailed).ToList();
        if (typeInit.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"-- type initializer failed: {typeInit.Count:N0} methods, over {typeInit.Select(r => r.Type).Distinct().Count():N0} types (top 15) --");
            Console.WriteLine("   The cctor is recovered code too. One broken cctor takes out every method of its type.");
            foreach (var group in typeInit.GroupBy(r => r.Type).OrderByDescending(g => g.Count()).Take(15))
                Console.WriteLine($"  {group.Count(),6:N0}  {Shorten(group.Key, 70)}");
        }

        Console.WriteLine();
        Console.WriteLine("-- by declaring type, the ones that ran most (top 20) --");
        foreach (var group in ran.GroupBy(r => r.Type).OrderByDescending(g => g.Count()).Take(20))
            Console.WriteLine($"  {group.Count(),6:N0}  {Shorten(group.Key, 70)}");

        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in records)
        foreach (var kind in entry.ExceptionKinds)
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;

        if (kinds.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("-- exception types seen, by how many methods threw them (top 20) --");
            foreach (var kv in kinds.OrderByDescending(k => k.Value).Take(20))
                Console.WriteLine($"  {kv.Value,8:N0}  {kv.Key}");
        }

        Console.WriteLine();
        Console.WriteLine("-- cross-tab: why the COMPARISON selector refused them, and what the census found --");
        Console.WriteLine("   A high 'ran' against a strict rejection reason means the comparison path is leaving");
        Console.WriteLine("   working code unmeasured, not that the code is broken.");
        Console.WriteLine($"  {"selector reason",-46} {"tried",8} {"ran",8} {"invalidIL",10}");
        foreach (var group in records.GroupBy(r => r.SelectorReason).OrderByDescending(g => g.Count()).Take(20))
            Console.WriteLine($"  {Shorten(group.Key, 46),-46} {group.Count(),8:N0} {group.Count(r => r.Outcome == CensusOutcome.Ran),8:N0} {group.Count(r => r.Outcome == CensusOutcome.RuntimeRefusedBody),10:N0}");

        var comparable = records.Where(r => r.WasSelected).ToList();
        if (comparable.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("-- the methods the comparison path already covers, as a control group --");
            Console.WriteLine($"  in this census             : {comparable.Count:N0}");
            Console.WriteLine($"  ran                        : {comparable.Count(r => r.Outcome == CensusOutcome.Ran):N0}  ({P(comparable.Count(r => r.Outcome == CensusOutcome.Ran), comparable.Count):F1}%)");
            Console.WriteLine($"  refused by the runtime     : {comparable.Count(r => r.Outcome == CensusOutcome.RuntimeRefusedBody):N0}");
            Console.WriteLine("   If this group does not run at a much higher rate than the rest, the census is wrong.");
        }

        Console.WriteLine();
        if (elapsed > TimeSpan.Zero)
            Console.WriteLine($"Elapsed                    : {elapsed.TotalSeconds:F0}s");

        Console.WriteLine($"Census                     : {Path.GetFullPath(outPath)}");
    }

    private static string Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Int(string value, int fallback) =>
        value != null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private static string Shorten(string text, int length)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        return text.Length <= length ? text : text.Substring(0, length);
    }

    // Un fir de lucru cu predare de sarcina, nu un fir nou pentru fiecare metoda: 72.000 de porniri de
    // fir ar costa mai mult decat apelurile. Cand o sarcina nu se mai intoarce, firul este ABANDONAT -
    // ramane viu, de fundal - iar apelantul isi face altul. Este singurul mod de a supravietui unei
    // bucle infinite fara sa reporneasca procesul, si de aceea are un plafon in Sweep.
    private sealed class Invoker
    {
        private readonly AutoResetEvent _go = new(false);
        private readonly AutoResetEvent _done = new(false);
        private Func<CensusRecord> _job;
        private CensusRecord _result;
        private Exception _error;

        public Invoker()
        {
            var thread = new Thread(Loop) { IsBackground = true, Name = "census-invoker" };
            thread.Start();
        }

        private void Loop()
        {
            while (true)
            {
                _go.WaitOne();
                try
                {
                    _result = _job();
                    _error = null;
                }
                catch (Exception ex)
                {
                    _result = null;
                    _error = ex;
                }

                _done.Set();
            }
        }

        public bool TryRun(Func<CensusRecord> job, TimeSpan timeout, out CensusRecord result, out Exception error)
        {
            _job = job;
            _go.Set();
            if (!_done.WaitOne(timeout))
            {
                result = null;
                error = null;
                return false;
            }

            result = _result;
            error = _error;
            return true;
        }
    }
}
