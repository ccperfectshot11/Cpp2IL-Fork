using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Cpp2IL.VerifyCore;

namespace Cpp2IL.VerifyMod;

/// <summary>
/// Indexul jocului, scos pe disc o singura data.
///
/// Prima dintre cele trei bucati, si singura care are neaparat nevoie de joc: numai un proces in care
/// ruleaza Il2CppInterop stie ce metode are build-ul si ce forma au ele. Sesiunea asta nu cheama NIMIC -
/// doar enumera - deci nu poate omori procesul si nu are nevoie de jurnal, de lista de sarituri sau de
/// reporniri.
///
/// Se face o data si se refoloseste. Tot ce urmeaza - pereche, clasificare, argumente - se face pe disc,
/// pornind de la fisierele scrise aici.
/// </summary>
internal static class GameIndexDump
{
    /// <summary>
    /// Indexeaza si constructorii de instanta, nu numai metodele.
    ///
    /// Merita spus de ce, fiindca unealta de dinainte ii lasa afara. Un corp de constructor nu face
    /// aproape nimic altceva decat sa scrie campuri, iar campurile se pot citi inapoi dupa apel - deci un
    /// constructor este una dintre cele mai bine masurabile tinte pe care le are unealta, tocmai el.
    /// ConstructorInfo.Invoke(obiect, argumente) cheama corpul peste un obiect deja alocat, adica exact
    /// peste receptorul fabricat de noi pe amandoua partile.
    ///
    /// .cctor nu intra: nu este o functie a argumentelor lui si nu se cheama direct.
    /// </summary>
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    public static void Write(string directory, string interopDirectory, Action<string> log)
    {
        var keys = new Dictionary<string, MethodBase>(StringComparer.Ordinal);
        var collided = new HashSet<string>(StringComparer.Ordinal);
        var receiverTypes = new Dictionary<string, Type>(StringComparer.Ordinal);

        Fill(interopDirectory, keys, collided, receiverTypes, log);

        WriteIndex(Path.Combine(directory, PlanFiles.GameIndexFile), keys, collided, log);
        WriteFields(Path.Combine(directory, PlanFiles.GameFieldsFile), receiverTypes, log);
    }

    /// <summary>
    /// Enumerarea propriu-zisa, folosita si de scrierea fisierului si de harnas.
    ///
    /// Este chiar acelasi cod in amandoua locurile, si trebuie sa fie: fisierul poarta cheile pe care le
    /// planifica planificatorul, iar harnasul cauta metodele vii tot dupa ele. Doua enumerari care s-ar
    /// deosebi fie si printr-un BindingFlags ar face ca o parte din lista de lucru sa nu gaseasca niciodata
    /// perechea, si asta s-ar citi ca "jocul nu are metoda" in loc de "le-am cautat altfel".
    /// </summary>
    public static void Fill(string interopDirectory, Dictionary<string, MethodBase> keys,
        HashSet<string> collided, Dictionary<string, Type> receiverTypes, Action<string> log)
    {
        LoadEveryInteropAssembly(interopDirectory, log);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            // Assembly-urile modului ar indexa codul RECUPERAT de langa el si l-ar compara cu el insusi,
            // ceea ce este perfect de acord si nu inseamna nimic. In sesiunea de index codul recuperat nici
            // macar nu se incarca, dar verificarea ramane: e mai ieftina decat descoperirea ei a doua oara.
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
                var usable = new List<Type>();
                foreach (var type in partial.Types)
                    if (type != null)
                        usable.Add(type);

                types = usable.ToArray();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var type in types)
                Index(type, keys, collided, receiverTypes);
        }

        log("Indexul jocului: " + keys.Count + " metode, " + collided.Count + " chei scoase din pereche prin ciocnire.");
    }

    private static void Index(Type type, Dictionary<string, MethodBase> keys, HashSet<string> collided,
        Dictionary<string, Type> receiverTypes)
    {
        MethodInfo[] methods;
        ConstructorInfo[] constructors;

        try
        {
            methods = type.GetMethods(Members);
            constructors = type.GetConstructors(Members & ~BindingFlags.Static);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var method in methods)
            Add(keys, collided, receiverTypes, method);

        foreach (var constructor in constructors)
            Add(keys, collided, receiverTypes, constructor);
    }

    private static void Add(Dictionary<string, MethodBase> keys, HashSet<string> collided,
        Dictionary<string, Type> receiverTypes, MethodBase method)
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
        // Normalizarea este o ingrosare - '<', '>' si '.' devin toate '_', iar instantierile generice isi
        // pierd assembly-ul si versiunea - deci doua metode care inainte aveau chei diferite pot ajunge
        // acum pe aceeasi. "Prima castiga" ar alege atunci la intamplare care metoda a jocului este
        // perechea, iar o pereche gresita este mai rea decat una lipsa: da fie un "nu se comporta la fel"
        // mincinos, fie, mult mai rau, un "se comporta la fel" mincinos. O cheie lipsa se vede in fisier si
        // se poate numara; o pereche gresita nu se vede nicaieri.
        if (collided.Contains(key))
            return;

        if (keys.TryGetValue(key, out var already))
        {
            if (ReferenceEquals(already, method) || already.Equals(method))
                return;

            keys.Remove(key);
            collided.Add(key);
            return;
        }

        keys[key] = method;

        if (!method.IsStatic && method.DeclaringType != null)
        {
            var name = Name(method.DeclaringType);
            if (!receiverTypes.ContainsKey(name))
                receiverTypes[name] = method.DeclaringType;
        }
    }

    private static void WriteIndex(string path, Dictionary<string, MethodBase> keys, HashSet<string> collided, Action<string> log)
    {
        using (var writer = new StreamWriter(path, false))
        {
            writer.WriteLine(PlanFiles.Header(PlanFiles.GameIndexColumns));

            foreach (var pair in keys)
                writer.WriteLine(Describe(pair.Key, pair.Value, ""));

            // Cheile ciocnite se scriu si ele, cu semnul lor. Fara randul asta, planificatorul nu ar putea
            // deosebi "cheia nu exista in joc" - o normalizare care inca nu ajunge la forma jocului, deci
            // ceva de reparat - de "cheia a iesit din pereche fiindca doua metode au cazut pe ea", care
            // este pretul normalizarii si se plateste stiind cat este.
            foreach (var key in collided)
                writer.WriteLine(PlanFiles.Row(key, "", "", "", "0", "", "", "", "collided"));
        }

        log("Scris " + PlanFiles.GameIndexFile + ": " + keys.Count + " metode + " + collided.Count + " chei ciocnite.");
    }

    /// <summary>
    /// Campurile de instanta ale fiecarui tip al jocului care poate fi receptor.
    ///
    /// Aici este un amanunt care schimba totul si care e usor de ratat: un camp al jocului NU este un camp
    /// CLR pe partea jocului. Pentru un tip referinta, Il2CppInterop genereaza un invelis care nu are campuri
    /// de instanta deloc - are o PROPRIETATE pentru fiecare camp il2cpp, al carei getter si setter citesc si
    /// scriu memoria nativa la offsetul potrivit. Cerute cu GetFields, campurile acelea nu exista. Pentru un
    /// tip valoare, dimpotriva, invelisul chiar este o structura CLR obisnuita cu campuri adevarate. Deci se
    /// cauta in AMANDOUA locurile, si tot in amandoua se scrie mai tarziu, cand se seamana receptorul.
    ///
    /// Se scriu NUMAI membrii care se pot semana identic pe amandoua partile: primitive, enum-uri si siruri.
    /// Restul nu au ce cauta in fisier - un camp de tip clasa nu se poate umple la fel in cele doua universuri
    /// de tipuri, deci planificatorul nu are ce sa faca cu el si prezenta lui ar umfla fisierul degeaba.
    ///
    /// Fara fisierul asta, singura observatie posibila pentru cele 17.555 de metode care intorc void si
    /// pentru cele 27.160 de metode de instanta ar fi "nu a crapat".
    /// </summary>
    private static void WriteFields(string path, Dictionary<string, Type> receiverTypes, Action<string> log)
    {
        var rows = 0;
        var types = 0;

        using (var writer = new StreamWriter(path, false))
        {
            writer.WriteLine(PlanFiles.Header(PlanFiles.GameFieldColumns));

            foreach (var pair in receiverTypes)
            {
                var wrote = 0;

                foreach (var member in SeedableMembers(pair.Value))
                {
                    writer.WriteLine(PlanFiles.Row(pair.Key, member.Name, member.TypeName, member.Kind));
                    rows++;
                    wrote++;
                }

                if (wrote > 0)
                    types++;
            }
        }

        log("Scris " + PlanFiles.GameFieldsFile + ": " + rows + " campuri semanabile pe " + types
            + " tipuri din " + receiverTypes.Count + " cercetate.");
    }

    private struct Seedable
    {
        public string Name;
        public string TypeName;
        public string Kind;
    }

    private static List<Seedable> SeedableMembers(Type type)
    {
        var found = new List<Seedable>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                // Si citit si scris: un camp care nu se poate scrie nu se poate semana, iar unul care nu se
                // poate citi nu se poate privi dupa apel. Fara amandoua, membrul nu foloseste la nimic.
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)
                    continue;

                var kind = KindOf(property.PropertyType);
                if (kind == null || !seen.Add(property.Name))
                    continue;

                found.Add(new Seedable { Name = property.Name, TypeName = Name(property.PropertyType), Kind = kind });
            }
        }
        catch (Exception)
        {
            // Un tip pe care reflectia refuza sa il descrie costa acel tip, nu fisierul.
        }

        try
        {
            foreach (var field in ValueShape.InstanceFields(type))
            {
                if (field.IsLiteral || field.IsInitOnly)
                    continue;

                var kind = KindOf(field.FieldType);
                if (kind == null || !seen.Add(field.Name))
                    continue;

                found.Add(new Seedable { Name = field.Name, TypeName = Name(field.FieldType), Kind = kind });
            }
        }
        catch (Exception)
        {
        }

        return found;
    }

    private static string KindOf(Type type)
    {
        try
        {
            if (type == typeof(string))
                return "string";

            var shape = ValueShape.For(type);
            return shape != null && shape.IsLeaf ? shape.Kind.ToString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Describe(string key, MethodBase method, string flags)
    {
        var parameters = new StringBuilder();

        try
        {
            var all = method.GetParameters();
            for (var i = 0; i < all.Length; i++)
            {
                if (i > 0)
                    parameters.Append('|');

                parameters.Append(Name(all[i].ParameterType));
            }
        }
        catch (Exception)
        {
            parameters.Clear();
            parameters.Append('?');
        }

        var declaring = method.DeclaringType;

        return PlanFiles.Row(
            key,
            Name(declaring?.Assembly),
            Name(declaring),
            method.Name,
            method.IsStatic ? "1" : "0",
            method.IsStatic ? "" : Name(declaring),
            parameters.ToString(),
            Name((method as MethodInfo)?.ReturnType),
            flags);
    }

    private static string Name(Assembly assembly)
    {
        try
        {
            return TargetUniverse.Strip(assembly?.GetName().Name ?? "");
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string Name(Type type)
    {
        if (type == null)
            return "void";

        try
        {
            return TargetUniverse.Strip(type.FullName ?? type.Name);
        }
        catch (Exception)
        {
            return "?";
        }
    }

    /// <summary>
    /// Forteaza incarcarea fiecarui assembly Il2CppInterop inainte de indexare.
    ///
    /// Se incarca lenes - abia cand ceva atinge primul tip din ele - deci la pornirea modului domeniul tine
    /// doar ce a avut jocul nevoie pana atunci, iar restul pur si simplu lipseste. Indexat asa, indexul
    /// pare intreg si nu este: 273 de metode s-au raportat odata "nu se gasesc" fata de un build care sigur
    /// le contine, iar cele disparute lipseau cu tipul intreg - FPMathUtils, UIGradientUtils,
    /// BattlePassLevel - ceea ce este chiar felul in care arata din afara un assembly neincarcat.
    /// </summary>
    private static void LoadEveryInteropAssembly(string directory, Action<string> log)
    {
        if (!Directory.Exists(directory))
        {
            log("ATENTIE: nu exista " + directory + "; indexul va cuprinde numai ce este deja incarcat.");
            return;
        }

        var loaded = 0;

        foreach (var path in Directory.GetFiles(directory, "*.dll"))
        {
            try
            {
                // Dupa NUME, nu din fisier: incarcat a doua oara de pe cale, acelasi assembly ar capata o
                // identitate dublata si fiecare tip din el ar iesi diferit de cel al jocului.
                Assembly.Load(AssemblyName.GetAssemblyName(path));
                loaded++;
            }
            catch (Exception)
            {
                // O dependinta care nu se rezolva costa acel assembly, nu rularea.
            }
        }

        log("Incarcate " + loaded + " assembly-uri interop inainte de indexare.");
    }
}
