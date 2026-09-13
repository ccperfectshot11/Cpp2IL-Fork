using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Cpp2IL.VerifyCore;

namespace Cpp2IL.VerifyMod;

/// <summary>
/// Codul recuperat, incarcat in procesul JOCULUI.
///
/// Cele doua implementari trebuie sa stea in ACELASI proces, fiindca numai asa pot primi exact aceleasi
/// argumente: o comparatie intre doua procese nu poate trece granita decat cu un hash, si atunci nu se
/// stie niciodata daca doua valori "identice" chiar au fost identice. Deci assembly-urile recuperate se
/// incarca langa cele ale jocului, fara sa se calce reciproc pe identitati.
///
/// De ce un context separat, si nu Assembly.LoadFrom: build-ul recuperat contine propriul mscorlib.dll,
/// propriul UnityEngine.CoreModule.dll si asa mai departe, adica exact numele pe care le poarta si
/// assembly-urile gazdei. Incarcate in contextul implicit, procesul ar avea doua System.Int32 si fiecare
/// Invoke ar cadea cu o nepotrivire de argumente care se citeste exact ca o eroare in codul recuperat.
/// Acelasi rationament si aceeasi lista ca in Cpp2IL.VerifyPlan/RecoveredContext.cs - repetat
/// aici, nu referit, fiindca modul nu poate lua o dependinta pe unealta de desktop.
/// </summary>
internal sealed class RecoveredCode
{
    private sealed class Context : AssemblyLoadContext
    {
        private readonly string _directory;

        public Context(string directory) : base("cpp2il-recovered-ingame", isCollectible: false) => _directory = directory;

        // Numai cele care definesc primitivele insele sunt imprumutate de la gazda. Restul - inclusiv
        // UnityEngine.CoreModule - trebuie sa vina din directorul recuperat, chiar daca acolo este un
        // ciot: in procesul jocului "UnityEngine.CoreModule" inseamna proiectia Il2CppInterop, ale carei
        // tipuri nu au nimic de-a face cu cele pe care le astepta codul recuperat, deci o cadere in
        // contextul implicit ar da TypeLoadException in loc de un ciot inofensiv.
        private static readonly HashSet<string> HostOwned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mscorlib", "netstandard", "System.Runtime", "System.Private.CoreLib",
        };

        public static bool IsHostOwned(string name) => name != null && HostOwned.Contains(name);

        protected override Assembly Load(AssemblyName name)
        {
            if (name.Name == null || HostOwned.Contains(name.Name))
                return null;

            var candidate = Path.Combine(_directory, name.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }

    private readonly Context _context;
    private readonly Dictionary<string, Assembly> _loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, MethodBase>> _indexes = new Dictionary<string, Dictionary<string, MethodBase>>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Assembly> _mine = new HashSet<Assembly>();

    public RecoveredCode(string directory)
    {
        Directory = directory;
        _context = new Context(directory);
    }

    public string Directory { get; }

    /// <summary>
    /// Adevarat daca assembly-ul acesta a fost incarcat de AICI, nu de joc.
    ///
    /// Are un rost foarte concret. Indexul jocului se construieste parcurgand
    /// AppDomain.CurrentDomain.GetAssemblies(), iar acolo apar si assembly-urile incarcate intr-un context
    /// separat. Daca indexul ar fi construit DUPA ce s-a incarcat Assembly-CSharp recuperat, metoda
    /// "jocului" gasita dupa cheie ar putea fi chiar metoda recuperata - si atunci am compara codul
    /// recuperat cu el insusi, care este perfect de acord si nu inseamna nimic. Este exact capcana pe
    /// care o ocoleste si indexul jocului sarind assembly-urile "Cpp2IL.*", doar ca aici numele nu ne mai ajuta:
    /// assembly-ul recuperat se cheama chiar "Assembly-CSharp".
    /// </summary>
    public bool Owns(Assembly assembly) => assembly != null && _mine.Contains(assembly);

    /// <summary>
    /// Un singur assembly recuperat, dupa nume. Incarcarea este lenesa si pe bucati DINADINS: corpusul
    /// are 150 de DLL-uri, Assembly-CSharp singur are 13 MB, iar masina are 16 GB din care jocul foloseste
    /// deja cea mai mare parte. Recensamantul de pe desktop le-a incarcat pe toate si a fost omorat de
    /// memorie; aici ar lua jocul cu el.
    /// </summary>
    public Assembly Load(string assemblyName)
    {
        if (_loaded.TryGetValue(assemblyName, out var already))
            return already;

        if (Context.IsHostOwned(assemblyName))
        {
            _loaded[assemblyName] = null;
            return null;
        }

        var path = Path.Combine(Directory, assemblyName + ".dll");
        Assembly assembly = null;

        try
        {
            if (File.Exists(path))
                assembly = _context.LoadFromAssemblyPath(path);
        }
        catch (Exception)
        {
            // Un DLL care nu se incarca costa acel assembly, nu rularea.
        }

        _loaded[assemblyName] = assembly;
        if (assembly != null)
            _mine.Add(assembly);

        return assembly;
    }

    /// <summary>
    /// Metodele unui assembly recuperat, indexate dupa ACEEASI cheie pe care o foloseste si indexul jocului pentru
    /// metodele jocului. Asta este tot ce face legatura dintre cele doua parti: nu tokenul, care este
    /// atribuit independent de Cpp2IL si de Il2CppInterop, ci numele normalizat plus forma semnaturii.
    /// </summary>
    public Dictionary<string, MethodBase> Index(string assemblyName)
    {
        // Memorat, si nu din eleganta: harnasul cere indexul o data pentru fiecare metoda din lista de
        // lucru, iar Assembly-CSharp recuperat are zeci de mii de metode. Fara memorare, o lista de o mie
        // de metode ar reface reflectia de o mie de ori peste acelasi assembly.
        if (_indexes.TryGetValue(assemblyName, out var cached))
            return cached;

        var index = new Dictionary<string, MethodBase>(StringComparer.Ordinal);
        _indexes[assemblyName] = index;

        // Cheile pe care au cazut doua metode diferite ALE ACESTUI assembly. Numai ale lui: indexul este
        // per assembly, deci doua assembly-uri au voie sa foloseasca aceeasi cheie fara sa se incurce.
        var collided = new HashSet<string>(StringComparer.Ordinal);

        var assembly = Load(assemblyName);
        if (assembly == null)
            return index;

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            // Metadatele recuperate promit membri care nu exista - 20.431 de metode din recensamant au
            // cazut exact asa. Tipurile care s-au incarcat raman utilizabile si nu au de ce sa piarda
            // intregul assembly.
            var usable = new List<Type>();
            foreach (var type in partial.Types)
                if (type != null)
                    usable.Add(type);

            types = usable.ToArray();
        }
        catch (Exception)
        {
            return index;
        }

        foreach (var type in types)
        {
            MethodBase[] methods;
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

                // Si constructorii de instanta, nu numai metodele, si exact aceiasi pe care ii indexeaza si
                // partea jocului. Un corp de constructor nu face aproape nimic altceva decat sa scrie
                // campuri, iar campurile se citesc inapoi dupa apel - deci constructorii sunt printre cele
                // mai bine masurabile tinte pe care le are unealta. Daca ar fi indexati doar de o parte,
                // fiecare dintre ei ar cadea tacut ca "cheia nu mai este in indexul recuperat".
                var declared = type.GetMethods(flags);
                var constructors = type.GetConstructors(flags & ~BindingFlags.Static);

                methods = new MethodBase[declared.Length + constructors.Length];
                Array.Copy(declared, methods, declared.Length);
                Array.Copy(constructors, 0, methods, declared.Length, constructors.Length);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var method in methods)
            {
                if (method.IsGenericMethodDefinition || method.ContainsGenericParameters || method.IsAbstract)
                    continue;

                string key;
                try
                {
                    key = MethodKeys.For(method);
                }
                catch (Exception)
                {
                    continue;
                }

                // Cheia ciocnita se scoate din pereche, exact ca la indexul jocului: doua metode pe care
                // cheia normalizata nu le mai deosebeste nu se pot compara cu nimic, fiindca nu se stie
                // care dintre ele este cea masurata. Ignorand doar a doua venita, prima ar ramane in
                // index si ar fi chemata drept pereche a celeilalte.
                if (collided.Contains(key))
                    continue;

                if (index.TryGetValue(key, out var already))
                {
                    // Aceeasi metoda vazuta de doua ori nu este o ciocnire: reflectia are voie sa dea alt
                    // obiect MethodInfo pentru acelasi membru.
                    if (ReferenceEquals(already, method) || already.Equals(method))
                        continue;

                    index.Remove(key);
                    collided.Add(key);
                    continue;
                }

                index[key] = method;
            }
        }

        Collisions += collided.Count;
        return index;
    }

    /// <summary>
    /// Cate chei recuperate au fost scoase din pereche fiindca doua metode diferite au cazut pe ele.
    /// Se aduna peste toate assembly-urile indexate pana acum si se raporteaza dupa dump: daca numarul
    /// creste dupa o schimbare de normalizare, normalizarea a devenit prea grosolana.
    /// </summary>
    public int Collisions { get; private set; }
}
