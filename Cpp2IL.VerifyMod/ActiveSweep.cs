using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Cpp2IL.VerifyCore;

namespace Cpp2IL.VerifyMod;

/// <summary>
/// Faza 4: maturare ACTIVA. Nu se mai asteapta ca jocul sa cheme o metoda - o chemam noi, pe amandoua
/// partile, in acelasi proces.
///
/// De ce exista, in numere masurate. Fuzzing-ul diferential (fazele 1 si 2) compara doua procese si de
/// aceea are nevoie ca argumentele sa poata fi cladite identic in amandoua: 1.232 de metode verificate din
/// 36.546, adica 3,4%. Substitutia in umbra (faza 3) rezolva problema receptorului dar depinde de noroc -
/// intr-o sesiune, 8 verificate si 22 NEVER_CALLED, fiindca jocul pur si simplu nu le-a chemat. Faza de
/// fata scoate amandoua limitele: argumentele nu mai trebuie sa treaca o granita de proces, si nu se mai
/// asteapta pe nimeni.
///
/// Ce se castiga cu adevarat, spus fara infrumusetare. Argumentele primitive, enum-urile si structurile
/// sunt IDENTICE bit cu bit pe cele doua parti, fiindca sunt generate o data si materializate in fiecare
/// dintre cele doua tipuri din aceleasi frunze. Argumentele de tip clasa sunt null pe amandoua partile,
/// deci tot identice. Receptorul NU poate fi acelasi obiect - motivul intreg este scris in NativeReceiver -
/// dar poate fi si este ECHIVALENT: pe ambele parti un obiect proaspat cu toate campurile pe zero. Deci
/// intrarile sunt egale prin constructie chiar si acolo unde nu sunt egale prin referinta.
///
/// Ce NU dovedeste, si trebuie citit langa orice numar pe care il scoate. O metoda chemata cu receptor pe
/// zero si argumente null are toate sansele sa iasa pe prima ramura, aceeasi pe ambele parti. Cand asta se
/// intampla, "AGREES" inseamna doar ca amandoua au refuzat intrarea in acelasi fel. De aceea fiecare rand
/// isi poarta calitatea argumentelor, de aceea raportul NU are un singur numar, si de aceea verdictele
/// slabe au nume propriu in loc sa fie varsate peste cele tari.
///
/// Variabile de mediu - oprita implicit, ca tot ce ruleaza cod neverificat in joc:
///
///   CPP2IL_ACTIVE=1            porneste faza 4
///   CPP2IL_ACTIVE_DLLS=&lt;dir&gt;   directorul cu DLL-urile recuperate (obligatoriu)
///   CPP2IL_ACTIVE_DUMP_ONLY=1  scrie numai dump-ul de cerinte si se opreste, fara niciun apel
///   CPP2IL_ACTIVE_REDUMP=1     rescrie dump-ul chiar daca exista (dupa o recompilare a codului recuperat)
///   CPP2IL_ACTIVE_MAX=N        cate metode se incearca in sesiunea asta (implicit 2000; 0 = toate)
///   CPP2IL_ACTIVE_PER_FRAME=N  cate apeluri pe cadru (implicit 25)
///   CPP2IL_ACTIVE_FILTER=a,b   fragmente de nume de assembly de INCLUS (implicit toate cele din univers)
///   CPP2IL_ACTIVE_SKIP=a,b     fragmente de nume de assembly de SARIT (de pilda Rewired)
///   CPP2IL_ACTIVE_SAFETY=0     opreste lista de siguranta - de folosit numai cu jocul deconectat
///   CPP2IL_ACTIVE_RECEIVERS=0  numai metode statice, niciun receptor fabricat
///   CPP2IL_ACTIVE_PREJIT=0     nu mai compileaza corpul recuperat inainte de apel (implicit compileaza)
///   CPP2IL_ACTIVE_ENUM_DOMAIN=0  enum-urile se fuzzeaza pe tot intervalul intregului, nu pe valorile declarate
///   CPP2IL_ACTIVE_PREPARE_PASS=0 fara trecere de pregatire separata (pregatirea ramane doar per metoda)
///   CPP2IL_ACTIVE_PREPARE_ALL=1  pregateste TOATE metodele chemabile, nu doar pe cele pe care le cheama sesiunea
///   CPP2IL_ACTIVE_PREPARE_ONLY=1 se opreste dupa pregatire, fara niciun apel - asa se cladeste lista permanenta
///   CPP2IL_ACTIVE_SEED=N       samanta generatorului (implicit 1)
///
/// Rezultatele se aduna in active-results.tsv si nu se rescriu niciodata: o sesiune noua sare peste cheile
/// deja masurate, deci universul se poate parcurge in reprize.
/// </summary>
internal static class ActiveSweep
{
    private const string ResultsFile = "active-results.tsv";
    private const string InflightFile = "active-inflight.txt";
    private const string SkipFile = "active-skip.txt";
    private const string SummaryFile = "active-summary.txt";

    private const char Sep = '\u001f';

    // Coloanele dump-ului, citite inapoi ca sa se cladeasca lista de lucru. Numerotate aici si nicaieri
    // altundeva: daca antetul din MethodRequirements se muta, se muta un singur loc.
    private const int ColKey = 0;
    private const int ColAssembly = 1;
    private const int ColType = 2;
    private const int ColStatic = 5;
    private const int ColReturnPlan = 12;
    private const int ColCallable = 15;
    private const int ColQuality = 16;

    // Verdictele. Scrise ca text in fisier fiindca fisierul este citit de oameni si de grep, nu de un
    // parser care ar sti sa traduca un numar inapoi.
    private const string VerdictAgrees = "AGREES";
    private const string VerdictAgreesWeak = "AGREES_WEAK";
    private const string VerdictDisagrees = "DISAGREES";
    private const string VerdictThrewBoth = "THREW_BOTH";
    private const string VerdictThrewOne = "THREW_ONE";
    private const string VerdictNoReturn = "NO_RETURN";
    private const string VerdictNotAttempted = "NOT_ATTEMPTED";
    private const string VerdictCrashed = "CRASHED";
    private const string VerdictIlInvalid = "IL_INVALID";

    // Etapa la care se afla maturarea cand scrie jurnalul. Exista pentru un singur motiv, dar unul care
    // hotaraste orice reparatie de aici incolo: "Fatal error. Internal CLR error. (0x80131506)" apare cu
    // stiva la ActiveSweep.Call, iar prin Call trec AMANDOUA implementarile. Fara etapa in jurnal nu se
    // poate spune daca procesul a fost omorat de corpul recuperat sau de metoda NATIVA a jocului chemata
    // cu argumente fabricate - iar cele doua cer reparatii care nu au nimic in comun.
    private const string StageResolve = "resolve";
    private const string StagePrepare = "prepare-ours";
    private const string StageCallGame = "call-game";
    private const string StageCallOurs = "call-ours";
    private const string StagePreparePass = "prepare-pass";

    private static Action<string> _log = _ => { };
    private static string _directory = ".";
    private static Func<Dictionary<string, MethodBase>> _gameIndexFactory;
    private static Dictionary<string, MethodBase> _gameIndex;
    private static RecoveredCode _recovered;

    private static bool _enabled;
    private static bool _dumpOnly;
    private static bool _safety;
    private static bool _receivers;
    private static bool _preJit;
    private static bool _enumDomain;
    private static bool _preparePass;
    private static bool _prepareAll;
    private static bool _prepareOnly;
    private static string _dllDirectory = "";
    private static string _filter = "";
    private static string _skipAssemblies = "";
    private static int _max;
    private static int _perFrame;
    private static ulong _seed;

    private static List<string[]> _queue;
    private static List<string[]> _prepareQueue;
    private static HashSet<string> _prepareFailed;
    private static int _prepareAt;
    private static int _lastPrepareLogged;
    private static int _preparedOk;
    private static int _prepareUnresolved;
    private static bool _preparing;
    private static int _queueAt;
    private static int _lastLogged;
    private static bool _finished;
    private static FileStream _journal;
    private static HashSet<string> _skip;
    private static readonly Dictionary<string, int> Verdicts = new Dictionary<string, int>(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> NotAttemptedReasons = new Dictionary<string, int>(StringComparer.Ordinal);

    public static bool Enabled => _enabled;
    public static bool Finished => _finished;

    /// <summary>
    /// Ceruta prin mediu, chiar daca pe urma nu a putut porni - aceeasi distinctie ca la faza 3 si pentru
    /// acelasi motiv: o faza ceruta si cazuta la configurare nu are voie sa lase alta faza sa porneasca in
    /// locul ei.
    /// </summary>
    public static bool Requested => Environment.GetEnvironmentVariable("CPP2IL_ACTIVE") == "1";

    public static bool Configure(string directory, Action<string> log, Func<Dictionary<string, MethodBase>> gameIndexFactory)
    {
        _enabled = Requested;
        if (!_enabled)
            return false;

        _directory = directory;
        _log = log;
        _gameIndexFactory = gameIndexFactory;

        _dllDirectory = Environment.GetEnvironmentVariable("CPP2IL_ACTIVE_DLLS") ?? "";
        _dumpOnly = Environment.GetEnvironmentVariable("CPP2IL_ACTIVE_DUMP_ONLY") == "1";
        _filter = Environment.GetEnvironmentVariable("CPP2IL_ACTIVE_FILTER") ?? "";
        _skipAssemblies = Environment.GetEnvironmentVariable("CPP2IL_ACTIVE_SKIP") ?? "";

        // Siguranta si receptorii sunt PORNITI implicit, deci "!= 0". Restul comutatoarelor sunt oprite
        // implicit, deci "== 1". Regula casei, si aici are si un inteles: ca sa scoti plasa de siguranta
        // trebuie sa o ceri anume.
        _safety = Environment.GetEnvironmentVariable("CPP2IL_ACTIVE_SAFETY") != "0";
        _receivers = Environment.GetEnvironmentVariable("CPP2IL_ACTIVE_RECEIVERS") != "0";
        _preJit = Environment.GetEnvironmentVariable("CPP2IL_ACTIVE_PREJIT") != "0";
        _enumDomain = Environment.GetEnvironmentVariable("CPP2IL_ACTIVE_ENUM_DOMAIN") != "0";
        _preparePass = Environment.GetEnvironmentVariable("CPP2IL_ACTIVE_PREPARE_PASS") != "0";
        _prepareAll = Environment.GetEnvironmentVariable("CPP2IL_ACTIVE_PREPARE_ALL") == "1";
        _prepareOnly = Environment.GetEnvironmentVariable("CPP2IL_ACTIVE_PREPARE_ONLY") == "1";

        _max = Number("CPP2IL_ACTIVE_MAX", 2000, allowZero: true);
        _perFrame = Number("CPP2IL_ACTIVE_PER_FRAME", 25, allowZero: false);
        _seed = (ulong)Number("CPP2IL_ACTIVE_SEED", 1, allowZero: false);

        if (_dllDirectory.Length == 0 || !Directory.Exists(_dllDirectory))
        {
            _log("CPP2IL_ACTIVE=1 dar CPP2IL_ACTIVE_DLLS nu arata catre un director existent - faza 4 nu porneste.");
            _enabled = false;
            return false;
        }

        _log("Faza 4 (maturare activa) pornita. dll=" + _dllDirectory
            + " max=" + (_max == 0 ? "toate" : _max.ToString(CultureInfo.InvariantCulture))
            + " pe-cadru=" + _perFrame + " siguranta=" + (_safety ? "pornita" : "OPRITA")
            + " receptori=" + (_receivers ? "da" : "nu"));

        if (!_safety)
            _log("ATENTIE: lista de siguranta este oprita. Se vor chema si metode de plati, cont si retea.");

        return true;
    }

    private static int Number(string name, int fallback, bool allowZero)
    {
        var text = Environment.GetEnvironmentVariable(name);
        if (text == null || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return fallback;

        if (value < 0 || (value == 0 && !allowZero))
            return fallback;

        return value;
    }

    // --------------------------------------------------------------------------------------------
    // Pregatirea
    // --------------------------------------------------------------------------------------------

    public static void Prepare()
    {
        _skip = ReadJournal();

        // Ordinea NU se poate schimba: indexul jocului se cladeste inainte ca vreun assembly recuperat sa
        // fie incarcat. Indexarea merge peste AppDomain.CurrentDomain.GetAssemblies(), iar assembly-urile
        // din contextul nostru apar si ele acolo - un Assembly-CSharp recuperat incarcat mai devreme ar
        // intra in index sub exact aceleasi chei si am compara codul recuperat cu el insusi, care este
        // perfect de acord si nu inseamna nimic.
        _gameIndex = _gameIndexFactory();
        _log("Indexul jocului: " + _gameIndex.Count + " metode.");

        _recovered = new RecoveredCode(_dllDirectory);

        var dumpPath = Path.Combine(_directory, MethodRequirements.FileName);

        // Dump-ul se scrie inainte de PRIMUL apel si nu se rescrie la fiecare sesiune. Motivul pentru care
        // se refoloseste cand exista deja este memoria: scrierea lui incarca toate cele ~54 de assembly-uri
        // recuperate ca sa le citeasca metadatele, iar o sesiune de reluare care maturaza doua sute de
        // metode dintr-un singur assembly nu are de ce sa plateasca din nou pretul acela langa un joc care
        // foloseste deja cea mai mare parte din cei 16 GB. CPP2IL_ACTIVE_REDUMP=1 il forteaza rescris dupa
        // o recompilare a codului recuperat.
        var redump = Environment.GetEnvironmentVariable("CPP2IL_ACTIVE_REDUMP") == "1";

        // Un dump vechi langa o normalizare noua este cel mai scump fel de a pierde o sesiune: cheile
        // scrise in fisier nu mai sunt cheile pe care le da MethodKeys acum, deci FIECARE metoda din
        // lista de lucru s-ar raporta ca "cheia a disparut din indexul jocului" si jocul ar fi pornit
        // degeaba. Semnul de langa dump spune cu ce forma de cheie a fost scris; cand nu se potriveste,
        // dump-ul se reface fara sa mai astepte cineva sa puna CPP2IL_ACTIVE_REDUMP=1.
        var stampPath = dumpPath + ".keyver";
        var stamped = File.Exists(stampPath) ? File.ReadAllText(stampPath).Trim() : "";
        var stale = File.Exists(dumpPath) && stamped != MethodKeys.FormatVersion;

        if (stale)
            _log("Dump-ul existent a fost scris cu alta forma de cheie (" + (stamped.Length > 0 ? stamped : "fara semn") + " != " + MethodKeys.FormatVersion + "); se reface.");

        if (redump || stale || !File.Exists(dumpPath))
        {
            WriteRequirements(dumpPath);
            File.WriteAllText(stampPath, MethodKeys.FormatVersion);
        }
        else
        {
            _log("Dump-ul de cerinte exista deja (" + MethodRequirements.FileName + "); se refoloseste. CPP2IL_ACTIVE_REDUMP=1 il rescrie.");
        }

        if (_dumpOnly)
        {
            _log("CPP2IL_ACTIVE_DUMP_ONLY=1 - dump-ul este scris, nu se cheama nicio metoda.");
            _finished = true;
            return;
        }

        BuildQueue(dumpPath);
    }

    /// <summary>
    /// Dump-ul de cerinte, un assembly pe rand si scris pe masura ce se citeste.
    ///
    /// Se scrie in flux si nu se aduna in memorie dinadins: universul are ~59.000 de metode in 54 de
    /// assembly-uri, iar o lista de obiecte de descriere pentru toate, tinuta langa un joc care foloseste
    /// deja cea mai mare parte din cei 16 GB, este exact felul in care recensamantul de pe desktop a fost
    /// omorat de memorie. Lista de lucru se recladeste dupa aceea CITIND fisierul inapoi, deci nimic nu
    /// trebuie tinut viu intre cele doua etape.
    /// </summary>
    private static void WriteRequirements(string dumpPath)
    {
        var files = Directory.GetFiles(_dllDirectory, "*.dll");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        var skipSet = TargetUniverse.Split(_skipAssemblies);
        var rows = 0;
        var assemblies = 0;
        var byClass = new Dictionary<AssemblyClass, int>();
        var excluded = new List<string>();

        using (var writer = new StreamWriter(dumpPath, false))
        {
            writer.WriteLine(MethodRequirements.HeaderLine());

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var universe = TargetUniverse.Classify(name);

                Bump(byClass, universe);

                if (universe != AssemblyClass.InHouse)
                {
                    excluded.Add(name + " (" + universe + ")");
                    continue;
                }

                if (!TargetUniverse.Matches(name, _filter) || Skipped(name, skipSet))
                {
                    excluded.Add(name + " (filtru)");
                    continue;
                }

                Dictionary<string, MethodBase> index;
                try
                {
                    index = _recovered.Index(name);
                }
                catch (Exception ex)
                {
                    _log("  " + name + ": indexarea a aruncat " + ex.GetType().Name);
                    continue;
                }

                if (index.Count == 0)
                    continue;

                assemblies++;

                foreach (var pair in index)
                {
                    _gameIndex.TryGetValue(pair.Key, out var game);

                    MethodRequirement requirement;
                    try
                    {
                        requirement = MethodRequirements.Describe(name, pair.Value, game, _safety, KeyCollisions.Contains(pair.Key));
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    writer.WriteLine(MethodRequirements.RowLine(requirement));
                    rows++;
                }

                if (assemblies % 5 == 0)
                    _log("  dump: " + rows + " metode din " + assemblies + " assembly-uri...");
            }
        }

        _log("Dump de cerinte: " + rows + " metode din " + assemblies + " assembly-uri -> " + MethodRequirements.FileName);

        // Pretul normalizarii, scris langa castigul ei. Cheia se ingroasa dinadins ca sa ajunga la forma
        // pe care o da Il2CppInterop, iar orice ingrosare poate face ca doua metode diferite sa cada pe
        // aceeasi cheie. Acelea sunt scoase din pereche, si numarul lor trebuie sa se vada: daca sare, o
        // regula de normalizare este prea larga si trebuie stramtata, nu lasata sa produca perechi
        // gresite.
        if (_recovered.Collisions > 0)
            _log("Chei recuperate scoase din pereche (doua metode pe aceeasi cheie): " + _recovered.Collisions);

        foreach (var pair in byClass)
            _log("  assembly-uri " + pair.Key + ": " + pair.Value);

        if (excluded.Count > 0)
            File.WriteAllLines(Path.Combine(_directory, "active-excluded-assemblies.txt"), excluded);
    }

    private static bool Skipped(string assembly, HashSet<string> fragments)
    {
        foreach (var fragment in fragments)
            if (assembly.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

        return false;
    }

    /// <summary>
    /// Lista de lucru, citita inapoi din dump: numai metodele fara niciun blocaj, care nu au fost deja
    /// masurate si care nu sunt in lista de sarituri. Grupata pe assembly ca o sesiune sa atinga putine
    /// assembly-uri si sa nu tina vii toate cele 54 deodata.
    /// </summary>
    private static void BuildQueue(string dumpPath)
    {
        var measured = AlreadyMeasured();
        var candidates = new List<string[]>();
        _prepareFailed = new HashSet<string>(StringComparer.Ordinal);

        var total = 0;
        var blocked = 0;
        var done = 0;
        var skipped = 0;
        var deferred = 0;

        foreach (var line in File.ReadLines(dumpPath))
        {
            var fields = line.Split(Sep);
            if (fields.Length <= ColQuality || fields[ColKey] == "key")
                continue;

            total++;

            if (fields[ColCallable] != "1")
            {
                blocked++;
                continue;
            }

            if (measured.Contains(fields[ColKey]))
            {
                done++;
                continue;
            }

            if (_skip.Contains(fields[ColKey]))
            {
                skipped++;
                continue;
            }

            candidates.Add(fields);
        }

        // Intai STATICELE, si nu din gust pentru ordine. O metoda statica se cheama fara receptor, deci nu
        // atinge niciun obiect fabricat si nu poate cadea din cauza unui camp nul citit dintr-un obiect pe
        // zero. Metodele de instanta sunt cele periculoase: IL2CPP compilat pentru livrare nu mai emite
        // verificari de nul, asa ca o dereferentiere a unui camp zero nu da NullReferenceException, ci o
        // violare de acces care omoara procesul. Cu staticele in fata, primele sesiuni scot rezultate fara
        // sa fie intrerupte, iar caderile incep abia dupa ce partea ieftina este deja in fisier.
        // Coada de APELURI. Cand receptorii sunt opriti, metodele de instanta nu intra deloc in ea - nu se
        // inregistreaza pentru ele un NOT_ATTEMPTED cu motivul "receptorii sunt opriti din configurare".
        // Diferenta nu este cosmetica: un rezultat scris inseamna metoda MASURATA, iar AlreadyMeasured o
        // sare in toate sesiunile urmatoare. Asa, o sesiune statica ar fi ingropat definitiv cele 21.260 de
        // metode de instanta pe care tocmai modul full trebuie sa le masoare, si nimeni nu ar fi observat
        // decat dupa ce modul full ar fi raportat ca nu mai are ce face.
        _queue = new List<string[]>(candidates.Count);
        foreach (var row in candidates)
        {
            if (!_receivers && row[ColStatic] != "1")
            {
                deferred++;
                continue;
            }

            _queue.Add(row);
        }

        _queue.Sort((a, b) =>
        {
            var byStatic = string.CompareOrdinal(b[ColStatic], a[ColStatic]);
            if (byStatic != 0)
                return byStatic;

            var byAssembly = string.CompareOrdinal(a[ColAssembly], b[ColAssembly]);
            return byAssembly != 0 ? byAssembly : string.CompareOrdinal(a[ColKey], b[ColKey]);
        });

        if (_max > 0 && _queue.Count > _max)
            _queue = _queue.GetRange(0, _max);

        // Coada de PREGATIRE. Compilarea nu are nevoie nici de receptor, nici de argumente, deci poate
        // acoperi si metodele pe care sesiunea asta nu le cheama. Acolo este si castigul cerut: o singura
        // trecere cu CPP2IL_ACTIVE_PREPARE_ALL=1 cladeste lista de ucigasi peste TOT ce este chemabil, iar
        // pe urma si modul static si modul full pornesc cu ea gata facuta, in loc sa o descopere fiecare.
        if (_preparePass)
        {
            _prepareQueue = _prepareAll ? candidates : _queue;
            _preparing = _prepareQueue.Count > 0;
        }

        _log("Univers " + total + ": " + blocked + " blocate, " + done + " deja masurate, "
            + skipped + " sarite dupa caderi"
            + (deferred > 0 ? ", " + deferred + " de instanta lasate pentru o sesiune cu receptori" : "")
            + ". Sesiunea asta incearca " + _queue.Count
            + (_preparing ? ", dupa ce pregateste " + _prepareQueue.Count : "") + ".");

        if (_queue.Count == 0 && !_preparing)
        {
            // Nimic de facut nu inseamna nimic de spus: raportul se scrie oricum, fiindca el aduna TOATE
            // sesiunile de pana acum, iar runner-ul are nevoie de el tocmai cand universul s-a terminat.
            Summarise();
            _finished = true;
        }
    }

    private static HashSet<string> AlreadyMeasured()
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        var path = Path.Combine(_directory, ResultsFile);

        if (!File.Exists(path))
            return done;

        foreach (var line in File.ReadLines(path))
        {
            var at = line.IndexOf(Sep);
            if (at > 0)
                done.Add(line.Substring(0, at));
        }

        return done;
    }

    /// <summary>
    /// Jurnalul, in forma pe care recensamantul a dus-o prin 900 de morti de proces. O singura metoda este
    /// scrisa la un moment dat - spre deosebire de faza 3, unde o transa armata lasa mai multi suspecti -
    /// fiindca aici apelurile sunt sincrone si se face cate unul pe rand. Deci fiecare cadere are exact un
    /// vinovat si niciodata nu se pierd metode nevinovate.
    ///
    /// Metoda gasita in jurnal la pornire nu este doar sarita, ci si INREGISTRATA ca CRASHED: o cadere este
    /// un rezultat despre metoda aceea, nu o metoda pierduta.
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

        string crashed;
        try
        {
            crashed = File.ReadAllText(journalPath).Trim();
        }
        catch (Exception)
        {
            return skip;
        }

        // Jurnalul poarta trei campuri, dar in lista de sarituri intra NUMAI cheia. Lista este consultata
        // cu cheia goala - asa o citeste si lista de lucru - iar daca aici ar intra randul intreg, cheia nu
        // s-ar potrivi niciodata, metoda care a omorat procesul ar fi incercata din nou la fiecare pornire
        // si sesiunea n-ar mai trece niciodata de ea.
        var parts = crashed.Split(Sep);
        var crashedKey = parts[0];

        if (crashedKey.Length > 0 && skip.Add(crashedKey))
        {
            File.AppendAllText(skipPath, crashedKey + Environment.NewLine);

            // Etapa ajunge in coloana de TARIE, acolo unde celelalte verdicte isi pun felul dovezii. Asa
            // se poate numara direct din fisier cate caderi au fost pe partea jocului si cate pe a noastra,
            // fara sa fie nevoie de inca o rulare ca sa se afle.
            var stage = parts.Length > 3 ? parts[3] : "necunoscuta";

            RecordLine(crashedKey, parts.Length > 1 ? parts[1] : "", VerdictCrashed, stage,
                parts.Length > 2 ? parts[2] : "", "", "", "a luat procesul cu ea la etapa " + stage);

            _log("Rularea trecuta a murit in " + crashedKey + " la etapa " + stage
                + " - inregistrata ca CRASHED si sarita de acum.");
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

    // --------------------------------------------------------------------------------------------
    // Maturarea
    // --------------------------------------------------------------------------------------------

    public static void Tick()
    {
        if (_finished)
            return;

        if (_preparing)
        {
            TickPrepare();
            return;
        }

        TickCall();
    }

    /// <summary>
    /// Trecerea de pregatire: se compileaza corpul recuperat al fiecarei metode din coada, si NU se cheama
    /// nimic.
    ///
    /// De ce separat de apeluri, desi pregatirea per metoda facea deja acelasi lucru cu o linie mai jos.
    /// Diferenta nu este in cate reporniri ies - tot atatea - ci in ce ramane dupa ele. O metoda pe care
    /// JIT-ul o omoara costa o repornire O SINGURA DATA, fiindca ajunge in active-skip.txt, iar una pe care
    /// o refuza cuminte ajunge in active-results.tsv ca IL_INVALID. Amandoua fisierele sunt citite de orice
    /// sesiune urmatoare, indiferent de mod. Cu pregatirea amestecata printre apeluri, descoperirea asta se
    /// face din nou in fiecare mod si se plateste de fiecare data; asezata in fata, se plateste o data si
    /// faza de apeluri porneste pe o coada din care JIT-ul nu mai are ce sa doboare.
    ///
    /// Pregatirea se reface la FIECARE pornire, si asta nu este risipa. Compilarea traieste in proces, nu pe
    /// disc: un proces nou nu stie nimic despre ce a compilat cel dinainte, iar garantia care face faza de
    /// apeluri linistita este tocmai ca fiecare metoda a trecut prin JIT in ACEST proces. Un catalog cu
    /// "pregatita cu bine" ar scurta pornirea si ar desfiinta chiar garantia pentru care exista trecerea.
    /// </summary>
    private static void TickPrepare()
    {
        if (_prepareQueue == null)
        {
            FinishPrepare();
            return;
        }

        for (var i = 0; i < _perFrame && _prepareAt < _prepareQueue.Count; i++)
            StepPrepare(_prepareQueue[_prepareAt++]);

        if (_prepareAt < _prepareQueue.Count)
        {
            if (_prepareAt - _lastPrepareLogged >= 500)
            {
                _lastPrepareLogged = _prepareAt;
                _log("  pregatite " + _prepareAt + " / " + _prepareQueue.Count
                    + " (refuzate " + (_prepareFailed?.Count ?? 0) + ")...");
            }

            return;
        }

        FinishPrepare();
    }

    private static void StepPrepare(string[] row)
    {
        var key = row[ColKey];
        var assembly = row[ColAssembly];

        // Acelasi jurnal ca la apeluri, cu etapa lui: o metoda care omoara JIT-ul trebuie sa-si spuna numele
        // inainte, altfel repornirea nu are ce sari.
        WriteJournal(key, assembly, row[ColQuality], StagePreparePass);

        MethodBase ours;
        try
        {
            ours = _recovered.Index(assembly).TryGetValue(key, out var found) ? found : null;
        }
        catch (Exception)
        {
            ours = null;
        }

        if (ours == null)
        {
            // Nerezolvata aici nu se inregistreaza ca rezultat: faza de apeluri ajunge la ea si scrie motivul
            // adevarat. Daca am scrie un rand acum, metoda ar fi socotita masurata si nu s-ar mai incerca.
            _prepareUnresolved++;
            return;
        }

        if (!PrepareOurs(ours, out var error))
        {
            _prepareFailed.Add(key);
            Record(key, assembly, VerdictIlInvalid, "prepare-pass", row[ColQuality], "", "", error);
            return;
        }

        _preparedOk++;
    }

    private static void FinishPrepare()
    {
        _preparing = false;

        var refused = _prepareFailed?.Count ?? 0;
        _log("Pregatire gata: " + _preparedOk + " compilate, " + refused + " refuzate (IL_INVALID), "
            + _prepareUnresolved + " nerezolvate.");

        // Coada de apeluri pierde ce a fost refuzat la compilare: metodele acelea au deja un verdict propriu
        // si un apel peste ele ar cere exact compilarea care tocmai a esuat.
        if (_queue != null && refused > 0)
        {
            var kept = new List<string[]>(_queue.Count);
            foreach (var row in _queue)
                if (!_prepareFailed.Contains(row[ColKey]))
                    kept.Add(row);

            _log("  coada de apeluri: " + _queue.Count + " -> " + kept.Count);
            _queue = kept;
        }

        if (_prepareOnly)
        {
            _log("CPP2IL_ACTIVE_PREPARE_ONLY=1 - lista este cladita, nu se cheama nicio metoda.");
            CloseJournal();
            Summarise();
            _finished = true;
        }
    }

    private static void TickCall()
    {
        if (_queue == null)
            return;

        for (var i = 0; i < _perFrame && _queueAt < _queue.Count; i++)
            Step(_queue[_queueAt++]);

        if (_queueAt < _queue.Count)
        {
            // Pragul se tine intr-un camp si nu se calculeaza cu modulo: apelurile se fac in transe pe
            // cadru, deci _queueAt sare peste multipli intregi si un test "% 500 == 0" ar tacea sesiuni
            // intregi la intamplare.
            if (_queueAt - _lastLogged >= 500)
            {
                _lastLogged = _queueAt;
                _log("  " + _queueAt + " / " + _queue.Count + " incercate...");
            }

            return;
        }

        CloseJournal();
        Summarise();
        _finished = true;
    }

    private static void Step(string[] row)
    {
        var key = row[ColKey];
        var assembly = row[ColAssembly];

        // Scris INAINTE de apel, nu dupa. O metoda care ia procesul cu ea nu se poate prinde, si singurul
        // fel in care rularea urmatoare trece de ea este sa-i stie numele de dinainte.
        WriteJournal(key, assembly, row[ColQuality], StageResolve);

        try
        {
            Invoke(row);
        }
        catch (Exception ex)
        {
            // Orice scapa de aici este un defect al harnasului, nu al metodei - dar nu are voie sa opreasca
            // sesiunea, altfel o singura metoda ciudata ar costa toate miile de dupa ea.
            RecordLine(key, assembly, VerdictNotAttempted, "harness-threw", row[ColQuality], "", "",
                ex.GetType().Name + ": " + ex.Message);
            Bump(Verdicts, VerdictNotAttempted);
            Bump(NotAttemptedReasons, "harnasul a aruncat: " + ex.GetType().Name);
        }
    }

    private static void Invoke(string[] row)
    {
        var key = row[ColKey];
        var assembly = row[ColAssembly];
        var quality = row[ColQuality];
        var returnPlan = row[ColReturnPlan];

        if (!_gameIndex.TryGetValue(key, out var game))
        {
            NotAttempted(key, assembly, quality, "cheia a disparut din indexul jocului");
            return;
        }

        MethodBase ours;
        try
        {
            ours = _recovered.Index(assembly).TryGetValue(key, out var found) ? found : null;
        }
        catch (Exception ex)
        {
            NotAttempted(key, assembly, quality, "indexul recuperat a aruncat: " + ex.GetType().Name);
            return;
        }

        if (ours == null)
        {
            NotAttempted(key, assembly, quality, "cheia a disparut din indexul recuperat");
            return;
        }

        // Verificarea de identitate care nu se sare: daca "metoda jocului" vine chiar din contextul nostru,
        // am compara codul recuperat cu el insusi - perfect de acord si fara niciun inteles.
        if (_recovered.Owns(game.DeclaringType?.Assembly))
        {
            NotAttempted(key, assembly, quality, "metoda jocului este de fapt tot cea recuperata");
            return;
        }

        object gameReceiver = null;
        object ourReceiver = null;

        if (!game.IsStatic)
        {
            if (!_receivers)
            {
                NotAttempted(key, assembly, quality, "receptorii sunt opriti din configurare");
                return;
            }

            gameReceiver = NativeReceiver.Allocate(game.DeclaringType, out var whyGame);
            if (gameReceiver == null)
            {
                NotAttempted(key, assembly, quality, "receptorul jocului: " + whyGame);
                return;
            }

            try
            {
                ourReceiver = RuntimeHelpers.GetUninitializedObject(ours.DeclaringType);
            }
            catch (Exception ex)
            {
                NotAttempted(key, assembly, quality, "receptorul recuperat: " + ex.GetType().Name);
                return;
            }
        }

        if (!BuildArguments(game, ours, key, out var gameArgs, out var ourArgs, out var argProblem))
        {
            NotAttempted(key, assembly, quality, argProblem);
            return;
        }

        // Compilarea corpului recuperat, INAINTE de orice apel si inaintea metodei jocului.
        //
        // Ce incearca sa repare: un corp recuperat cu IL invalid nu da intotdeauna InvalidProgramException.
        // Uneori JIT-ul cade in timp ce construieste ciotul de invocare prin reflectie, si atunci nu mai
        // exista cadru pe care sa se ridice o exceptie - procesul moare cu 0x80131506 si niciun try nu il
        // prinde. PrepareMethod cere aceeasi compilare, dar in afara ciotului, unde esecul are unde sa se
        // ridice ca exceptie obisnuita.
        //
        // NU este sigur ca muta toate caderile, si nu se pretinde asta nicaieri: daca PrepareMethod cade la
        // fel de fatal, jurnalul o va arata drept cadere la etapa "prepare-ours" si atunci se stie, dintr-o
        // singura rulare, ca drumul asta nu tine. Se face inaintea metodei jocului dinadins - un corp pe
        // care nu il putem compila nu are de ce sa mai coste un apel in codul nativ al jocului.
        WriteJournal(key, assembly, quality, StagePrepare);

        if (!PrepareOurs(ours, out var prepareError))
        {
            Record(key, assembly, VerdictIlInvalid, "prepare-refused", quality, "", "", prepareError);
            return;
        }

        WriteJournal(key, assembly, quality, StageCallGame);
        var gameThrew = Call(game, gameReceiver, gameArgs, out var gameValue, out var gameError);

        WriteJournal(key, assembly, quality, StageCallOurs);
        var ourThrew = Call(ours, ourReceiver, ourArgs, out var ourValue, out var ourError);

        if (gameThrew && ourThrew)
        {
            // Doua exceptii de acelasi fel sunt un acord slab, dar real: amandoua implementarile au refuzat
            // aceeasi intrare in acelasi fel. Doua exceptii de feluri diferite sunt un dezacord la fel de
            // adevarat ca doua numere diferite, si de aceea nu se amesteca.
            var same = string.Equals(gameError, ourError, StringComparison.Ordinal);
            Record(key, assembly, VerdictThrewBoth, same ? "same-exception" : "different-exception",
                quality, gameError, ourError, same ? "" : "tipuri de exceptie diferite");
            return;
        }

        if (gameThrew || ourThrew)
        {
            Record(key, assembly, VerdictThrewOne, gameThrew ? "game-threw" : "recovered-threw",
                quality, gameThrew ? gameError : Show(gameValue), ourThrew ? ourError : Show(ourValue), "");
            return;
        }

        CompareReturn(key, assembly, quality, returnPlan, game, ours, gameValue, ourValue);
    }

    /// <summary>
    /// Cere JIT-ului sa compileze corpul recuperat acum, ca sa avem unde prinde un IL invalid.
    ///
    /// Metoda jocului NU se pregateste la fel, si nu din scapare: invelisul Il2CppInterop este IL generat
    /// de unealta si valid prin constructie, deci nu are ce sa refuze JIT-ul acolo. Daca o cadere se
    /// intampla totusi pe partea jocului, ea vine din codul NATIV de dincolo de invelis, unde nici
    /// PrepareMethod si nicio alta pregatire nu ajunge.
    /// </summary>
    private static bool PrepareOurs(MethodBase method, out string error)
    {
        error = "";

        if (!_preJit)
            return true;

        try
        {
            RuntimeHelpers.PrepareMethod(method.MethodHandle);
            return true;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            error = inner.GetType().Name + ": " + inner.Message;
            return false;
        }
    }

    /// <summary>
    /// Cheama o metoda si spune daca a aruncat. Exceptia se intoarce ca NUME de tip si nu ca mesaj:
    /// mesajele poarta nume de tipuri si adrese care difera intre cele doua universuri de tipuri, deci doua
    /// NullReferenceException identice ca inteles ar arata ca fiind diferite.
    /// </summary>
    private static bool Call(MethodBase method, object receiver, object[] args, out object value, out string error)
    {
        value = null;
        error = "";

        try
        {
            value = method.Invoke(receiver, args);
            return false;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            error = inner.GetType().Name;
            return true;
        }
    }

    // --------------------------------------------------------------------------------------------
    // Argumentele
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// Aceleasi valori, materializate in cele doua universuri de tipuri.
    ///
    /// Asta este piesa pe care fuzzing-ul intre doua procese nu o putea avea. Frunzele - bitii propriu-zisi
    /// - se genereaza O SINGURA DATA, dintr-o samanta care depinde numai de samanta rularii si de cheia
    /// metodei, si apoi se toarna in tipul jocului si in tipul recuperat separat. Deci nu se compara doua
    /// generari care se spera ca au iesit la fel, ci aceleasi numere puse in doua forme.
    /// </summary>
    private static bool BuildArguments(MethodBase game, MethodBase ours, string key,
        out object[] gameArgs, out object[] ourArgs, out string problem)
    {
        gameArgs = null;
        ourArgs = null;
        problem = "";

        var gameParameters = game.GetParameters();
        var ourParameters = ours.GetParameters();

        if (gameParameters.Length != ourParameters.Length)
        {
            problem = "numar diferit de parametri: joc " + gameParameters.Length + ", recuperat " + ourParameters.Length;
            return false;
        }

        gameArgs = new object[gameParameters.Length];
        ourArgs = new object[ourParameters.Length];

        var random = new DeterministicRandom(DeterministicRandom.SeedFor(_seed, key));

        for (var i = 0; i < gameParameters.Length; i++)
        {
            var gameType = gameParameters[i].ParameterType;
            var ourType = ourParameters[i].ParameterType;

            switch (TypeKinds.PlanFor(ourType))
            {
                case ArgPlans.Null:
                    gameArgs[i] = null;
                    ourArgs[i] = null;
                    break;

                case ArgPlans.Generated:
                    if (!Generate(gameType, ourType, ref random, out gameArgs[i], out ourArgs[i]))
                    {
                        // Formele nu se potrivesc, adica layout-ul recuperat difera de cel al jocului. Este
                        // un rezultat in sine, dar nu unul care sa opreasca apelul: se cade pe zero de
                        // ambele parti, tot simetric, si se merge mai departe.
                        if (!Zero(gameType, ourType, out gameArgs[i], out ourArgs[i]))
                        {
                            problem = "parametrul " + i + " nu s-a putut fabrica";
                            return false;
                        }
                    }

                    break;

                case ArgPlans.Zeroed:
                    if (!Zero(gameType, ourType, out gameArgs[i], out ourArgs[i]))
                    {
                        problem = "parametrul " + i + " (structura) nu s-a putut construi";
                        return false;
                    }

                    break;

                default:
                    problem = "parametrul " + i + " nu are cum sa fie fabricat";
                    return false;
            }
        }

        return true;
    }

    private static bool Generate(Type gameType, Type ourType, ref DeterministicRandom random, out object gameValue, out object ourValue)
    {
        gameValue = null;
        ourValue = null;

        var gameShape = ValueShape.For(gameType);
        var ourShape = ValueShape.For(ourType);

        if (gameShape == null || ourShape == null || gameShape.LeafCount != ourShape.LeafCount)
            return false;

        // Frunzele se strang ca FORME, nu doar ca feluri de primitiva, fiindca pentru un enum ne trebuie
        // si tipul lui ca sa stim ce valori are voie sa ia.
        var leafShapes = new List<ValueShape>();
        CollectLeaves(ourShape, leafShapes);

        var leaves = new object[leafShapes.Count];
        for (var i = 0; i < leafShapes.Count; i++)
            leaves[i] = LeafValue(leafShapes[i], ref random);

        try
        {
            var gameAt = 0;
            var ourAt = 0;
            gameValue = gameShape.Materialise(leaves, ref gameAt);
            ourValue = ourShape.Materialise(leaves, ref ourAt);
            return true;
        }
        catch (Exception)
        {
            gameValue = null;
            ourValue = null;
            return false;
        }
    }

    private static void CollectLeaves(ValueShape shape, List<ValueShape> leaves)
    {
        if (shape.IsLeaf)
        {
            leaves.Add(shape);
            return;
        }

        foreach (var child in shape.Children)
            CollectLeaves(child, leaves);
    }

    /// <summary>
    /// Valoarea unei frunze. Pentru un enum se trage dintre valorile DECLARATE, nu de pe tot intervalul
    /// intregului de dedesubt.
    ///
    /// Nu este o rafinare de stil, este o reparatie cu nume si prenume. Dintre cele cinci metode care au
    /// omorat procesul in prima sesiune, doua au exact aceeasi forma - CompressionUtils::GetHttpName(
    /// CompressionAlgorithm) si QualityTermInterpreter::QualityLevelToText(QualityLevel): primesc un enum
    /// si intorc un string. Un joc scrie asa ceva ca o cautare intr-un tabel indexat cu enumul, iar IL2CPP
    /// compilat pentru livrare nu mai emite verificari de interval. Un enum fuzzat cu int.MinValue citeste
    /// atunci mult in afara tabelului si intoarce un pointer de gunoi, pe care invelisul Il2CppInterop il
    /// desface ca pe un obiect - de acolo pana la moartea procesului nu mai e nimic de facut.
    ///
    /// Ce se pierde, spus pe fata: ramura "valoare necunoscuta" a metodei nu mai este exersata. Este un
    /// schimb constient - acea ramura costa, masurat, doua morti de proces din cinci, iar fiecare moarte
    /// costa o repornire de treizeci de secunde in care nu se masoara nimic. CPP2IL_ACTIVE_ENUM_DOMAIN=0
    /// da inapoi fuzzarea pe tot intervalul.
    ///
    /// Valorile declarate se iau de pe tipul RECUPERAT si se dau ca intreg brut amandurora. Este corect
    /// fiindca Materialise face Enum.ToObject cu tipul fiecarei parti, iar toate cele 1.761 de enum-uri
    /// recuperate au acelasi tip de baza si aceleasi valori ca ale jocului - verificat pe metadate, nu
    /// presupus.
    /// </summary>
    private static object LeafValue(ValueShape leaf, ref DeterministicRandom random)
    {
        if (!_enumDomain || leaf.EnumUnderlying == null)
            return FuzzInputs.RandomValue(leaf.Kind, ref random);

        Array declared;
        try
        {
            declared = Enum.GetValues(leaf.Type);
        }
        catch (Exception)
        {
            return FuzzInputs.RandomValue(leaf.Kind, ref random);
        }

        // Un enum fara niciun membru declarat nu are domeniu din care sa alegem; acolo intervalul intreg
        // este singurul lucru pe care il putem da.
        if (declared.Length == 0)
            return FuzzInputs.RandomValue(leaf.Kind, ref random);

        var picked = declared.GetValue((int)(random.Next() % (ulong)declared.Length));

        try
        {
            // Inapoi la intregul de dedesubt: Materialise asteapta frunza ca numar si o imbraca el in
            // enumul fiecarei parti. Un enum recuperat in cutie dat asa mai departe ar fi imbracat a doua
            // oara, in tipul gresit.
            return Convert.ChangeType(picked, leaf.EnumUnderlying, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return FuzzInputs.RandomValue(leaf.Kind, ref random);
        }
    }

    private static bool Zero(Type gameType, Type ourType, out object gameValue, out object ourValue)
    {
        gameValue = null;
        ourValue = null;

        try
        {
            if (gameType.IsValueType)
                gameValue = Activator.CreateInstance(gameType);

            if (ourType.IsValueType)
                ourValue = Activator.CreateInstance(ourType);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // --------------------------------------------------------------------------------------------
    // Comparatia
    // --------------------------------------------------------------------------------------------

    private static void CompareReturn(string key, string assembly, string quality, string plan,
        MethodBase game, MethodBase ours, object gameValue, object ourValue)
    {
        switch (plan)
        {
            case "void":
                // S-a chemat, nu a aruncat, si nu are ce sa intoarca. Nu este nici acord nici dezacord, si
                // tocmai de aceea are verdict propriu: varsat peste AGREES ar umfla cifra cu metode despre
                // care nu stim nimic in afara de faptul ca nu au crapat.
                Record(key, assembly, VerdictNoReturn, "nothing-to-compare", quality, "", "", "");
                return;

            case "bits":
                if (SameBits(gameValue, ourValue))
                    Record(key, assembly, VerdictAgrees, "bit-exact", quality, Show(gameValue), Show(ourValue), "");
                else
                    Record(key, assembly, VerdictDisagrees, "bit-exact", quality, Show(gameValue), Show(ourValue), "");

                return;

            case "shape":
                CompareShape(key, assembly, quality, game, ours, gameValue, ourValue);
                return;

            case "text":
                var gameText = gameValue as string;
                var ourText = ourValue as string;

                if (gameValue == null || ourValue == null)
                {
                    if (gameValue == null && ourValue == null)
                        Record(key, assembly, VerdictAgreesWeak, "both-null", quality, "(null)", "(null)", "");
                    else
                        Record(key, assembly, VerdictDisagrees, "nullness", quality, Show(gameValue), Show(ourValue), "");

                    return;
                }

                if (string.Equals(gameText, ourText, StringComparison.Ordinal))
                    Record(key, assembly, VerdictAgrees, "text", quality, Show(gameValue), Show(ourValue), "");
                else
                    Record(key, assembly, VerdictDisagrees, "text", quality, Show(gameValue), Show(ourValue), "");

                return;

            default:
                // Tip intors care este o clasa: cele doua valori traiesc in universuri de tipuri diferite si
                // nu au cum sa fie comparate pe continut. Ramane null-ul, care este putin dar nu este zero -
                // o metoda care intoarce un obiect acolo unde cealalta intoarce null chiar difera.
                if (gameValue == null && ourValue == null)
                    Record(key, assembly, VerdictAgreesWeak, "both-null", quality, "(null)", "(null)", "");
                else if (gameValue != null && ourValue != null)
                    Record(key, assembly, VerdictAgreesWeak, "both-non-null", quality, "(obiect)", "(obiect)", "");
                else
                    Record(key, assembly, VerdictDisagrees, "nullness", quality, Show(gameValue), Show(ourValue), "");

                return;
        }
    }

    /// <summary>
    /// Doua structuri din universuri de tipuri diferite nu se pot compara cu Equals - sunt tipuri CLR
    /// diferite chiar cand poarta acelasi nume - deci se compara asa cum le compara si fazele 1 si 2: prin
    /// frunzele lor, in aceeasi ordine, absorbite in acelasi fel de hash.
    /// </summary>
    private static void CompareShape(string key, string assembly, string quality,
        MethodBase game, MethodBase ours, object gameValue, object ourValue)
    {
        var gameShape = ValueShape.For((game as MethodInfo)?.ReturnType);
        var ourShape = ValueShape.For((ours as MethodInfo)?.ReturnType);

        if (gameShape == null || ourShape == null || gameShape.LeafCount != ourShape.LeafCount)
        {
            Record(key, assembly, VerdictDisagrees, "shape-mismatch", quality,
                gameShape == null ? "?" : gameShape.LeafCount.ToString(CultureInfo.InvariantCulture),
                ourShape == null ? "?" : ourShape.LeafCount.ToString(CultureInfo.InvariantCulture),
                "structura intoarsa are alta forma pe cele doua parti");
            return;
        }

        ulong gameHash;
        ulong ourHash;

        try
        {
            using (var hash = new SignatureHash())
            {
                hash.RestartRunning();
                gameShape.Absorb(gameValue, hash);
                gameHash = hash.Running;

                hash.RestartRunning();
                ourShape.Absorb(ourValue, hash);
                ourHash = hash.Running;
            }
        }
        catch (Exception ex)
        {
            Record(key, assembly, VerdictNotAttempted, "shape-absorb", quality, "", "", ex.GetType().Name);
            Bump(NotAttemptedReasons, "citirea structurii intoarse a aruncat: " + ex.GetType().Name);
            return;
        }

        if (gameHash == ourHash)
            Record(key, assembly, VerdictAgrees, "shape", quality, "0x" + gameHash.ToString("x16"), "0x" + ourHash.ToString("x16"), "");
        else
            Record(key, assembly, VerdictDisagrees, "shape", quality, "0x" + gameHash.ToString("x16"), "0x" + ourHash.ToString("x16"), "");
    }

    /// <summary>
    /// Egalitate pe BITI, acelasi prag ca hash-ul fazelor 1 si 2: un float care difera pe ultimul bit este
    /// o diferenta si trebuie sa se vada ca atare, nu sa fie inghitita de o toleranta pe care nu a ales-o
    /// nimeni.
    /// </summary>
    private static bool SameBits(object a, object b)
    {
        if (a == null || b == null)
            return a == null && b == null;

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
    // comparatia coboara la intregul de dedesubt - acelasi drum ca in ValueShape.Absorb.
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
        string s => s.Length > 120 ? s.Substring(0, 120) + "..." : s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.GetType().Name,
    };

    // --------------------------------------------------------------------------------------------
    // Rezultatele
    // --------------------------------------------------------------------------------------------

    private static void NotAttempted(string key, string assembly, string quality, string reason)
    {
        Record(key, assembly, VerdictNotAttempted, "not-attempted", quality, "", "", reason);
        Bump(NotAttemptedReasons, reason);
    }

    private static void Record(string key, string assembly, string verdict, string strength,
        string quality, string gameValue, string ourValue, string detail)
    {
        RecordLine(key, assembly, verdict, strength, quality, gameValue, ourValue, detail);
        Bump(Verdicts, verdict);
    }

    private static void RecordLine(string key, string assembly, string verdict, string strength,
        string quality, string gameValue, string ourValue, string detail)
    {
        var builder = new StringBuilder();
        builder.Append(Clean(key)).Append(Sep);
        builder.Append(Clean(assembly)).Append(Sep);
        builder.Append(verdict).Append(Sep);
        builder.Append(Clean(strength)).Append(Sep);
        builder.Append(Clean(quality)).Append(Sep);
        builder.Append(Clean(gameValue)).Append(Sep);
        builder.Append(Clean(ourValue)).Append(Sep);
        builder.Append(Clean(detail));

        try
        {
            File.AppendAllText(Path.Combine(_directory, ResultsFile), builder.ToString() + Environment.NewLine);
        }
        catch (Exception)
        {
            // Un rand pierdut costa un rezultat, nu sesiunea.
        }
    }

    /// <summary>
    /// Jurnalul se tine deschis si se goleste dupa fiecare scriere, nu se deschide si se inchide de 59.000
    /// de ori. Flush duce datele in sistemul de operare, iar asta ajunge: o violare de acces omoara procesul
    /// dar nu si ce a apucat sistemul de operare sa preia. Numai o cadere a masinii ar pierde randul, si
    /// atunci nu jurnalul este problema.
    /// </summary>
    private static void WriteJournal(string key, string assembly, string quality, string stage)
    {
        try
        {
            if (_journal == null)
                _journal = new FileStream(Path.Combine(_directory, InflightFile), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);

            var bytes = Encoding.UTF8.GetBytes(key + Sep + assembly + Sep + quality + Sep + stage);
            _journal.SetLength(0);
            _journal.Position = 0;
            _journal.Write(bytes, 0, bytes.Length);
            _journal.Flush();
        }
        catch (Exception)
        {
            // Fara jurnal, o cadere costa o metoda in plus la repornire - nu merita oprita sesiunea.
        }
    }

    private static void CloseJournal()
    {
        try
        {
            _journal?.Dispose();
            _journal = null;

            var path = Path.Combine(_directory, InflightFile);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Raportul. Grupat pe verdict, pe calitatea argumentelor, pe assembly si pe tip declarant - patru
    /// taieturi si niciun numar unic, fiindca un numar unic ar fi exact felul de cifra care a mintit pana
    /// acum: "N metode sunt de acord" nu inseamna nimic fara cate dintre ele au avut receptor fabricat.
    /// </summary>
    private static void Summarise()
    {
        var path = Path.Combine(_directory, ResultsFile);
        var byVerdict = new Dictionary<string, int>(StringComparer.Ordinal);
        var byQuality = new Dictionary<string, int>(StringComparer.Ordinal);
        var byAssembly = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var byType = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var agreeByQuality = new Dictionary<string, int>(StringComparer.Ordinal);
        var byCrashStage = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;

        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                var fields = line.Split(Sep);
                if (fields.Length < 5)
                    continue;

                total++;
                var key = fields[0];
                var assembly = fields[1];
                var verdict = fields[2];
                var quality = fields[4];

                Bump(byVerdict, verdict);
                Bump(byQuality, quality);

                if (!byAssembly.TryGetValue(assembly, out var perAssembly))
                    byAssembly[assembly] = perAssembly = new Dictionary<string, int>(StringComparer.Ordinal);

                Bump(perAssembly, verdict);

                var at = key.IndexOf("::", StringComparison.Ordinal);
                var typeName = at > 0 ? key.Substring(0, at) : "?";

                if (!byType.TryGetValue(typeName, out var perType))
                    byType[typeName] = perType = new Dictionary<string, int>(StringComparer.Ordinal);

                Bump(perType, verdict);

                if (verdict == VerdictAgrees)
                    Bump(agreeByQuality, quality);

                if (verdict == VerdictCrashed)
                    Bump(byCrashStage, fields.Length > 3 ? fields[3] : "necunoscuta");
            }
        }

        var builder = new StringBuilder();
        builder.AppendLine("faza 4 - maturare activa");
        builder.AppendLine("dll: " + _dllDirectory);
        builder.AppendLine("incercate in sesiunea asta: " + _queueAt + " din " + (_queue?.Count ?? 0));

        if (_prepareQueue != null)
        {
            builder.AppendLine("pregatire: " + _prepareAt + " din " + _prepareQueue.Count
                + " - " + _preparedOk + " compilate, " + (_prepareFailed?.Count ?? 0) + " refuzate (IL_INVALID), "
                + _prepareUnresolved + " nerezolvate");
            builder.AppendLine("   ATENTIE: PrepareMethod compileaza corpul metodei, NU si corpurile pe care le");
            builder.AppendLine("   cheama ea. Un apelat care nu a trecut si el prin trecerea de pregatire se");
            builder.AppendLine("   compileaza abia la primul apel si poate cadea acolo. Cu");
            builder.AppendLine("   CPP2IL_ACTIVE_PREPARE_ALL=1 toate metodele CHEMABILE sunt acoperite; raman pe");
            builder.AppendLine("   dinafara doar cele blocate din dump, care pot fi si ele apelate din corpuri.");
        }
        builder.AppendLine("randuri in total (toate sesiunile): " + total);
        builder.AppendLine();

        builder.AppendLine("== pe verdict ==");
        foreach (var pair in Sorted(byVerdict))
            builder.AppendLine("  " + pair.Key.PadRight(16) + pair.Value);

        builder.AppendLine();
        builder.AppendLine("== ACORDURI, desfacute pe calitatea argumentelor ==");
        builder.AppendLine("   (un acord obtinut cu receptor fabricat pe zero NU se aduna cu unul pe valori generate)");
        foreach (var pair in Sorted(agreeByQuality))
            builder.AppendLine("  " + pair.Key.PadRight(48) + pair.Value);

        if (byCrashStage.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("== CADERI, pe etapa ==");
            builder.AppendLine("   call-game   = a omorat-o metoda NATIVA a jocului; nici PrepareMethod nici");
            builder.AppendLine("                 filtrarea IL-ului recuperat nu ajuta acolo");
            builder.AppendLine("   call-ours   = a omorat-o corpul recuperat la apel, desi compilarea a trecut");
            builder.AppendLine("   prepare-ours= a omorat-o chiar compilarea corpului recuperat, deci");
            builder.AppendLine("                 PrepareMethod nu muta caderea si drumul asta nu tine");
            foreach (var pair in Sorted(byCrashStage))
                builder.AppendLine("  " + pair.Key.PadRight(16) + pair.Value);
        }

        builder.AppendLine();
        builder.AppendLine("== pe calitatea argumentelor, toate verdictele ==");
        foreach (var pair in Sorted(byQuality))
            builder.AppendLine("  " + pair.Key.PadRight(48) + pair.Value);

        builder.AppendLine();
        builder.AppendLine("== pe assembly ==");
        foreach (var pair in byAssembly)
        {
            builder.Append("  ").Append(pair.Key).Append(": ");
            foreach (var verdict in Sorted(pair.Value))
                builder.Append(verdict.Key).Append('=').Append(verdict.Value).Append(' ');

            builder.AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine("== primele 40 de tipuri dupa numar de randuri ==");
        var types = new List<KeyValuePair<string, Dictionary<string, int>>>(byType);
        types.Sort((a, b) => Sum(b.Value).CompareTo(Sum(a.Value)));

        for (var i = 0; i < types.Count && i < 40; i++)
        {
            builder.Append("  ").Append(types[i].Key).Append(": ");
            foreach (var verdict in Sorted(types[i].Value))
                builder.Append(verdict.Key).Append('=').Append(verdict.Value).Append(' ');

            builder.AppendLine();
        }

        if (NotAttemptedReasons.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("== de ce nu s-a incercat (sesiunea asta) ==");
            foreach (var pair in Sorted(NotAttemptedReasons))
                builder.AppendLine("  " + pair.Value.ToString(CultureInfo.InvariantCulture).PadLeft(6) + "  " + pair.Key);
        }

        File.WriteAllText(Path.Combine(_directory, SummaryFile), builder.ToString());
        _log(builder.ToString());
        _log("ACTIVE_DONE");
    }

    private static int Sum(Dictionary<string, int> counts)
    {
        var total = 0;
        foreach (var pair in counts)
            total += pair.Value;

        return total;
    }

    private static List<KeyValuePair<string, int>> Sorted(Dictionary<string, int> counts)
    {
        var list = new List<KeyValuePair<string, int>>(counts);
        list.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
        return list;
    }

    private static void Bump(Dictionary<string, int> counts, string key)
    {
        counts.TryGetValue(key, out var value);
        counts[key] = value + 1;
    }

    private static void Bump(Dictionary<AssemblyClass, int> counts, AssemblyClass key)
    {
        counts.TryGetValue(key, out var value);
        counts[key] = value + 1;
    }

    private static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        return text.Replace(Sep, ' ').Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }
}
