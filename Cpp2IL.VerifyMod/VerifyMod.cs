using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cpp2IL.VerifyCore;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

[assembly: MelonInfo(typeof(Cpp2IL.VerifyMod.VerifyMod), "Cpp2IL VerifyMod", "1.0", "Cpp2IL")]
[assembly: MelonGame(null, null)]

namespace Cpp2IL.VerifyMod;

/// <summary>
/// Phase 2 of the differential verification: the same signatures, computed from the REAL methods inside
/// the running game. Phase 1 (<c>Cpp2IL.VerifyCheck</c>) only proves a recovered method is a function of
/// its arguments; comparing the two files is what says whether it is the RIGHT function.
///
/// Reads the Phase 1 file for the method list, resolves each key against the game's own Il2CppInterop
/// assemblies, fuzzes it with the same plan and the same generator, and writes a file in the identical
/// format. Nothing about input generation or hashing lives here - it is all <c>Cpp2IL.VerifyCore</c>,
/// shared with Phase 1 on purpose, because two implementations drift and then every difference in the
/// output is a difference in the harness rather than in the game.
/// </summary>
public class VerifyMod : MelonMod
{
    private const string InputFile = "verifycheck-phase1.json";
    private const string OutputFile = "verifycheck-phase2.json";

    private string _directory;
    private bool _ran;
    private bool _auto;
    private int _frames;
    private bool _substitution;
    private bool _substitutionPrepared;
    private bool _active;
    private bool _activePrepared;

    public override void OnInitializeMelon()
    {
        _directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        _auto = Environment.GetEnvironmentVariable("CPP2IL_VERIFY_AUTO") == "1";

        // Faza 4 se verifica PRIMA si le exclude pe celelalte. Cele trei masoara populatii diferite prin
        // mecanisme diferite, iar doua pornite in aceeasi sesiune ar amesteca doua experimente si niciunul
        // n-ar mai insemna nimic: maturarea fazei 2 cheama fiecare metoda a jocului de zece mii de ori cu
        // intrari ostile, faza 3 pune carlige si asteapta apelurile adevarate ale jocului, iar faza 4 cheama
        // ea insasi ambele implementari. Un carlig al fazei 3 peste o metoda pe care faza 4 tocmai o cheama
        // ar masura chiar apelul nostru, nu al jocului.
        _active = ActiveSweep.Configure(_directory, message => LoggerInstance.Msg(message), BuildNativeIndex);
        if (_active)
        {
            if (SubstitutionHarness.Requested)
                LoggerInstance.Warning("CPP2IL_SUBST si CPP2IL_ACTIVE sunt amandoua pornite; faza 4 castiga, faza 3 nu porneste.");

            return;
        }

        // Ceruta dar cazuta la configurare: nu se cade inapoi pe nicio alta faza. Acelasi rationament ca la
        // faza 3 de mai jos - un comutator scris gresit nu are voie sa porneasca alt experiment.
        if (ActiveSweep.Requested)
        {
            LoggerInstance.Msg("Faza 4 a fost ceruta dar nu a putut porni; nu se porneste alta faza in locul ei.");
            _ran = true;
            return;
        }

        // Faza 3 sta pe comutatorul ei si nu are nevoie de fisierul fazei 1: masoara alta populatie de
        // metode, prin alt mecanism, si scrie in alte fisiere. Cele doua NU ruleaza in aceeasi sesiune -
        // maturarea fazei 2 cheama fiecare metoda a jocului de zece mii de ori cu intrari ostile, iar un
        // carlig pus peste asa ceva ar amesteca doua experimente si niciunul n-ar mai insemna nimic.
        // Inainte de orice: daca s-a cerut un backend local, se armeaza acum, fiindca Initializer.Awake
        // ruleaza devreme si un carlig pus dupa el nu mai prinde nimic.
        BackendRedirect.Install(HarmonyInstance, message => LoggerInstance.Msg(message));

        _substitution = SubstitutionHarness.Configure(_directory, HarmonyInstance, message => LoggerInstance.Msg(message), BuildNativeIndex);
        if (_substitution)
            return;

        // Ceruta dar cazuta la configurare: nu se cade inapoi pe maturare. Vezi nota lui
        // SubstitutionHarness.Requested - un comutator scris gresit nu are voie sa porneasca alt experiment.
        if (SubstitutionHarness.Requested)
        {
            LoggerInstance.Msg("Faza 3 a fost ceruta dar nu a putut porni; maturarea fazei 2 NU se porneste in locul ei.");
            _ran = true;
            return;
        }

        if (!File.Exists(Path.Combine(_directory, InputFile)))
        {
            LoggerInstance.Msg($"No {InputFile} beside the mod - nothing to verify.");
            return;
        }

        if (_auto)
        {
            LoggerInstance.Msg("CPP2IL_VERIFY_AUTO=1: the sweep starts by itself once the game is up.");
        }
        else
        {
            LoggerInstance.Msg("Phase 1 file found. Press F9 in-game to run the verification sweep.");
            LoggerInstance.Msg("It takes minutes and calls thousands of game methods with hostile inputs, so it is");
            LoggerInstance.Msg("not automatic by default: a sweep at startup would look like the game had frozen.");
        }
    }

    // Triggered rather than automatic, and the reason is not politeness. The sweep calls real game code
    // with NaN, denormals and int.MinValue, which is exactly what nothing in a shipped game is written to
    // survive; if one of those takes the process down, it must be the player's choice to have started it,
    // and the crash journal below must already name the culprit so the next run gets past it.
    public override void OnUpdate()
    {
        if (_active)
        {
            TickActive();
            return;
        }

        if (_substitution)
        {
            TickSubstitution();
            return;
        }

        if (_ran)
            return;

        // Automatic mode waits a few frames rather than starting at the first one: Il2CppInterop fills in
        // its type cache lazily, and indexing the game's assemblies before it has settled finds fewer
        // methods, which would read as "the game does not have them" instead of "we asked too early".
        if (_auto && ++_frames < 300)
            return;

        if (!_auto && !Input.GetKeyDown(KeyCode.F9))
            return;

        _ran = true;

        try
        {
            Run(Path.Combine(_directory, InputFile), Path.Combine(_directory, OutputFile));
        }
        catch (Exception ex)
        {
            LoggerInstance.Error($"Verification run failed: {ex}");
        }
    }

    /// <summary>
    /// Faza 4, condusa tot cadru cu cadru, dar din alt motiv decat faza 3.
    ///
    /// Faza 3 asteapta ca jocul sa cheme metoda, deci nu are ce cauta intr-o bucla. Faza 4 nu asteapta pe
    /// nimeni si ar putea, in principiu, sa parcurga tot universul intr-un singur cadru - si tocmai de aceea
    /// nu o face. Zeci de mii de apeluri intr-un cadru inseamna un joc inghetat minute in sir, pe care
    /// Windows il arata ca "nu raspunde" si pe care utilizatorul sau sistemul il omoara - iar o metoda
    /// omorata asa ajunge in jurnal drept vinovata de o cadere pe care nu a produs-o. Cate
    /// CPP2IL_ACTIVE_PER_FRAME pe cadru, si jocul ramane viu intre transe.
    /// </summary>
    private void TickActive()
    {
        // Acelasi ragaz de cadre ca la celelalte faze si pentru acelasi motiv: Il2CppInterop isi umple lenes
        // cache-ul de tipuri, iar un index construit prea devreme arata ca si cum jocul n-ar avea metodele.
        if (++_frames < 300)
            return;

        if (!_activePrepared)
        {
            _activePrepared = true;

            try
            {
                ActiveSweep.Prepare();
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Faza 4 nu a putut porni: {ex}");
                _active = false;
            }

            return;
        }

        try
        {
            ActiveSweep.Tick();
        }
        catch (Exception ex)
        {
            LoggerInstance.Error($"Faza 4 s-a oprit: {ex}");
            _active = false;
            return;
        }

        if (!ActiveSweep.Finished)
            return;

        _active = false;

        // La fel ca celelalte faze: o rulare pornita de un script trebuie sa se termine singura, altfel
        // runner-ul ar astepta o fereastra pe care nu o inchide nimeni.
        if (_auto)
            UnityEngine.Application.Quit();
    }

    /// <summary>
    /// Faza 3, condusa cadru cu cadru si nu dintr-o bucla: harnasul ARMEAZA o metoda si apoi trebuie sa
    /// astepte ca jocul sa o cheme singur. O bucla ar tine firul principal ocupat exact cand jocul ar avea
    /// nevoie de el ca sa ajunga la punctul de apel, adica ar face imposibil chiar lucrul pe care il asteapta.
    /// </summary>
    private void TickSubstitution()
    {
        // Acelasi ragaz de cadre ca maturarea, si din acelasi motiv: Il2CppInterop isi umple lenes cache-ul
        // de tipuri, iar un index construit prea devreme arata ca si cum jocul n-ar avea metodele.
        if (++_frames < 300)
            return;

        if (!_substitutionPrepared)
        {
            _substitutionPrepared = true;

            try
            {
                SubstitutionHarness.Prepare();
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Faza 3 nu a putut porni: {ex}");
                _substitution = false;
            }

            return;
        }

        try
        {
            SubstitutionHarness.Tick();
        }
        catch (Exception ex)
        {
            LoggerInstance.Error($"Faza 3 s-a oprit: {ex}");
            _substitution = false;
            return;
        }

        if (!SubstitutionHarness.Finished)
            return;

        _substitution = false;

        // La fel ca maturarea: o rulare pornita de un script trebuie sa se termine singura, altfel
        // harnasul ar astepta o fereastra pe care nu o inchide nimeni.
        if (_auto)
            UnityEngine.Application.Quit();
    }

    private void Run(string phase1Path, string outputPath)
    {
        var request = Phase1File.Read(File.ReadAllText(phase1Path));
        LoggerInstance.Msg($"Phase 1 lists {request.Keys.Count} comparable methods; plan seed {request.Plan.Seed}.");

        var index = BuildNativeIndex();
        LoggerInstance.Msg($"Indexed {index.Count} candidate methods from the game's own assemblies.");

        var journalPath = outputPath + ".inflight";
        var skipPath = outputPath + ".skip";
        var skip = File.Exists(skipPath)
            ? new HashSet<string>(File.ReadAllLines(skipPath).Where(l => l.Length > 0), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        if (File.Exists(journalPath))
        {
            var crashed = File.ReadAllText(journalPath).Trim();
            if (crashed.Length > 0 && skip.Add(crashed))
            {
                File.AppendAllText(skipPath, crashed + Environment.NewLine);
                LoggerInstance.Warning($"Last run died in {crashed} - skipping it from now on.");
            }

            File.Delete(journalPath);
        }

        var results = new List<MethodFuzzResult>();
        var missesByReason = new Dictionary<string, int>(StringComparer.Ordinal);
        var resolved = 0;
        var missing = 0;

        foreach (var key in request.Keys)
        {
            if (!index.TryGetValue(key, out var method))
            {
                missing++;
                RecordMiss(key, index, missesByReason);
                continue;
            }

            resolved++;

            if (skip.Contains(key))
                continue;

            // Written before the call, not after: a native method that takes the process down cannot be
            // caught, so the only way to get past it is to know on the next run which one it was. Same
            // mechanism as Phase 1's journal, and the two files are read the same way.
            File.WriteAllText(journalPath, key);

            MethodFuzzResult result;
            try
            {
                result = MethodFuzzer.Run(method, request.Plan);
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                result = new MethodFuzzResult { Key = key, Failure = inner.GetType().Name + ": " + inner.Message };
            }

            // The key from the file, not the one just computed: Phase 1 is the side that decided what a
            // method is called, and a key that differs by a normalisation detail would silently drop the
            // pair out of the comparison instead of failing loudly.
            result.Key = key;
            results.Add(result);

            if (results.Count % 250 == 0)
                LoggerInstance.Msg($"  {results.Count} / {resolved} fuzzed...");
        }

        if (File.Exists(journalPath))
            File.Delete(journalPath);

        using (var writer = new StreamWriter(outputPath, false))
            SignatureJson.Write(writer, SignatureJson.PhaseNative, "in-game", request.Plan, results);

        LoggerInstance.Msg($"Resolved {resolved}, could not find {missing}. Wrote {results.Count} signatures to {outputPath}.");

        foreach (var reason in missesByReason.OrderByDescending(r => r.Value))
            LoggerInstance.Msg($"  miss: {reason.Value,5}  {reason.Key}");
        LoggerInstance.Msg("Compare with: Cpp2IL.VerifyCheck --compare phase1.json phase2.json");

        // A scripted run has to end by itself, or the harness would wait for a window nobody is going to
        // close. Interactive runs stay up, because the point there is to keep playing.
        if (_auto)
        {
            LoggerInstance.Msg("VERIFY_DONE");
            UnityEngine.Application.Quit();
        }
    }

    /// <summary>
    /// Every method in the game's Il2CppInterop assemblies that could match a Phase 1 key, indexed by that
    /// key. Built by walking the loaded assemblies rather than by resolving keys one at a time, because a
    /// key names a type by its normalised name and there is no reverse lookup from that back to a Type.
    /// </summary>
    private Dictionary<string, MethodBase> BuildNativeIndex()
    {
        var index = new Dictionary<string, MethodBase>(StringComparer.Ordinal);

        KeyCollisions.Reset();
        LoadEveryInteropAssembly();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            // The mod's own assemblies would otherwise index the RECOVERED methods sitting next to it and
            // compare them against themselves, which agrees perfectly and means nothing.
            var name = assembly.GetName().Name ?? "";
            if (name.StartsWith("Cpp2IL.", StringComparison.Ordinal) || name.StartsWith("MelonLoader", StringComparison.Ordinal))
                continue;

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException partial)
            {
                // A half-loaded assembly still contains usable types, and dropping the whole assembly
                // because one type failed would quietly shrink the comparison.
                types = partial.Types.Where(t => t != null).ToArray();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var type in types)
                IndexType(type, index);
        }

        if (KeyCollisions.Count > 0)
        {
            LoggerInstance.Warning($"{KeyCollisions.Count} chei ale jocului au cazut pe cate doua metode diferite si au fost scoase din pereche.");
            LoggerInstance.Warning("  " + KeyCollisions.Summary());
            KeyCollisions.WriteSamples(Path.Combine(_directory, "active-key-collisions.txt"), message => LoggerInstance.Msg(message));
        }

        return index;
    }

    /// <summary>
    /// Forces every Il2CppInterop assembly to load before the index is built.
    ///
    /// They load lazily - only when something first touches a type in them - so at mod-init time the
    /// domain holds whatever the game happened to need so far, and everything else is simply absent.
    /// Indexing that gives an index that looks complete and is not: 273 Phase 1 methods came back
    /// "could not find" against a build that certainly contains them, and the ones that vanished were
    /// whole types at a time (FPMathUtils, UIGradientUtils, BattlePassLevel) rather than a scattering,
    /// which is what an unloaded assembly looks like from the outside.
    /// </summary>
    private void LoadEveryInteropAssembly()
    {
        var directory = Path.Combine(MelonEnvironment.MelonLoaderDirectory, "Il2CppAssemblies");

        // Retinut inainte de orice verificare: impartirea ciocnirilor are nevoie sa stie care assembly
        // este proiectia jocului si care este un assembly .NET adevarat al gazdei, iar singurul lucru
        // care le deosebeste sigur este directorul din care au fost incarcate.
        KeyCollisions.InteropDirectory = directory;

        if (!Directory.Exists(directory))
        {
            LoggerInstance.Warning($"No Il2CppAssemblies at {directory}; the index will only see what is already loaded.");
            return;
        }

        var loaded = 0;

        foreach (var path in Directory.GetFiles(directory, "*.dll"))
        {
            try
            {
                // By name, not from the file: loading the same assembly a second time from its path would
                // give a duplicate identity, and every type in it would compare unequal to the game's own.
                Assembly.Load(AssemblyName.GetAssemblyName(path));
                loaded++;
            }
            catch (Exception)
            {
                // A dependency that will not resolve costs that one assembly, not the run.
            }
        }

        LoggerInstance.Msg($"Loaded {loaded} interop assemblies before indexing.");
    }

    /// <summary>
    /// Says WHY a key did not resolve, which is the difference between a fixable normalisation bug and a
    /// method the running build genuinely does not have. Guessing at this cost two wrong hypotheses
    /// already: first that the keys were shaped differently, then that the assemblies had not loaded -
    /// both were wrong, and the index size did not move when the second was "fixed".
    /// </summary>
    private static void RecordMiss(string key, Dictionary<string, MethodBase> index, Dictionary<string, int> reasons)
    {
        var typeName = key.Substring(0, key.IndexOf("::", StringComparison.Ordinal));
        var methodName = key.Substring(typeName.Length + 2);
        methodName = methodName.Substring(0, methodName.IndexOf('('));

        var typePrefix = typeName + "::";
        var typeIndexed = false;
        var methodIndexed = false;

        foreach (var indexed in index.Keys)
        {
            if (!indexed.StartsWith(typePrefix, StringComparison.Ordinal))
                continue;

            typeIndexed = true;

            if (indexed.StartsWith(typePrefix + methodName + "(", StringComparison.Ordinal))
            {
                methodIndexed = true;
                break;
            }
        }

        var reason = methodIndexed
            ? "type and method are there - the signature differs"
            : typeIndexed
                ? "type is there, method is not"
                : "type is not in the index at all";

        reasons.TryGetValue(reason, out var count);
        reasons[reason] = count + 1;
    }

    private static readonly bool IndexConstructors =
        Environment.GetEnvironmentVariable("CPP2IL_VERIFY_INDEX_CTORS") != "0";

    /// <summary>
    /// Every method of one type, filed under its key - plus its constructors, which are NOT methods.
    ///
    /// GetMethods nu intoarce niciodata un .ctor: intoarce numai MethodInfo, iar un constructor este
    /// ConstructorInfo. Cat a lipsit bucata a doua, fiecare constructor cerut de faza 1 se raporta ca
    /// "type is there, method is not" - masurat pe fisierele reale, 92 de chei ".ctor" cerute si ZERO
    /// raspunse, adica rata de pierdere 100%.
    ///
    /// Merita indexati, nu ocoliti: un .ctor de structura este chiar cazul pentru care exista Tier 2 -
    /// nu intoarce nimic, dar scrie prin receptor, si scrisul ACELA este comportamentul lui. Faza 1 ii
    /// masoara deja asa, fara sa ceara nimic special: isi rezolva metodele dupa token si primeste
    /// ConstructorInfo, iar MethodBase.Invoke(receptor, argumente) pe un constructor il ruleaza PESTE
    /// instanta primita in loc sa aloce una noua.
    /// </summary>
    private static void IndexType(Type type, Dictionary<string, MethodBase> index)
    {
        MethodInfo[] methods;
        try
        {
            methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var method in methods)
            IndexMember(index, method);

        if (!IndexConstructors)
            return;

        ConstructorInfo[] constructors;
        try
        {
            // Numai cei de instanta: .cctor nu este o functie a argumentelor lui si selectorul il refuza
            // oricum, deci indexarea lui ar fi doar zgomot.
            constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var constructor in constructors)
            IndexMember(index, constructor);
    }

    private static void IndexMember(Dictionary<string, MethodBase> index, MethodBase method)
    {
        if (method.IsGenericMethodDefinition || method.ContainsGenericParameters)
            return;

        string key;
        try
        {
            key = MethodKeys.For(method);
        }
        catch (Exception)
        {
            return;
        }

        // Cheia ciocnita se SCOATE din pereche, nu se atribuie primei venite.
        //
        // Normalizarea este o ingrosare - '<', '>' si '.' devin toate '_', iar instantierile generice
        // isi pierd assembly-ul si versiunea - deci doua metode care inainte aveau chei diferite pot
        // ajunge acum pe aceeasi. "Prima castiga" ar alege atunci la intamplare care metoda a jocului
        // este perechea, iar o pereche gresita este mai rea decat una lipsa: da fie un "nu se comporta
        // la fel" mincinos, fie, mult mai rau, un "se comporta la fel" mincinos. O cheie lipsa se vede
        // in dump ca "cheia nu exista in indexul jocului" si se poate numara; o pereche gresita nu se
        // vede nicaieri.
        if (KeyCollisions.Contains(key))
            return;

        if (index.TryGetValue(key, out var already))
        {
            // Aceeasi metoda vazuta de doua ori nu este o ciocnire. Reflectia are voie sa dea alt obiect
            // MethodInfo pentru acelasi membru, deci intrebarea se pune si prin Equals, nu numai prin
            // identitatea de referinta.
            if (ReferenceEquals(already, method) || already.Equals(method))
                return;

            index.Remove(key);
            KeyCollisions.Record(key, already, method);
            return;
        }

        index[key] = method;
    }
}

/// <summary>
/// Cheile pe care au cazut doua metode DIFERITE ale jocului, si din ce fel de suprapunere au iesit.
///
/// Se tin separat de index fiindca o cheie ciocnita nu se repara ignorand a doua venita: prima a intrat
/// deja in index si ar ramane acolo drept pereche a ceva ce nu i se cuvine. Dar numarul gol nu spune
/// nimic singur - 12.041 de ciocniri inseamna cu totul altceva daca vin dintr-un singur assembly decat
/// daca vin din suprapunerea dintre proiectia jocului si runtime-ul .NET adevarat. De aceea fiecare
/// ciocnire este si CLASIFICATA:
///
///  - acelasi assembly: doua metode ale aceluiasi cod nu se mai deosebesc dupa normalizare. NUMAI astea
///    pun sub semnul intrebarii o regula de normalizare, fiindca numai aici normalizarea a pierdut o
///    deosebire adevarata.
///  - interop contra gazda: tipul proiectat al jocului si tipul .NET adevarat cu acelasi nume -
///    Il2CppSystem.String si System.String ajung amandoua "System.String". Suprapunerea asta este CERUTA
///    de schema, fiindca exact de aceea se taie prefixul Il2Cpp, si exista de dinainte de orice
///    stalcire; pana acum "prima castiga" o rezolva dand cu banul, ceea ce inseamna ca jumatate din
///    cazuri se legau de metoda .NET in loc de cea a jocului.
///  - assembly-uri diferite: doua proiectii diferite, sau doua assembly-uri ale gazdei.
/// </summary>
internal static class KeyCollisions
{
    // Cate exemple se scriu pe disc. Destule cat sa se vada tiparul, putine cat sa nu coste nimic langa
    // un joc care foloseste deja cea mai mare parte din cei 16 GB.
    private const int SampleLimit = 200;

    private static readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal);
    private static readonly List<string> Samples = new List<string>();

    private static int _sameAssembly;
    private static int _interopVsHost;
    private static int _otherCross;

    /// <summary>Directorul din care se incarca proiectiile Il2CppInterop. Gol cand nu se stie.</summary>
    public static string InteropDirectory { get; set; }

    public static int Count => Keys.Count;

    public static bool Contains(string key) => Keys.Contains(key);

    public static void Reset()
    {
        Keys.Clear();
        Samples.Clear();
        _sameAssembly = 0;
        _interopVsHost = 0;
        _otherCross = 0;
    }

    public static void Record(string key, MethodBase first, MethodBase second)
    {
        Keys.Add(key);

        var left = first.DeclaringType?.Assembly;
        var right = second.DeclaringType?.Assembly;

        string bucket;
        if (ReferenceEquals(left, right))
        {
            _sameAssembly++;
            bucket = "acelasi-assembly";
        }
        else if (IsInterop(left) != IsInterop(right))
        {
            _interopVsHost++;
            bucket = "interop-contra-gazda";
        }
        else
        {
            _otherCross++;
            bucket = "assembly-uri-diferite";
        }

        if (Samples.Count < SampleLimit)
            Samples.Add(bucket + "\t" + key + "\t" + Where(first) + "\t" + Where(second));
    }

    public static string Summary() =>
        "acelasi assembly: " + _sameAssembly +
        " | interop contra gazda: " + _interopVsHost +
        " | assembly-uri diferite: " + _otherCross;

    public static void WriteSamples(string path, Action<string> log)
    {
        try
        {
            var lines = new List<string> { "categorie\tcheie\tprima\ta doua" };
            lines.AddRange(Samples);
            File.WriteAllLines(path, lines);
            log("  primele " + Samples.Count + " ciocniri scrise in " + Path.GetFileName(path));
        }
        catch (Exception)
        {
            // Un fisier de diagnostic care nu se poate scrie nu are voie sa opreasca indexarea.
        }
    }

    private static string Where(MethodBase method)
    {
        var type = method.DeclaringType;
        var assembly = type?.Assembly.GetName().Name ?? "?";
        return assembly + ":" + (type?.FullName ?? "?") + "::" + method.Name;
    }

    private static bool IsInterop(Assembly assembly)
    {
        if (assembly == null || string.IsNullOrEmpty(InteropDirectory))
            return false;

        string location;
        try
        {
            location = assembly.Location;
        }
        catch (Exception)
        {
            // Un assembly incarcat din memorie nu are fisier, deci nu este o proiectie de pe disc.
            return false;
        }

        if (string.IsNullOrEmpty(location))
            return false;

        var directory = Path.GetDirectoryName(location);
        if (directory == null)
            return false;

        return string.Equals(
            directory.TrimEnd('\\', '/'),
            InteropDirectory.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
    }
}
