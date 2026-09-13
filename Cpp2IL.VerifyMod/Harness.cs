using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cpp2IL.VerifyCore;

namespace Cpp2IL.VerifyMod;

/// <summary>
/// A treia bucata: citeste lista de lucru si o executa. Atat.
///
/// Ce NU face, si de ce asta este toata schimbarea. Nu construieste niciun index al jocului, nu clasifica
/// nimic, nu hotaraste ce argumente sa dea, nu scrie dump-uri. Toate acelea s-au facut pe disc, unde o
/// greseala costa o secunda. Aici raman doar apelurile - deci o sesiune de joc se cheltuie pe apeluri si pe
/// nimic altceva.
///
/// Ce se pastreaza din unealta veche, fiindca a trecut prin sute de morti de proces: jurnalul scris INAINTE
/// de fiecare apel. O metoda care omoara procesul nu mai apuca sa scrie nimic despre sine, deci numele ei
/// trebuie sa fie deja pe disc; repornirea il gaseste acolo, il trece drept KILLED - ceea ce ESTE un
/// rezultat despre ea - si merge mai departe de la urmatoarea.
/// </summary>
internal static class Harness
{
    private static string _directory;
    private static Action<string> _log;
    private static RecoveredCode _recovered;
    private static Dictionary<string, MethodBase> _gameIndex;

    private static List<string[]> _queue;
    private static int _at;
    private static int _perFrame;
    private static int _max;
    private static bool _seedReceivers;

    private static FileStream _journal;
    private static StreamWriter _results;
    private static readonly Dictionary<string, int> Verdicts = new Dictionary<string, int>(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> Observations = new Dictionary<string, int>(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> NotReached = new Dictionary<string, int>(StringComparer.Ordinal);

    public static bool Finished { get; private set; }

    // ------------------------------------------------------------------------------------------------
    // Pornirea
    // ------------------------------------------------------------------------------------------------

    public static bool Prepare(string directory, string dllDirectory, int perFrame, int max, bool seedReceivers,
        Func<Dictionary<string, MethodBase>> gameIndexFactory, Action<string> log)
    {
        _directory = directory;
        _log = log;
        _perFrame = perFrame;
        _max = max;
        _seedReceivers = seedReceivers;

        var worklistPath = Path.Combine(directory, PlanFiles.WorklistFile);
        if (!File.Exists(worklistPath))
        {
            log("Lipseste " + PlanFiles.WorklistFile + " - ruleaza intai Cpp2IL.VerifyPlan. Harnasul nu are ce executa.");
            return false;
        }

        // Ordinea NU se poate schimba: indexul jocului se cladeste INAINTE ca vreun assembly recuperat sa
        // fie incarcat. Indexarea merge peste assembly-urile domeniului, iar cele din contextul nostru apar
        // si ele acolo - un Assembly-CSharp recuperat incarcat mai devreme ar intra in index sub exact
        // aceleasi chei si am compara codul recuperat cu el insusi, care este perfect de acord si nu
        // inseamna nimic.
        _gameIndex = gameIndexFactory();
        log("Indexul jocului: " + _gameIndex.Count + " metode.");

        _recovered = new RecoveredCode(dllDirectory);

        // Ordinea de mai jos nu este intamplatoare si a fost gresita o data.
        //
        // Ce s-a masurat pana acum se citeste INAINTE sa se deschida fisierul de scris, ca sa nu se citeasca
        // dintr-un fisier pe care tocmai il tinem deschis. Fisierul de rezultate se deschide INAINTE de
        // citirea jurnalului, fiindca citirea jurnalului chiar SCRIE un rand - metoda care a omorat
        // procesul, trecuta ca KILLED - iar daca fisierul n-ar fi deschis inca, randul acela s-ar pierde
        // tacut si tocmai vinovata caderii ar ramane nescrisa. Jurnalul se deschide ultimul, fiindca se
        // deschide cu trunchiere si ar sterge chiar numele pe care il citim din el.
        var done = AlreadyMeasured();

        var resultsPath = Path.Combine(directory, PlanFiles.ResultsFile);
        var fresh = !File.Exists(resultsPath);
        _results = new StreamWriter(new FileStream(resultsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));

        if (fresh)
        {
            _results.WriteLine(PlanFiles.Header(PlanFiles.ResultColumns));
            _results.Flush();
        }

        var skip = ReadJournalAndSkip();

        if (!ReadWorklist(worklistPath, skip, done, out var problem))
        {
            log(problem);
            return false;
        }

        _journal = new FileStream(Path.Combine(directory, PlanFiles.JournalFile),
            FileMode.Create, FileAccess.Write, FileShare.ReadWrite);

        return true;
    }

    private static bool ReadWorklist(string path, HashSet<string> skip, HashSet<string> done, out string problem)
    {
        problem = "";
        _queue = new List<string[]>();

        var first = true;
        var total = 0;
        var skipped = 0;
        var already = 0;

        foreach (var line in File.ReadLines(path))
        {
            if (first)
            {
                first = false;

                if (!PlanFiles.HeaderMatches(line))
                {
                    problem = PlanFiles.WorklistFile + " este scris cu alta forma decat " + PlanFiles.Schema
                        + " - ruleaza din nou Cpp2IL.VerifyPlan. Pornit asa, jocul s-ar deschide degeaba.";
                    return false;
                }

                continue;
            }

            if (line.Length == 0)
                continue;

            var fields = PlanFiles.Fields(line);
            if (fields.Length <= PlanFiles.WorkWeight)
                continue;

            total++;

            if (skip.Contains(fields[PlanFiles.WorkKey]))
            {
                skipped++;
                continue;
            }

            if (done.Contains(fields[PlanFiles.WorkKey]))
            {
                already++;
                continue;
            }

            _queue.Add(fields);

            if (_max > 0 && _queue.Count >= _max)
                break;
        }

        _log("Lista de lucru: " + total + " randuri citite, " + skipped + " sarite, " + already
            + " deja masurate, " + _queue.Count + " de facut acum.");

        return true;
    }

    private static HashSet<string> AlreadyMeasured()
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        var path = Path.Combine(_directory, PlanFiles.ResultsFile);

        if (!File.Exists(path))
            return done;

        foreach (var line in File.ReadLines(path))
        {
            var at = line.IndexOf(PlanFiles.Sep);
            if (at > 0)
                done.Add(line.Substring(0, at));
        }

        return done;
    }

    /// <summary>
    /// Lista de sarituri, cu doua izvoare care se aduna in acelasi fisier.
    ///
    /// Cel dintai vine DIN AFARA, si asta a fost cerut anume: planificatorul de pe disc, sau chiar mana
    /// omului, poate pune chei in verify-skip.txt inainte de pornire. Cel de-al doilea este jurnalul:
    /// metoda gasita in el la pornire nu este doar sarita, ci si INREGISTRATA ca KILLED, fiindca o cadere
    /// este un rezultat despre metoda aceea, nu o metoda pierduta.
    /// </summary>
    private static HashSet<string> ReadJournalAndSkip()
    {
        var skipPath = Path.Combine(_directory, PlanFiles.SkipFile);
        var journalPath = Path.Combine(_directory, PlanFiles.JournalFile);

        var skip = new HashSet<string>(StringComparer.Ordinal);

        if (File.Exists(skipPath))
            foreach (var line in File.ReadAllLines(skipPath))
                if (line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
                    skip.Add(line);

        if (!File.Exists(journalPath))
            return skip;

        string crashed;
        try
        {
            crashed = File.ReadAllText(journalPath).Trim('\0', ' ', '\r', '\n');
        }
        catch (Exception)
        {
            return skip;
        }

        var parts = crashed.Split(PlanFiles.Sep);
        var key = parts[0];

        if (key.Length > 0 && skip.Add(key))
        {
            File.AppendAllText(skipPath, key + Environment.NewLine);

            var stage = parts.Length > 2 ? parts[2] : "necunoscuta";
            WriteResult(key, parts.Length > 1 ? parts[1] : "", Verdict.Killed, Observed.Nothing,
                "", "", "", "a luat procesul cu ea la etapa " + stage);

            _log("Rularea trecuta a murit in " + key + " la etapa " + stage
                + " - inregistrata ca " + Verdict.Killed + " si sarita de acum.");
        }

        try
        {
            File.Delete(journalPath);
        }
        catch (Exception)
        {
            // Un jurnal care nu se sterge inseamna cel mult ca aceeasi metoda se raporteaza inca o data.
        }

        return skip;
    }

    // ------------------------------------------------------------------------------------------------
    // Executia
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Cate CPP2IL_VERIFY_PER_FRAME pe cadru, si jocul ramane viu intre transe.
    ///
    /// Nu este politete. Zeci de mii de apeluri intr-un cadru inseamna un joc inghetat minute in sir, pe
    /// care Windows il arata ca "nu raspunde" si pe care utilizatorul sau sistemul il omoara - iar o metoda
    /// omorata asa ajunge in jurnal drept vinovata de o cadere pe care nu a produs-o.
    /// </summary>
    public static void Tick()
    {
        if (Finished)
            return;

        for (var i = 0; i < _perFrame && _at < _queue.Count; i++)
            Step(_queue[_at++]);

        if (_at < _queue.Count)
        {
            if (_at % 500 < _perFrame && _at > 0)
                _log("  " + _at + " / " + _queue.Count + "...");

            return;
        }

        Finish();
    }

    private static void Step(string[] row)
    {
        var key = row[PlanFiles.WorkKey];
        var assembly = row[PlanFiles.WorkAssembly];
        var quality = row[PlanFiles.WorkQuality];

        WriteJournal(key, assembly, "rezolvare");

        if (!_gameIndex.TryGetValue(key, out var game))
        {
            NotReachedBecause(key, assembly, quality, "cheia nu mai este in indexul jocului");
            return;
        }

        MethodBase ours;
        try
        {
            ours = _recovered.Index(assembly).TryGetValue(key, out var found) ? found : null;
        }
        catch (Exception ex)
        {
            NotReachedBecause(key, assembly, quality, "indexul recuperat a aruncat: " + ex.GetType().Name);
            return;
        }

        if (ours == null)
        {
            NotReachedBecause(key, assembly, quality, "cheia nu mai este in indexul recuperat");
            return;
        }

        // Verificarea de identitate care nu se sare: daca "metoda jocului" vine chiar din contextul nostru,
        // am compara codul recuperat cu el insusi - perfect de acord si fara niciun inteles.
        if (_recovered.Owns(game.DeclaringType?.Assembly))
        {
            NotReachedBecause(key, assembly, quality, "metoda jocului este de fapt tot cea recuperata");
            return;
        }

        object gameReceiver = null;
        object ourReceiver = null;

        if (!game.IsStatic)
        {
            WriteJournal(key, assembly, "receptor");

            gameReceiver = NativeReceiver.Allocate(game.DeclaringType, out var whyGame);
            if (gameReceiver == null)
            {
                NotReachedBecause(key, assembly, quality, "receptorul jocului: " + whyGame);
                return;
            }

            try
            {
                ourReceiver = RuntimeHelpers.GetUninitializedObject(ours.DeclaringType);
            }
            catch (Exception ex)
            {
                NotReachedBecause(key, assembly, quality, "receptorul recuperat: " + ex.GetType().Name);
                return;
            }
        }

        var seeds = new List<Seed>();
        if (ourReceiver != null && _seedReceivers && row[PlanFiles.WorkReceiver] == "seeded")
        {
            if (!Sow(row[PlanFiles.WorkReceiverSeed], game.DeclaringType, ours.DeclaringType,
                    gameReceiver, ourReceiver, seeds, out var whySeed))
            {
                NotReachedBecause(key, assembly, quality, "semanarea receptorului: " + whySeed);
                return;
            }
        }

        if (!BuildArguments(row[PlanFiles.WorkArguments], game, ours, out var gameArgs, out var ourArgs, out var whyArgs, out var layout))
        {
            if (layout)
                Record(key, assembly, Verdict.LayoutDiffers, Observed.Nothing, quality, "", "", whyArgs);
            else
                NotReachedBecause(key, assembly, quality, whyArgs);

            return;
        }

        WriteJournal(key, assembly, "apel-joc");
        var gameThrew = Call(game, gameReceiver, gameArgs, out var gameValue, out var gameError);

        WriteJournal(key, assembly, "apel-recuperat");
        var ourThrew = Call(ours, ourReceiver, ourArgs, out var ourValue, out var ourError);

        WriteJournal(key, assembly, "comparatie");
        Compare(row, key, assembly, quality, game, ours, gameReceiver, ourReceiver, seeds,
            gameThrew, ourThrew, gameValue, ourValue, gameError, ourError, gameArgs, ourArgs);
    }

    private static bool Call(MethodBase method, object receiver, object[] args, out object value, out string error)
    {
        value = null;
        error = "";

        try
        {
            // Un constructor chemat asa ruleaza corpul peste obiectul DEJA alocat, in loc sa aloce altul.
            // Asta este chiar ce ne trebuie: amandoua partile primesc receptorul fabricat de noi, iar ce
            // scrie constructorul in campuri se citeste inapoi si se compara.
            value = method.Invoke(receiver, args);
            return false;
        }
        catch (Exception ex)
        {
            // Exceptia se intoarce ca NUME de tip si nu ca mesaj: mesajele poarta nume de tipuri si adrese
            // care difera intre cele doua universuri, deci doua NullReferenceException identice ca inteles
            // ar arata ca fiind diferite.
            var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            error = inner.GetType().Name;
            return true;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Receptorul semanat
    // ------------------------------------------------------------------------------------------------

    private sealed class Seed
    {
        public string Name;
        public Materialise.Member Game;
        public Materialise.Member Ours;
    }

    /// <summary>
    /// Scrie aceleasi valori in aceleasi campuri pe amandoua partile.
    ///
    /// Simetric prin constructie: aceiasi biti, prin reflectie, in campuri cu acelasi nume - fara sa ruleze
    /// niciun cod al niciuneia dintre parti. Un constructor chemat pe fiecare parte ar fi parut mai firesc,
    /// dar acela ar fi rulat cod nativ de o parte si cod recuperat de cealalta, adica tocmai bucata pe care
    /// vrem sa o masuram, si receptorii ar fi iesit diferiti ori de cate ori recuperarea este gresita.
    ///
    /// Daca un camp nu se poate scrie pe AMANDOUA partile, nu se scrie pe niciuna. Un receptor semanat pe
    /// jumatate este mai rau decat unul gol: diferentele de dupa ar fi ale noastre.
    /// </summary>
    private static bool Sow(string plan, Type gameType, Type ourType, object gameReceiver, object ourReceiver,
        List<Seed> seeds, out string problem)
    {
        problem = "";

        if (string.IsNullOrEmpty(plan))
            return true;

        foreach (var entry in plan.Split(Recipe.Separator))
        {
            var at = entry.IndexOf('=');
            if (at <= 0)
                continue;

            var name = entry.Substring(0, at);
            var recipe = Recipe.Parse(entry.Substring(at + 1));

            var ours = Materialise.Member.Find(ourType, name);
            var theirs = Materialise.Member.Find(gameType, MethodKeys.MangleMember(name));

            // Planificat pe disc, negasit in joc. Nu este fatal si nu opreste apelul - campul iese din
            // semanare si din observatie, pe amandoua partile deodata, deci receptorii raman echivalenti.
            if (ours == null || theirs == null)
                continue;

            if (!Materialise.Value(recipe, ours.Type, out var ourValue, out var whyOurs))
            {
                problem = name + " pe partea noastra: " + whyOurs;
                return false;
            }

            if (!Materialise.Value(recipe, theirs.Type, out var gameValue, out var whyGame))
            {
                problem = name + " pe partea jocului: " + whyGame;
                return false;
            }

            if (!ours.Write(ourReceiver, ourValue, out var failedOurs))
            {
                problem = name + " nu s-a putut scrie pe partea noastra: " + failedOurs;
                return false;
            }

            if (!theirs.Write(gameReceiver, gameValue, out var failedGame))
            {
                problem = name + " nu s-a putut scrie pe partea jocului: " + failedGame;
                return false;
            }

            seeds.Add(new Seed { Name = name, Game = theirs, Ours = ours });
        }

        return true;
    }

    // ------------------------------------------------------------------------------------------------
    // Argumentele
    // ------------------------------------------------------------------------------------------------

    private static bool BuildArguments(string plan, MethodBase game, MethodBase ours,
        out object[] gameArgs, out object[] ourArgs, out string problem, out bool layout)
    {
        problem = "";
        layout = false;

        var gameParameters = game.GetParameters();
        var ourParameters = ours.GetParameters();

        gameArgs = new object[gameParameters.Length];
        ourArgs = new object[ourParameters.Length];

        if (gameParameters.Length != ourParameters.Length)
        {
            problem = "numar diferit de parametri: joc " + gameParameters.Length + ", recuperat " + ourParameters.Length;
            return false;
        }

        var recipes = Recipe.Split(plan);
        if (recipes.Length != ourParameters.Length)
        {
            problem = "lista de lucru da " + recipes.Length + " retete pentru " + ourParameters.Length + " parametri";
            return false;
        }

        for (var i = 0; i < recipes.Length; i++)
        {
            if (!Materialise.Value(recipes[i], ourParameters[i].ParameterType, out ourArgs[i], out var whyOurs))
            {
                problem = "argumentul " + i + " pe partea noastra: " + whyOurs;
                layout = whyOurs.StartsWith("forma difera", StringComparison.Ordinal);
                return false;
            }

            if (!Materialise.Value(recipes[i], gameParameters[i].ParameterType, out gameArgs[i], out var whyGame))
            {
                problem = "argumentul " + i + " pe partea jocului: " + whyGame;
                layout = whyGame.StartsWith("forma difera", StringComparison.Ordinal);
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------------------------------------
    // Comparatia
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Verdictul, dat dupa o singura regula: se scrie ACORD numai daca a existat ceva de privit si ce s-a
    /// privit s-a potrivit.
    ///
    /// Asta inchide gaura prin care se scurgea zgomotul in coloana de succes. Metoda care intoarce void si
    /// nu scrie nimic in receptor nu mai poate iesi "de acord" - are verdictul ei, NOTHING_OBSERVED, si nu
    /// se aduna nicaieri. La fel doua referinte nule.
    /// </summary>
    private static void Compare(string[] row, string key, string assembly, string quality,
        MethodBase game, MethodBase ours, object gameReceiver, object ourReceiver, List<Seed> seeds,
        bool gameThrew, bool ourThrew, object gameValue, object ourValue, string gameError, string ourError,
        object[] gameArgs, object[] ourArgs)
    {
        if (gameThrew && ourThrew)
        {
            var same = string.Equals(gameError, ourError, StringComparison.Ordinal);
            Record(key, assembly, same ? Verdict.BothThrewSame : Verdict.BothThrewDifferent,
                Observed.ExceptionKind, quality, gameError, ourError, "");
            return;
        }

        if (gameThrew || ourThrew)
        {
            Record(key, assembly, gameThrew ? Verdict.OnlyGameThrew : Verdict.OnlyOursThrew,
                Observed.ExceptionKind, quality,
                gameThrew ? gameError : Show(gameValue),
                ourThrew ? ourError : Show(ourValue), "");
            return;
        }

        // Starea de dupa apel, adunata inainte de verdict. Ordinea conteaza: un void care a scris in
        // receptor este o metoda MASURATA, iar daca s-ar hotari verdictul dupa valoarea intoarsa singura,
        // ar fi trecuta drept "nimic de privit".
        var differences = new List<string>();
        var observedState = false;

        foreach (var seed in seeds)
        {
            // Un camp pe care nu il putem CITI inapoi pe amandoua partile nu a fost observat, si atunci
            // nu are voie sa umfle "ce s-a privit". Un getter nativ poate arunca pe un obiect pe jumatate
            // initializat; daca aici s-ar pune observedState numai fiindca exista campuri semanate, un
            // rand cu zero campuri citite ar iesi in raport ca "receiver-fields" si ar fi o minciuna in
            // chiar coloana pe care raportul se bizuie.
            if (!seed.Game.Read(gameReceiver, out var after) || !seed.Ours.Read(ourReceiver, out var mine))
                continue;

            observedState = true;

            if (!SameValue(after, mine))
                differences.Add("camp " + seed.Name + ": joc=" + Show(after) + " noi=" + Show(mine));
        }

        // Tablourile date ca argument: o metoda care scrie intr-un tablou primit este masurabila prin el
        // chiar daca nu intoarce nimic. Aceeasi idee ca la campurile receptorului, pe alt drum.
        for (var i = 0; i < ourArgs.Length; i++)
        {
            if (ourArgs[i] == null || gameArgs[i] == null)
                continue;

            if (!Materialise.ReadArray(ourArgs[i], out var mine, out var mineLength))
                continue;

            if (!Materialise.ReadArray(gameArgs[i], out var theirs, out var theirLength))
                continue;

            observedState = true;

            if (mineLength != theirLength || !SameBits(theirs, mine))
                differences.Add("tabloul " + i + ": joc=" + theirLength + " elemente, noi=" + mineLength);
        }

        var plan = row[PlanFiles.WorkReturnPlan];
        var returnVerdict = CompareReturn(plan, game, ours, gameValue, ourValue, out var observedReturn, out var detail);

        if (returnVerdict == ReturnComparison.Layout)
        {
            Record(key, assembly, Verdict.LayoutDiffers, Observed.Nothing, quality, Show(gameValue), Show(ourValue), detail);
            return;
        }

        var different = differences.Count > 0 || returnVerdict == ReturnComparison.Different;
        var observed = Name(observedReturn, observedState);

        if (observed == Observed.Nothing)
        {
            // Nimic de privit. NU este acord, si tocmai numarul asta spune cat de mult mai are de castigat
            // partea de observatii - fiecare rand de aici este o metoda chemata degeaba.
            Record(key, assembly, Verdict.NothingObserved, Observed.Nothing, quality, "", "", "");
            return;
        }

        if (returnVerdict == ReturnComparison.BothNull && !observedState)
        {
            // Amandoua au iesit pe o ramura care intoarce null si nu au atins nimic altceva. Nu s-a
            // demonstrat nimic despre restul corpului.
            Record(key, assembly, Verdict.BothNull, Observed.ReturnNullness, quality, "(null)", "(null)", "");
            return;
        }

        Record(key, assembly, different ? Verdict.Different : Verdict.Same, observed, quality,
            Show(gameValue), Show(ourValue), different ? string.Join("; ", differences.ToArray()) : "");
    }

    private enum ReturnComparison
    {
        Nothing,
        Same,
        Different,
        BothNull,
        Layout,
    }

    private static ReturnComparison CompareReturn(string plan, MethodBase game, MethodBase ours,
        object gameValue, object ourValue, out string observed, out string detail)
    {
        observed = Observed.Nothing;
        detail = "";

        switch (plan)
        {
            case "void":
                return ReturnComparison.Nothing;

            case "bits":
                observed = Observed.ReturnBits;
                return SameValue(gameValue, ourValue) ? ReturnComparison.Same : ReturnComparison.Different;

            case "text":
                if (gameValue == null && ourValue == null)
                {
                    observed = Observed.ReturnNullness;
                    return ReturnComparison.BothNull;
                }

                observed = Observed.ReturnText;
                return string.Equals(gameValue as string, ourValue as string, StringComparison.Ordinal)
                    ? ReturnComparison.Same
                    : ReturnComparison.Different;

            case "shape":
            {
                var gameShape = ValueShape.For((game as MethodInfo)?.ReturnType);
                var ourShape = ValueShape.For((ours as MethodInfo)?.ReturnType);

                if (gameShape == null || ourShape == null)
                {
                    detail = "structura intoarsa nu se reduce la frunze pe una dintre parti";
                    return ReturnComparison.Layout;
                }

                if (gameShape.LeafCount != ourShape.LeafCount)
                {
                    detail = "structura intoarsa are " + gameShape.LeafCount + " frunze in joc si "
                        + ourShape.LeafCount + " la noi";
                    return ReturnComparison.Layout;
                }

                var theirs = new List<long>();
                var mine = new List<long>();

                try
                {
                    gameShape.Absorb(gameValue, theirs);
                    ourShape.Absorb(ourValue, mine);
                }
                catch (Exception)
                {
                    detail = "citirea structurii intoarse a aruncat";
                    return ReturnComparison.Layout;
                }

                observed = Observed.ReturnShape;
                return SameBits(theirs, mine) ? ReturnComparison.Same : ReturnComparison.Different;
            }

            case "array":
            {
                if (gameValue == null && ourValue == null)
                {
                    observed = Observed.ReturnNullness;
                    return ReturnComparison.BothNull;
                }

                if (gameValue == null || ourValue == null)
                {
                    observed = Observed.ReturnNullness;
                    return ReturnComparison.Different;
                }

                if (!Materialise.ReadArray(gameValue, out var theirs, out var theirLength)
                    || !Materialise.ReadArray(ourValue, out var mine, out var mineLength))
                {
                    observed = Observed.ReturnNullness;
                    return ReturnComparison.Same;
                }

                observed = Observed.ReturnArray;
                return theirLength == mineLength && SameBits(theirs, mine)
                    ? ReturnComparison.Same
                    : ReturnComparison.Different;
            }

            default:
                // Un tip intors care este o clasa: cele doua valori traiesc in universuri diferite si nu au
                // cum sa fie comparate pe continut. Ramane nulitatea, care este putin - si scrisa asa ca sa
                // se vada ca este putin.
                if (gameValue == null && ourValue == null)
                {
                    observed = Observed.ReturnNullness;
                    return ReturnComparison.BothNull;
                }

                observed = Observed.ReturnNullness;
                return gameValue == null || ourValue == null ? ReturnComparison.Different : ReturnComparison.Same;
        }
    }

    private static string Name(string observedReturn, bool observedState)
    {
        if (observedReturn != Observed.Nothing && observedState)
            return Observed.ReturnAndReceiver;

        if (observedReturn != Observed.Nothing)
            return observedReturn;

        return observedState ? Observed.ReceiverFields : Observed.Nothing;
    }

    private static bool SameBits(List<long> a, List<long> b)
    {
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i])
                return false;

        return true;
    }

    /// <summary>
    /// Egalitate pe BITI, nu pe valoare: un float care difera pe ultimul bit este o diferenta si trebuie sa
    /// se vada ca atare, nu sa fie inghitita de o toleranta pe care nu a ales-o nimeni. La fel -0.0 fata de
    /// 0.0, si NaN-urile cu incarcaturi diferite.
    /// </summary>
    private static bool SameValue(object a, object b)
    {
        if (a == null || b == null)
            return a == null && b == null;

        if (a is string left && b is string right)
            return string.Equals(left, right, StringComparison.Ordinal);

        var theirs = new List<long>();
        var mine = new List<long>();

        var theirShape = ValueShape.For(a.GetType());
        var ourShape = ValueShape.For(b.GetType());

        if (theirShape == null || ourShape == null)
            return false;

        try
        {
            theirShape.Absorb(a, theirs);
            ourShape.Absorb(b, mine);
        }
        catch (Exception)
        {
            return false;
        }

        return SameBits(theirs, mine);
    }

    // "R" pentru virgula mobila: o diferenta pe ultimul bit trebuie sa se VADA in fisier, altfel un dezacord
    // de rotunjire si unul de logica arata identic.
    private static string Show(object value)
    {
        switch (value)
        {
            case null: return "(null)";
            case float f: return f.ToString("R", CultureInfo.InvariantCulture);
            case double d: return d.ToString("R", CultureInfo.InvariantCulture);
            case string s: return s.Length > 120 ? s.Substring(0, 120) + "..." : s;
            default:
                try
                {
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.GetType().Name;
                }
                catch (Exception)
                {
                    return value.GetType().Name;
                }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Scrisul
    // ------------------------------------------------------------------------------------------------

    private static void NotReachedBecause(string key, string assembly, string quality, string reason)
    {
        Record(key, assembly, Verdict.NotReached, Observed.Nothing, quality, "", "", reason);
        Bump(NotReached, reason);
    }

    private static void Record(string key, string assembly, string verdict, string observed, string quality,
        string game, string ours, string note)
    {
        Bump(Verdicts, verdict);
        Bump(Observations, observed);
        WriteResult(key, assembly, verdict, observed, quality, game, ours, note);
    }

    private static void WriteResult(string key, string assembly, string verdict, string observed, string quality,
        string game, string ours, string note)
    {
        if (_results == null)
            return;

        _results.WriteLine(PlanFiles.Row(key, assembly, verdict, observed, quality, game, ours, note));

        // Golit la fiecare rand. Un joc care moare la a treia metoda trebuie sa lase in urma cele doua
        // rezultate de dinainte, nu un fisier gol.
        _results.Flush();
    }

    private static void WriteJournal(string key, string assembly, string stage)
    {
        if (_journal == null)
            return;

        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                key + PlanFiles.Sep + assembly + PlanFiles.Sep + stage + "\n");

            _journal.Seek(0, SeekOrigin.Begin);
            _journal.SetLength(0);
            _journal.Write(bytes, 0, bytes.Length);
            _journal.Flush(true);
        }
        catch (Exception)
        {
            // Un jurnal care nu se scrie face repornirea mai proasta, nu rularea imposibila.
        }
    }

    private static void Finish()
    {
        Finished = true;

        try
        {
            _journal?.Dispose();
            _journal = null;
            File.Delete(Path.Combine(_directory, PlanFiles.JournalFile));
        }
        catch (Exception)
        {
        }

        var lines = new List<string>
        {
            "Metode incercate in sesiunea asta: " + _at,
            "",
            "Verdicte:",
        };

        foreach (var pair in Sorted(Verdicts))
            lines.Add("  " + pair.Value.ToString(CultureInfo.InvariantCulture).PadLeft(7) + "  " + pair.Key);

        lines.Add("");
        lines.Add("Ce s-a privit:");
        foreach (var pair in Sorted(Observations))
            lines.Add("  " + pair.Value.ToString(CultureInfo.InvariantCulture).PadLeft(7) + "  " + pair.Key);

        if (NotReached.Count > 0)
        {
            lines.Add("");
            lines.Add("De ce nu s-a ajuns la apel:");
            foreach (var pair in Sorted(NotReached))
                lines.Add("  " + pair.Value.ToString(CultureInfo.InvariantCulture).PadLeft(7) + "  " + pair.Key);
        }

        lines.Add("");
        lines.Add("De citit asa: SAME si DIFFERENT sunt singurele randuri care spun ceva despre corpul metodei,");
        lines.Add("si numai impreuna cu coloana 'observed', care spune CE s-a privit. NOTHING_OBSERVED si");
        lines.Add("BOTH_NULL nu sunt acorduri - sunt metode chemate degeaba, si numarul lor este chiar");
        lines.Add("masura a cat mai are de castigat partea de argumente si de observatii.");

        try
        {
            File.WriteAllLines(Path.Combine(_directory, PlanFiles.SummaryFile), lines);
        }
        catch (Exception)
        {
        }

        foreach (var line in lines)
            _log(line);

        try
        {
            _results?.Dispose();
            _results = null;
        }
        catch (Exception)
        {
        }
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
