using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Cpp2IL.VerifyCore;

namespace Cpp2IL.VerifyPlan;

/// <summary>
/// Codul recuperat, incarcat pe DISC exact asa cum il incarca harnasul in joc.
///
/// "Exact asa" este intreaga valoare a clasei. Verdictul pe care il da unealta - trece sau nu trece de JIT
/// - atarna numai de doua lucruri: corpul recuperat si regulile dupa care se leaga referintele lui. Corpul
/// este acelasi fisier. Regulile trebuie sa fie aceleasi reguli, altfel verdictul dat aici nu spune nimic
/// despre ce se va intampla acolo, iar sesiunea de joc pe care voiam sa o economisim se pierde oricum.
///
/// Regula, aceeasi ca in Cpp2IL.VerifyMod/RecoveredCode.cs: un context separat, in care numai
/// assembly-urile ce definesc primitivele insele sunt imprumutate de la gazda, iar tot restul - inclusiv
/// UnityEngine.CoreModule - vine din directorul recuperat. Build-ul recuperat isi are propriul mscorlib.dll
/// si propriul UnityEngine.CoreModule.dll, adica exact numele pe care le poarta si assembly-urile gazdei;
/// incarcate in contextul implicit, procesul ar avea doua System.Int32 si fiecare apel ar cadea cu o
/// nepotrivire de argumente care se citeste exact ca o eroare in codul recuperat.
///
/// Aici este si radacina celei mai mari categorii de esec masurate pana acum, si merita spus pe fata:
/// fiindca mscorlib se imprumuta de la GAZDA, orice corp recuperat care cere un amanunt intern al BCL-ului
/// Mono cu care a fost compilat jocul - System.ThrowHelper, System.SpanHelpers, campul
/// RuntimeTypeHandle.value - nu are de unde sa il ia. Din 1.931 de refuzuri masurate, aproximativ 900 sunt
/// de felul asta. Nu este o greseala de recuperare, este o nepotrivire intre doua BCL-uri, si tocmai de
/// aceea trebuie aflata pe disc, unde nu costa nimic, si scrisa cu numele ei.
/// </summary>
internal sealed class RecoveredContext : AssemblyLoadContext
{
    private static readonly HashSet<string> HostOwned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib", "netstandard", "System.Runtime", "System.Private.CoreLib",
    };

    private readonly string _directory;
    private readonly Dictionary<string, Assembly> _loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, MethodBase>> _indexes = new Dictionary<string, Dictionary<string, MethodBase>>(StringComparer.OrdinalIgnoreCase);

    public RecoveredContext(string directory) : base("cpp2il-recovered-offline", isCollectible: false)
    {
        _directory = directory;
        Directory = directory;
    }

    public string Directory { get; }

    /// <summary>Cheile pe care au cazut doua metode diferite ALE ACELUIASI assembly recuperat.</summary>
    public HashSet<string> Collisions { get; } = new HashSet<string>(StringComparer.Ordinal);

    protected override Assembly Load(AssemblyName name)
    {
        if (name.Name == null || HostOwned.Contains(name.Name))
            return null;

        var candidate = Path.Combine(_directory, name.Name + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }

    public Assembly Get(string assemblyName)
    {
        if (_loaded.TryGetValue(assemblyName, out var already))
            return already;

        Assembly assembly = null;

        if (!HostOwned.Contains(assemblyName))
        {
            var path = Path.Combine(_directory, assemblyName + ".dll");

            try
            {
                if (File.Exists(path))
                    assembly = LoadFromAssemblyPath(path);
            }
            catch (Exception)
            {
                // Un DLL care nu se incarca costa acel assembly, nu rularea.
            }
        }

        _loaded[assemblyName] = assembly;
        return assembly;
    }

    /// <summary>
    /// Metodele unui assembly recuperat, indexate dupa ACEEASI cheie pe care o da si indexul jocului.
    /// Asta este tot ce leaga cele doua parti: nu tokenul, care este atribuit independent de Cpp2IL si de
    /// Il2CppInterop, ci numele normalizat plus forma semnaturii.
    ///
    /// Cheia ciocnita se SCOATE din pereche, nu se atribuie primei venite: doua metode pe care cheia
    /// normalizata nu le mai deosebeste nu se pot compara cu nimic, fiindca nu se stie care dintre ele
    /// este cea masurata. O pereche gresita este mai rea decat una lipsa - cea lipsa se vede in
    /// numaratoare, cea gresita se crede.
    /// </summary>
    public Dictionary<string, MethodBase> Index(string assemblyName)
    {
        if (_indexes.TryGetValue(assemblyName, out var cached))
            return cached;

        var index = new Dictionary<string, MethodBase>(StringComparer.Ordinal);
        _indexes[assemblyName] = index;

        var assembly = Get(assemblyName);
        if (assembly == null)
            return index;

        var collided = new HashSet<string>(StringComparer.Ordinal);

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
            MethodBase[] members;
            try
            {
                members = Members(type);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var method in members)
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

                if (collided.Contains(key))
                    continue;

                if (index.TryGetValue(key, out var already))
                {
                    if (ReferenceEquals(already, method) || already.Equals(method))
                        continue;

                    index.Remove(key);
                    collided.Add(key);
                    Collisions.Add(key);
                    continue;
                }

                index[key] = method;
            }
        }

        return index;
    }

    private static MethodBase[] Members(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        var methods = type.GetMethods(flags);
        var constructors = type.GetConstructors(flags & ~BindingFlags.Static);

        var all = new MethodBase[methods.Length + constructors.Length];
        Array.Copy(methods, all, methods.Length);
        Array.Copy(constructors, 0, all, methods.Length, constructors.Length);
        return all;
    }
}
