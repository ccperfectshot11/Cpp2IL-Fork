using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace Cpp2IL.VerifyMod;

/// <summary>
/// Faza 3: codul recuperat rulat pe starea REALA a jocului, la punctele reale de apel.
///
/// De ce exista, pe scurt. Fazele 1 si 2 compara doua hash-uri obtinute chemand aceeasi metoda cu aceleasi
/// argumente fabricate in doua procese. Asta cere ca argumentele sa poata fi CLADITE identic pe ambele
/// parti, ceea ce limiteaza masuratoarea la semnaturi numai-primitive: masurat pe fisierele reale din
/// Mods, 2.038 de metode comparabile, 1.828 perechi formate, 1.232 identice - din 71.826 de metode cu
/// corp in recensamant. Restul nu sunt gresite, sunt nemasurabile, iar motivul numarul unu este chiar
/// receptorul: 34.361 de metode au deja toti parametrii numai-primitive si cad DOAR pentru ca sunt metode
/// de instanta pe o clasa (numarat in census.jsonl, unde verificarea parametrilor se face inaintea celei
/// de receptor, deci eticheta aceea le implica pe celelalte).
///
/// Ideea: receptorul nu trebuie fabricat. Jocul il are deja. Punem un carlig Harmony pe metoda reala,
/// asteptam ca jocul sa o cheme singur, si atunci avem trei lucruri deodata - obiectul viu, argumentele
/// adevarate si raspunsul metodei ADEVARATE. Copiem starea obiectului intr-un obiect de tipul recuperat,
/// chemam metoda recuperata cu aceleasi argumente si comparam cele doua raspunsuri bit cu bit.
///
/// De ce POSTFIX si nu prefix, si de ce asta este intreaga poanta: un postfix lasa metoda jocului sa
/// ruleze si sa-si faca efectele, iar noi primim rezultatul ei. Asa avem si termenul de comparatie, si
/// zero risc pentru starea jocului. Un prefix care ar sari peste original ne-ar da exact dovada slaba pe
/// care o evitam - "nu a crapat" - si ar rupe efectele laterale ale metodei.
///
/// Ce NU face, spus limpede: corpul recuperat primeste o COPIE a receptorului, deci nu poate scrie in
/// obiectul jocului. Prin urmare unealta masoara valoarea intoarsa, nu efectele laterale. O metoda care
/// se exprima numai prin ce scrie in lume ramane nemasurata, iar ca sa fie masurata ar trebui rescris
/// IL-ul recuperat peste tipurile interop - alta treaba, mult mai mare, si nu una ascunsa aici.
///
/// Variabile de mediu - TOATE oprite implicit, fiindca asta este singura parte a proiectului care ruleaza
/// cod neverificat intr-un joc conectat la serverele lui:
///
///   CPP2IL_SUBST=1              porneste faza 3 (fara ea nu se intampla nimic)
///   CPP2IL_SUBST_DLLS=&lt;dir&gt;     directorul cu DLL-urile recuperate (obligatoriu)
///   CPP2IL_SUBST_PLAN=1         scrie numai lista de lucru si se opreste
///   CPP2IL_SUBST_FILTER=a,b     fragmente de nume de assembly pentru planificare (implicit Assembly-CSharp)
///   CPP2IL_SUBST_TIER=0|1|01    ce trepte se planifica (implicit 01)
///   CPP2IL_SUBST_MODE=shadow|substitute   implicit shadow: nu se schimba niciodata raspunsul jocului
///   CPP2IL_SUBST_BATCH=N        cate metode stau carligate deodata (implicit 1)
///   CPP2IL_SUBST_SAMPLES=N      cate apeluri se masoara per metoda inainte de descarligare (implicit 64)
///   CPP2IL_SUBST_DWELL=S        cate secunde sta armata o transa daca jocul nu o cheama (implicit 30)
///   CPP2IL_SUBST_PROMOTE=N      cate potriviri consecutive inainte ca modul substitute sa scrie inapoi (implicit 16)
///   CPP2IL_SUBST_MAX=N          cate metode se incearca in aceasta sesiune (implicit 0 = toate)
///
/// Rezultatele se aduna in subst-results.tsv si nu se rescriu: o sesiune noua sare peste cheile deja
/// masurate, deci masuratoarea se poate face in reprize de cate douazeci de minute in loc de una singura
/// de treisprezece ore.
/// </summary>
internal static class SubstitutionHarness
{
    private const string WorkListFile = "subst-worklist.txt";
    private const string ResultsFile = "subst-results.tsv";
    private const string InflightFile = "subst-inflight.txt";
    private const string SkipFile = "subst-skip.txt";
    private const string SummaryFile = "subst-summary.txt";

    private const char Sep = '\u001f';

    private static Action<string> _log = _ => { };
    private static string _directory = ".";
    private static Harmony _harmony;
    private static RecoveredCode _recovered;
    private static Func<Dictionary<string, MethodBase>> _gameIndexFactory;
    private static Dictionary<string, MethodBase> _gameIndex;

    private static bool _enabled;
    private static bool _planOnly;
    private static bool _writeBack;
    private static string _dllDirectory;
    private static string _filter;
    private static HashSet<SubstTier> _tiers;
    private static int _batch;
    private static int _samples;
    private static int _dwellSeconds;
    private static int _promote;

    private static readonly object Gate = new object();
    private static readonly Dictionary<MethodBase, Binding> Armed = new Dictionary<MethodBase, Binding>();

    [ThreadStatic]
    private static bool _inside;

    private static List<SubstEntry> _queue;
    private static int _queueAt;
    private static List<Binding> _current = new List<Binding>();
    private static int _armedAtTick;
    private static bool _finished;
    private static int _done;
    private static bool _warnedAboutIdentity;

    // ------------------------------------------------------------------------------------------------
    // Configurare si ciclul de viata
    // ------------------------------------------------------------------------------------------------

    public static bool Enabled => _enabled;

    /// <summary>
    /// Ceruta prin mediu, chiar daca pe urma nu a putut porni. Nu este acelasi lucru cu Enabled, si
    /// diferenta este importanta: daca faza 3 a fost ceruta si a cazut la configurare, modul NU trebuie sa
    /// cada inapoi pe maturarea fazei 2. Aceea cheama mii de metode reale ale jocului de zece mii de ori
    /// fiecare, cu NaN si int.MinValue - ultimul lucru care trebuie sa se intample fiindca o variabila de
    /// mediu arata gresit.
    /// </summary>
    public static bool Requested => Environment.GetEnvironmentVariable("CPP2IL_SUBST") == "1";

    public static bool Configure(string directory, Harmony harmony, Action<string> log, Func<Dictionary<string, MethodBase>> gameIndexFactory)
    {
        _enabled = Requested;
        if (!_enabled)
            return false;

        _directory = directory;
        _harmony = harmony;
        _log = log;
        _gameIndexFactory = gameIndexFactory;

        _dllDirectory = Environment.GetEnvironmentVariable("CPP2IL_SUBST_DLLS") ?? "";
        _planOnly = Environment.GetEnvironmentVariable("CPP2IL_SUBST_PLAN") == "1";
        _writeBack = string.Equals(Environment.GetEnvironmentVariable("CPP2IL_SUBST_MODE"), "substitute", StringComparison.OrdinalIgnoreCase);
        _filter = Environment.GetEnvironmentVariable("CPP2IL_SUBST_FILTER") ?? "Assembly-CSharp";
        _batch = Number("CPP2IL_SUBST_BATCH", 1);
        _samples = Number("CPP2IL_SUBST_SAMPLES", 64);
        _dwellSeconds = Number("CPP2IL_SUBST_DWELL", 30);
        _promote = Number("CPP2IL_SUBST_PROMOTE", 16);

        var tiers = Environment.GetEnvironmentVariable("CPP2IL_SUBST_TIER") ?? "01";
        _tiers = new HashSet<SubstTier>();
        if (tiers.IndexOf('0') >= 0) _tiers.Add(SubstTier.None);
        if (tiers.IndexOf('1') >= 0) _tiers.Add(SubstTier.Receiver);

        if (_dllDirectory.Length == 0 || !Directory.Exists(_dllDirectory))
        {
            _log("CPP2IL_SUBST=1 dar CPP2IL_SUBST_DLLS nu arata catre un director existent - faza 3 nu porneste.");
            _enabled = false;
            return false;
        }

        if (_harmony == null)
        {
            _log("Nu exista instanta Harmony - faza 3 nu poate carliga nimic.");
            _enabled = false;
            return false;
        }

        _log("Faza 3 pornita. dll=" + _dllDirectory + " mod=" + (_writeBack ? "substitute" : "shadow")
            + " trepte=" + tiers + " transa=" + _batch + " esantioane=" + _samples + " asteptare=" + _dwellSeconds + "s");

        if (_writeBack)
            _log("ATENTIE: modul substitute inlocuieste raspunsul jocului dupa " + _promote + " potriviri consecutive.");

        return true;
    }

    private static int Number(string name, int fallback)
    {
        var text = Environment.GetEnvironmentVariable(name);
        return text != null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;
    }

    /// <summary>
    /// Pregatirea de la pornire: jurnalul rularii trecute, indexul jocului, codul recuperat si lista de
    /// lucru. Separata de Tick fiindca dureaza secunde si trebuie facuta o singura data.
    /// </summary>
    public static void Prepare()
    {
        var skip = ReadJournal();

        // Ordinea conteaza si nu se poate schimba: indexul jocului se cladeste INAINTE ca vreun assembly
        // recuperat sa fie incarcat. Indexarea merge peste AppDomain.CurrentDomain.GetAssemblies(), iar
        // acolo apar si assembly-urile din contextul nostru - un Assembly-CSharp recuperat incarcat mai
        // devreme ar intra in index sub exact aceleasi chei si am ajunge sa comparam codul recuperat cu el
        // insusi, care este perfect de acord si nu inseamna nimic. Verificarea din Bind prinde si cazul in
        // care cineva muta randurile astea.
        _recovered = new RecoveredCode(_dllDirectory);
        _gameIndex = _gameIndexFactory();
        _log("Indexul jocului: " + _gameIndex.Count + " metode.");

        var listPath = Path.Combine(_directory, WorkListFile);
        if (!File.Exists(listPath) || _planOnly)
            WritePlan(listPath);

        if (_planOnly)
        {
            _log("CPP2IL_SUBST_PLAN=1: lista scrisa, nu se carliga nimic.");

            // Rezumatul se scrie si aici, desi nu s-a masurat nimic: scriptul care porneste jocul asteapta
            // FISIERUL ca semn ca rularea s-a terminat. Fara el, o rulare de plan - care dureaza secunde -
            // ar parea blocata pana la expirarea celor treizeci de minute.
            Summarise();
            _finished = true;
            return;
        }

        _queue = new List<SubstEntry>();
        foreach (var line in File.ReadAllLines(listPath))
        {
            var entry = SubstEntry.FromLine(line);
            if (entry == null)
                continue;

            if (skip.Contains(entry.Key))
                continue;

            _queue.Add(entry);
        }

        // Plafon pe sesiune. Fara el o lista de o mie sase sute de metode cu o metoda pe rand si treizeci
        // de secunde de asteptare fiecare ar cere treisprezece ore de joc pornit - iar rezultatul s-ar
        // vedea abia la sfarsit. Cu plafon, fiecare rulare aduce o bucata masurata pe disc, si urmatoarea
        // continua de unde a ramas, fiindca lista sarita se citeste din rezultatele deja scrise.
        var max = Number("CPP2IL_SUBST_MAX", 0);
        var done = AlreadyMeasured();
        if (done.Count > 0)
            _queue.RemoveAll(entry => done.Contains(entry.Key));

        if (max > 0 && _queue.Count > max)
            _queue.RemoveRange(max, _queue.Count - max);

        _log("Lista de lucru: " + _queue.Count + " metode de incercat (" + skip.Count + " sarite dupa morti de proces, "
            + done.Count + " deja masurate in rulari anterioare).");
    }

    /// <summary>
    /// Cheile masurate deja, citite din fisierul de rezultate. Fara asta fiecare repornire ar lua-o de la
    /// capul listei si ar remasura la nesfarsit primele metode, exact ce l-ar fi omorat si pe recensamant
    /// daca nu si-ar fi tinut socoteala pe disc.
    /// </summary>
    private static HashSet<string> AlreadyMeasured()
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        var path = Path.Combine(_directory, ResultsFile);

        if (!File.Exists(path))
            return done;

        foreach (var line in File.ReadAllLines(path))
        {
            var at = line.IndexOf(Sep);
            if (at > 0)
                done.Add(line.Substring(0, at));
        }

        return done;
    }

    /// <summary>
    /// Jurnalul, exact in forma pe care recensamantul a dus-o prin 900 de morti de proces: fisierul
    /// ".inflight" se scrie INAINTE de incercare, fiindca o metoda care ia procesul cu ea nu poate fi
    /// prinsa si singurul fel in care rularea urmatoare trece de ea este sa stie numele ei.
    ///
    /// Singura diferenta fata de recensamant: aici o transa poate avea mai multe metode armate deodata,
    /// si atunci nu se stie care a fost vinovata. In cazul acela nu se sare peste niciuna - se cere
    /// impartirea in doua prin subst-bisect.txt, ca sa nu pierdem metode bune odata cu cea rea.
    /// </summary>
    private static HashSet<string> ReadJournal()
    {
        var skipPath = Path.Combine(_directory, SkipFile);
        var journalPath = Path.Combine(_directory, InflightFile);

        var skip = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(skipPath))
            foreach (var line in File.ReadAllLines(skipPath))
                if (line.Length > 0)
                    skip.Add(line);

        if (!File.Exists(journalPath))
            return skip;

        var armed = new List<string>();
        foreach (var line in File.ReadAllLines(journalPath))
            if (line.Length > 0)
                armed.Add(line);

        File.Delete(journalPath);

        if (armed.Count == 1)
        {
            if (skip.Add(armed[0]))
            {
                File.AppendAllText(skipPath, armed[0] + Environment.NewLine);
                _log("Rularea trecuta a murit in " + armed[0] + " - de acum se sare peste ea.");
            }
        }
        else if (armed.Count > 1)
        {
            // Nu se sare peste niciuna: ar insemna sa pierdem pana la BATCH-1 metode nevinovate pentru
            // una singura. Se scrie transa deoparte ca rularea urmatoare sa o ia cu CPP2IL_SUBST_BATCH=1.
            File.WriteAllLines(Path.Combine(_directory, "subst-bisect.txt"), armed);
            _log("Rularea trecuta a murit cu " + armed.Count + " metode armate. Au fost scrise in subst-bisect.txt;");
            _log("reia-le cu CPP2IL_SUBST_BATCH=1 ca sa iasa la iveala care dintre ele este vinovata.");
        }

        return skip;
    }

    private static void WritePlan(string listPath)
    {
        var entries = new List<SubstEntry>();
        var drops = new Dictionary<string, int>(StringComparer.Ordinal);
        var byTier = new Dictionary<SubstTier, int>();

        foreach (var dll in Directory.GetFiles(_dllDirectory, "*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(dll);
            if (!MatchesFilter(name))
                continue;

            var recovered = _recovered.Index(name);
            if (recovered.Count == 0)
            {
                _log("  " + name + ": niciun tip incarcabil din assembly-ul recuperat.");
                continue;
            }

            var planned = SubstPlanner.Plan(name, recovered, _gameIndex, _tiers, drops);
            entries.AddRange(planned);
            _log("  " + name + ": " + recovered.Count + " metode recuperate, " + planned.Count + " candidate.");
        }

        foreach (var entry in entries)
        {
            byTier.TryGetValue(entry.Tier, out var count);
            byTier[entry.Tier] = count + 1;
        }

        using (var writer = new StreamWriter(listPath, false))
        {
            writer.WriteLine("# lista de lucru pentru faza 3. o linie = tier <TAB> assembly <TAB> cheie.");
            writer.WriteLine("# tier 0 = fara nicio traducere de tipuri; tier 1 = numai receptorul, prin ReceiverTransfer.");
            writer.WriteLine("# stergerea unei linii scoate metoda din rulare; fisierul se rescrie doar cu CPP2IL_SUBST_PLAN=1.");
            foreach (var entry in entries)
                writer.WriteLine(entry.ToLine());
        }

        _log("Plan: " + entries.Count + " candidate scrise in " + listPath);
        foreach (var tier in byTier)
            _log("  treapta " + (int)tier.Key + " (" + tier.Key + "): " + tier.Value);
        foreach (var drop in drops)
            _log("  respinse - " + drop.Key + ": " + drop.Value);
    }

    private static bool MatchesFilter(string name)
    {
        if (_filter.Length == 0)
            return true;

        foreach (var piece in _filter.Split(','))
        {
            var trimmed = piece.Trim();
            if (trimmed.Length > 0 && name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    // ------------------------------------------------------------------------------------------------
    // Masina de stari: armeaza o transa, o lasa sa stea, o dezarmeaza, trece mai departe
    // ------------------------------------------------------------------------------------------------

    public static bool Finished => _finished;

    public static void Tick()
    {
        if (_finished || _queue == null)
            return;

        if (_current.Count > 0)
        {
            if (!TransaReady())
                return;

            Disarm();
            return;
        }

        if (_queueAt >= _queue.Count)
        {
            Summarise();
            _finished = true;
            return;
        }

        Arm();
    }

    private static bool TransaReady()
    {
        var elapsed = Environment.TickCount - _armedAtTick;
        if (elapsed >= _dwellSeconds * 1000)
            return true;

        foreach (var binding in _current)
            if (binding.Calls < _samples)
                return false;

        // Toate si-au strans esantioanele inainte de termen - nu mai are rost sa stea carligate.
        return true;
    }

    private static void Arm()
    {
        _current = new List<Binding>();
        var armedKeys = new List<string>();

        while (_current.Count < _batch && _queueAt < _queue.Count)
        {
            var entry = _queue[_queueAt++];
            var binding = Bind(entry);

            if (binding == null)
                continue;

            armedKeys.Add(entry.Key);
            _current.Add(binding);
        }

        if (_current.Count == 0)
            return;

        // Jurnalul INAINTE de Patch, nu dupa: un detour nativ pus gresit poate lua procesul instantaneu,
        // si atunci singura urma ramasa este fisierul asta.
        File.WriteAllLines(Path.Combine(_directory, InflightFile), armedKeys);

        foreach (var binding in _current)
            Patch(binding);

        _armedAtTick = Environment.TickCount;
    }

    private static Binding Bind(SubstEntry entry)
    {
        if (!_gameIndex.TryGetValue(entry.Key, out var gameMethod))
        {
            Record(entry, null, "NO_GAME", "cheia nu mai este in indexul jocului");
            return null;
        }

        // Plasa de siguranta pentru nota de la Prepare: daca metoda gasita in indexul "jocului" vine de
        // fapt dintr-un assembly incarcat de noi, carligul ar compara codul recuperat cu el insusi.
        if (_recovered.Owns(gameMethod.DeclaringType?.Assembly))
        {
            Record(entry, null, "NO_GAME", "indexul a prins assembly-ul recuperat, nu pe al jocului");
            return null;
        }

        var recoveredIndex = _recovered.Index(entry.Assembly);
        if (!recoveredIndex.TryGetValue(entry.Key, out var recoveredMethod))
        {
            Record(entry, null, "NO_RECOVERED", "cheia nu este in assembly-ul recuperat " + entry.Assembly);
            return null;
        }

        return new Binding
        {
            Entry = entry,
            GameMethod = gameMethod,
            Recovered = recoveredMethod,
            RecoveredDeclaring = recoveredMethod.DeclaringType,
            Parameters = recoveredMethod.GetParameters(),
            WriteBack = _writeBack && SubstSafety.MaySubstitute(entry.Assembly, entry.Type ?? recoveredMethod.DeclaringType?.FullName ?? ""),
        };
    }

    private static void Patch(Binding binding)
    {
        var returnType = (binding.GameMethod as MethodInfo)?.ReturnType;
        var patch = PatchFor(returnType, !binding.GameMethod.IsStatic);

        if (patch == null)
        {
            Record(binding.Entry, binding, "NOT_PATCHED", "nu exista postfix pentru tipul intors");
            return;
        }

        lock (Gate)
            Armed[binding.GameMethod] = binding;

        try
        {
            _harmony.Patch(binding.GameMethod, postfix: new HarmonyMethod(patch));
            binding.Patched = true;
            binding.Patch = patch;
        }
        catch (Exception ex)
        {
            lock (Gate)
                Armed.Remove(binding.GameMethod);

            Record(binding.Entry, binding, "NOT_PATCHED", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void Disarm()
    {
        foreach (var binding in _current)
        {
            if (binding.Patched)
            {
                try
                {
                    _harmony.Unpatch(binding.GameMethod, binding.Patch);
                }
                catch (Exception ex)
                {
                    binding.NoteProblem("descarligare: " + ex.GetType().Name);
                }
            }

            lock (Gate)
                Armed.Remove(binding.GameMethod);

            if (binding.Patched)
                Record(binding.Entry, binding, Verdict(binding), binding.FirstProblem);
        }

        _done += _current.Count;
        _current = new List<Binding>();
        File.Delete(Path.Combine(_directory, InflightFile));

        if (_done % 25 < _batch)
            _log("  " + _queueAt + " / " + _queue.Count + " metode incercate...");
    }

    private static string Verdict(Binding binding)
    {
        if (binding.Calls == 0)
            return "NEVER_CALLED";

        if (binding.Disagree > 0)
            return "DISAGREES";

        if (binding.Agree == 0)
            return "THREW";

        return binding.TransferIncomplete > 0 ? "AGREES_PARTIAL_STATE" : "AGREES";
    }

    // ------------------------------------------------------------------------------------------------
    // Comparatia propriu-zisa
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Miezul, chemat din fiecare postfix. Intoarce valoarea RECUPERATA doar cand modul substitute a
    /// castigat dreptul sa scrie inapoi; altfel null, iar raspunsul jocului ramane neatins.
    /// </summary>
    private static object Core(MethodBase original, object instance, object[] args, object realResult)
    {
        // Reentranta ar fi fatala: transferul de stare cheama getteri ai jocului, iar un getter carligat
        // ar intra din nou aici si ar recurge pana la depasirea stivei - adica un crash care ar arata ca
        // o metoda ucigasa, cand de fapt este unealta care se musca de coada.
        if (_inside || original == null)
            return null;

        Binding binding;
        lock (Gate)
        {
            if (!Armed.TryGetValue(original, out binding))
            {
                // __originalMethod ar trebui sa fie chiar MethodBase-ul pe care i l-am dat lui Patch, dar
                // pe metodele Il2Cpp drumul trece prin Il2CppDetourMethodPatcher si nu avem cum sa dovedim
                // asta fara sa rulam. Daca nu se potriveste, cu o singura metoda armata raspunsul este
                // oricum neambiguu - si asa un mecanism care ar fi raportat totul ca "NEVER_CALLED"
                // raporteaza in schimb adevarul, plus o linie in log care spune ca s-a intamplat.
                if (Armed.Count != 1)
                    return null;

                foreach (var only in Armed.Values)
                    binding = only;

                if (!_warnedAboutIdentity)
                {
                    _warnedAboutIdentity = true;
                    _log("Nota: __originalMethod nu se potriveste cu metoda carligata; merg dupa singura metoda armata.");
                }
            }
        }

        if (binding.Calls >= _samples)
            return null;

        _inside = true;
        try
        {
            return Compare(binding, instance, args, realResult);
        }
        catch (Exception ex)
        {
            binding.Threw++;
            binding.NoteProblem("harnas: " + ex.GetType().Name);
            return null;
        }
        finally
        {
            _inside = false;
        }
    }

    private static object Compare(Binding binding, object instance, object[] args, object realResult)
    {
        binding.Calls++;

        object receiver = null;
        if (binding.Entry.Tier == SubstTier.Receiver)
        {
            if (instance == null)
            {
                binding.Threw++;
                binding.NoteProblem("receptor null la punctul de apel");
                return null;
            }

            var report = new TransferReport();
            receiver = ReceiverTransfer.Build(instance, binding.RecoveredDeclaring, report);
            binding.CopiedFields += report.Copied;
            binding.UnmatchedFields += report.Total - report.Copied;

            if (!report.Complete)
            {
                binding.TransferIncomplete++;
                binding.NoteProblem(report.FirstProblem);
            }
        }

        var count = binding.Parameters.Length;
        var coerced = new object[count];
        for (var i = 0; i < count; i++)
        {
            var supplied = args != null && i < args.Length ? args[i] : null;
            if (!ReceiverTransfer.Coerce(supplied, binding.Parameters[i].ParameterType, out coerced[i]))
            {
                binding.Threw++;
                binding.NoteProblem("argument netradus " + i);
                return null;
            }
        }

        object recoveredResult;
        try
        {
            recoveredResult = binding.Recovered.Invoke(receiver, coerced);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            binding.Threw++;
            binding.NoteProblem(inner.GetType().Name);
            return null;
        }

        if (!SameBits(recoveredResult, realResult))
        {
            binding.Disagree++;
            binding.ConsecutiveAgree = 0;
            if (binding.FirstDisagreement.Length == 0)
                binding.FirstDisagreement = "joc=" + Show(realResult) + " recuperat=" + Show(recoveredResult);

            return null;
        }

        binding.Agree++;
        binding.ConsecutiveAgree++;

        // Scrisul inapoi vine ULTIMUL si numai dupa un sir de potriviri, dinadins. Cand cele doua valori
        // sunt egale, inlocuirea nu schimba nimic in joc - si tocmai asta este dovada ca mecanismul poate
        // fi pornit fara sa riste nimic. Cand ele difera, refuzam sa inlocuim: o valoare despre care
        // stim ca este alta n-are ce cauta in joc.
        if (!binding.WriteBack || binding.ConsecutiveAgree < _promote)
            return null;

        binding.Substituted++;
        return recoveredResult;
    }

    /// <summary>
    /// Egalitate pe BITI, nu pe valoare. Acelasi prag ca hash-ul fazei 1/2: un float care difera pe
    /// ultimul bit este o diferenta, si trebuie sa se vada ca atare, nu sa fie inghitita de o toleranta
    /// pe care nimeni nu a ales-o.
    /// </summary>
    private static bool SameBits(object a, object b)
    {
        if (a == null || b == null)
            return ReferenceEquals(a, b);

        var left = Unwrap(a);
        var right = Unwrap(b);

        if (left is double dl && right is double dr)
            return BitConverter.DoubleToInt64Bits(dl) == BitConverter.DoubleToInt64Bits(dr);

        if (left is float fl && right is float fr)
            return BitConverter.ToInt32(BitConverter.GetBytes(fl), 0) == BitConverter.ToInt32(BitConverter.GetBytes(fr), 0);

        if (left.GetType() != right.GetType())
        {
            try
            {
                right = Convert.ChangeType(right, left.GetType(), CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return false;
            }
        }

        return left.Equals(right);
    }

    // Un enum recuperat si unul al jocului sunt tipuri CLR diferite chiar cand poarta acelasi nume, deci
    // comparatia trebuie sa coboare la intregul de dedesubt - acelasi drum ca in ValueShape.Absorb.
    private static object Unwrap(object value)
    {
        var type = value.GetType();
        return type.IsEnum ? Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture) : value;
    }

    // "R" pentru virgula mobila: o diferenta pe ultimul bit trebuie sa se VADA in raport, altfel un
    // dezacord de rotunjire si unul de logica arata identic.
    private static string Show(object value) => value switch
    {
        null => "(null)",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.GetType().Name,
    };

    // ------------------------------------------------------------------------------------------------
    // Rezultatele
    // ------------------------------------------------------------------------------------------------

    private static void Record(SubstEntry entry, Binding binding, string verdict, string detail)
    {
        var builder = new StringBuilder();
        builder.Append(Clean(entry.Key)).Append(Sep);
        builder.Append(Clean(entry.Assembly)).Append(Sep);
        builder.Append((int)entry.Tier).Append(Sep);
        builder.Append(_writeBack ? "substitute" : "shadow").Append(Sep);
        builder.Append(verdict).Append(Sep);
        builder.Append(binding?.Calls ?? 0).Append(Sep);
        builder.Append(binding?.Agree ?? 0).Append(Sep);
        builder.Append(binding?.Disagree ?? 0).Append(Sep);
        builder.Append(binding?.Threw ?? 0).Append(Sep);
        builder.Append(binding?.TransferIncomplete ?? 0).Append(Sep);
        builder.Append(binding?.CopiedFields ?? 0).Append(Sep);
        builder.Append(binding?.UnmatchedFields ?? 0).Append(Sep);
        builder.Append(binding?.Substituted ?? 0).Append(Sep);
        builder.Append(Clean(binding?.FirstDisagreement ?? "")).Append(Sep);
        builder.Append(Clean(detail ?? ""));

        File.AppendAllText(Path.Combine(_directory, ResultsFile), builder.ToString() + Environment.NewLine);
    }

    private static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        return text.Replace(Sep, ' ').Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }

    private static void Summarise()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = Path.Combine(_directory, ResultsFile);

        if (File.Exists(path))
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var fields = line.Split(Sep);
                if (fields.Length < 5)
                    continue;

                counts.TryGetValue(fields[4], out var count);
                counts[fields[4]] = count + 1;
            }
        }

        var builder = new StringBuilder();
        builder.AppendLine("faza 3 - " + (_writeBack ? "substitute" : "shadow"));
        builder.AppendLine("dll: " + _dllDirectory);
        builder.AppendLine("incercate: " + _queueAt + " din " + (_queue?.Count ?? 0));
        foreach (var pair in counts)
            builder.AppendLine("  " + pair.Key + ": " + pair.Value);

        File.WriteAllText(Path.Combine(_directory, SummaryFile), builder.ToString());
        _log(builder.ToString());
        _log("SUBST_DONE");
    }

    // ------------------------------------------------------------------------------------------------
    // Carligele
    //
    // Harmony citeste NUMELE parametrilor, deci fiecare postfix trebuie sa aiba semnatura potrivita
    // metodei carligate: "ref T __result" cu T exact tipul intors, si "__instance" numai la metodele de
    // instanta. De aici perechile de mai jos - plictisitoare, dar scrise de mana dinadins: varianta
    // eleganta ar fi un DynamicMethod, iar un postfix emis la rulare care se dovedeste gresit nu da o
    // exceptie, da un proces mort.
    //
    // Tipurile intoarse care nu sunt printre cele douasprezece primitive (enum-uri, structuri) merg pe
    // perechea ObserveObject, cu "object __result" prin VALOARE: acolo se poate doar observa, fiindca un
    // "ref object" peste un tip valoare nu este IL valid.
    // ------------------------------------------------------------------------------------------------

    private static MethodInfo PatchFor(Type returnType, bool instance)
    {
        if (returnType == null || returnType == typeof(void))
            return null;

        var suffix = returnType == typeof(bool) ? "Boolean"
            : returnType == typeof(char) ? "Char"
            : returnType == typeof(sbyte) ? "SByte"
            : returnType == typeof(byte) ? "Byte"
            : returnType == typeof(short) ? "Int16"
            : returnType == typeof(ushort) ? "UInt16"
            : returnType == typeof(int) ? "Int32"
            : returnType == typeof(uint) ? "UInt32"
            : returnType == typeof(long) ? "Int64"
            : returnType == typeof(ulong) ? "UInt64"
            : returnType == typeof(float) ? "Single"
            : returnType == typeof(double) ? "Double"
            : "Object";

        var name = (instance ? "PostfixI" : "PostfixS") + suffix;
        return typeof(SubstitutionHarness).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
    }

    private static void PostfixSBoolean(object[] __args, ref bool __result, MethodBase __originalMethod) { if (Core(__originalMethod, null, __args, __result) is bool v) __result = v; }
    private static void PostfixIBoolean(object __instance, object[] __args, ref bool __result, MethodBase __originalMethod) { if (Core(__originalMethod, __instance, __args, __result) is bool v) __result = v; }
    private static void PostfixSChar(object[] __args, ref char __result, MethodBase __originalMethod) { if (Core(__originalMethod, null, __args, __result) is char v) __result = v; }
    private static void PostfixIChar(object __instance, object[] __args, ref char __result, MethodBase __originalMethod) { if (Core(__originalMethod, __instance, __args, __result) is char v) __result = v; }
    private static void PostfixSSByte(object[] __args, ref sbyte __result, MethodBase __originalMethod) { if (Core(__originalMethod, null, __args, __result) is sbyte v) __result = v; }
    private static void PostfixISByte(object __instance, object[] __args, ref sbyte __result, MethodBase __originalMethod) { if (Core(__originalMethod, __instance, __args, __result) is sbyte v) __result = v; }
    private static void PostfixSByte(object[] __args, ref byte __result, MethodBase __originalMethod) { if (Core(__originalMethod, null, __args, __result) is byte v) __result = v; }
    private static void PostfixIByte(object __instance, object[] __args, ref byte __result, MethodBase __originalMethod) { if (Core(__originalMethod, __instance, __args, __result) is byte v) __result = v; }
    private static void PostfixSInt16(object[] __args, ref short __result, MethodBase __originalMethod) { if (Core(__originalMethod, null, __args, __result) is short v) __result = v; }
    private static void PostfixIInt16(object __instance, object[] __args, ref short __result, MethodBase __originalMethod) { if (Core(__originalMethod, __instance, __args, __result) is short v) __result = v; }
    private static void PostfixSUInt16(object[] __args, ref ushort __result, MethodBase __originalMethod) { if (Core(__originalMethod, null, __args, __result) is ushort v) __result = v; }
    private static void PostfixIUInt16(object __instance, object[] __args, ref ushort __result, MethodBase __originalMethod) { if (Core(__originalMethod, __instance, __args, __result) is ushort v) __result = v; }
    private static void PostfixSInt32(object[] __args, ref int __result, MethodBase __originalMethod) { if (Core(__originalMethod, null, __args, __result) is int v) __result = v; }
    private static void PostfixIInt32(object __instance, object[] __args, ref int __result, MethodBase __originalMethod) { if (Core(__originalMethod, __instance, __args, __result) is int v) __result = v; }
    private static void PostfixSUInt32(object[] __args, ref uint __result, MethodBase __originalMethod) { if (Core(__originalMethod, null, __args, __result) is uint v) __result = v; }
    private static void PostfixIUInt32(object __instance, object[] __args, ref uint __result, MethodBase __originalMethod) { if (Core(__originalMethod, __instance, __args, __result) is uint v) __result = v; }
    private static void PostfixSInt64(object[] __args, ref long __result, MethodBase __originalMethod) { if (Core(__originalMethod, null, __args, __result) is long v) __result = v; }
    private static void PostfixIInt64(object __instance, object[] __args, ref long __result, MethodBase __originalMethod) { if (Core(__originalMethod, __instance, __args, __result) is long v) __result = v; }
    private static void PostfixSUInt64(object[] __args, ref ulong __result, MethodBase __originalMethod) { if (Core(__originalMethod, null, __args, __result) is ulong v) __result = v; }
    private static void PostfixIUInt64(object __instance, object[] __args, ref ulong __result, MethodBase __originalMethod) { if (Core(__originalMethod, __instance, __args, __result) is ulong v) __result = v; }
    private static void PostfixSSingle(object[] __args, ref float __result, MethodBase __originalMethod) { if (Core(__originalMethod, null, __args, __result) is float v) __result = v; }
    private static void PostfixISingle(object __instance, object[] __args, ref float __result, MethodBase __originalMethod) { if (Core(__originalMethod, __instance, __args, __result) is float v) __result = v; }
    private static void PostfixSDouble(object[] __args, ref double __result, MethodBase __originalMethod) { if (Core(__originalMethod, null, __args, __result) is double v) __result = v; }
    private static void PostfixIDouble(object __instance, object[] __args, ref double __result, MethodBase __originalMethod) { if (Core(__originalMethod, __instance, __args, __result) is double v) __result = v; }

    // Fara "ref": aici nu se poate inlocui nimic, doar observa. Vezi nota de deasupra lui PatchFor.
    private static void PostfixSObject(object[] __args, object __result, MethodBase __originalMethod) { Core(__originalMethod, null, __args, __result); }
    private static void PostfixIObject(object __instance, object[] __args, object __result, MethodBase __originalMethod) { Core(__originalMethod, __instance, __args, __result); }

    private sealed class Binding
    {
        public SubstEntry Entry;
        public MethodBase GameMethod;
        public MethodBase Recovered;
        public Type RecoveredDeclaring;
        public ParameterInfo[] Parameters;
        public bool WriteBack;
        public bool Patched;
        public MethodInfo Patch;

        public int Calls;
        public int Agree;
        public int Disagree;
        public int Threw;
        public int TransferIncomplete;
        public int CopiedFields;
        public int UnmatchedFields;
        public int Substituted;
        public int ConsecutiveAgree;
        public string FirstDisagreement = "";
        public string FirstProblem = "";

        public void NoteProblem(string problem)
        {
            if (FirstProblem.Length == 0 && !string.IsNullOrEmpty(problem))
                FirstProblem = problem;
        }
    }
}
