using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Cpp2IL.VerifyCore;

namespace Cpp2IL.VerifyPlan;

/// <summary>O metoda care a trecut de clasificare si asteapta sa i se planifice argumentele.</summary>
internal sealed class Candidate
{
    public string Key;
    public string Assembly;
    public MethodBase Recovered;
    public GameIndex.Entry Game;
}

/// <summary>Un rand gata de scris in lista de lucru.</summary>
internal sealed class PlannedCall
{
    public string Key;
    public string Assembly;
    public string Receiver;
    public string ReceiverSeed;
    public string Arguments;
    public string ReturnPlan;
    public string Quality;
    public int Weight;
}

/// <summary>De ce o metoda nu ajunge in lista. Un rezultat despre ea, nu o metoda pierduta.</summary>
internal sealed class BlockedCall
{
    public string Key;
    public string Assembly;
    public string Reason;
    public string Detail;
}

/// <summary>
/// Planificarea, adica tot ce se poate hotari fara joc.
///
/// Regula care da forma intregii clase: jocul se porneste ca sa EXECUTE o lista gata facuta, nu ca sa
/// descopere ce are de facut. Pana acum fiecare necunoscuta - se poate chema? cu ce? are pereche? - se
/// lamurea in joc, iar fiecare lamurire costa o repornire de patruzeci de secunde. Masurat: unsprezece
/// reporniri au produs treizeci si sapte de metode masurate. Pe un univers de 47.012, aritmetica aceea nu
/// iese niciodata.
/// </summary>
internal static class Planner
{
    /// <summary>
    /// Cate valori de proba primeste un tip de referinta cand nu se poate fabrica nimic mai bun. Nu se
    /// foloseste inca la nimic altceva decat la a numara: null-urile ramase sunt chiar lista de lucru a
    /// urmatoarei imbunatatiri, si trebuie sa se poata numara pe tip.
    /// </summary>
    public static readonly Dictionary<string, int> NullsByType = new Dictionary<string, int>(StringComparer.Ordinal);

    public static void Classify(RecoveredContext recovered, GameIndex game, string[] assemblies,
        string only, string skip, List<Candidate> candidates, List<BlockedCall> blocked, Action<string> log)
    {
        var excluded = TargetUniverse.Split(skip);

        foreach (var assembly in assemblies)
        {
            // Filtrele de linie de comanda, ca universul sa se poata masura pe felii fara recompilare. Un
            // filtru gol inseamna "tot", nu "nimic": altfel un argument uitat ar goli tacut universul si
            // planificarea ar raporta zero metode ca si cum n-ar fi fost nimic de masurat.
            if (!TargetUniverse.Matches(assembly, only) || excluded.Contains(assembly))
                continue;

            var universe = TargetUniverse.Classify(assembly);
            if (universe == AssemblyClass.Stubbed)
            {
                blocked.Add(new BlockedCall { Key = "*", Assembly = assembly, Reason = Blocked.StubbedModule, Detail = "Cpp2IL inlocuieste fiecare corp cu un ciot" });
                continue;
            }

            if (universe == AssemblyClass.PublicLibrary)
            {
                blocked.Add(new BlockedCall { Key = "*", Assembly = assembly, Reason = Blocked.PublicLibrary, Detail = "originalul se poate lua de la autor" });
                continue;
            }

            var index = recovered.Index(assembly);
            var before = candidates.Count;

            foreach (var pair in index)
            {
                var reason = Reject(assembly, pair.Value, pair.Key, game, out var detail);
                if (reason != null)
                {
                    blocked.Add(new BlockedCall { Key = pair.Key, Assembly = assembly, Reason = reason, Detail = detail });
                    continue;
                }

                game.TryGet(pair.Key, out var entry);
                candidates.Add(new Candidate { Key = pair.Key, Assembly = assembly, Recovered = pair.Value, Game = entry });
            }

            log("  " + assembly + ": " + (candidates.Count - before) + " candidati din " + index.Count + " metode recuperate.");
        }
    }

    /// <summary>
    /// De ce NU. Ordinea conteaza: se raspunde cu cel dintai motiv care se aplica, iar lista de siguranta
    /// sta inaintea oricarui motiv tehnic - o metoda de plati nu are de ce sa fie cercetata mai departe.
    /// </summary>
    private static string Reject(string assembly, MethodBase method, string key, GameIndex game, out string detail)
    {
        detail = "";

        var type = SafeTypeName(method.DeclaringType);

        if (!SubstSafety.MayTouch(assembly, type))
        {
            detail = "lista de siguranta pe tip (plati, cont, telemetrie, retea)";
            return Blocked.Safety;
        }

        if (TargetUniverse.NeverCall(method.Name))
        {
            detail = "lista de siguranta pe numele metodei";
            return Blocked.Safety;
        }

        if (!game.TryGet(key, out var entry))
        {
            detail = "cheia nu exista in indexul jocului";
            return Blocked.NoGamePair;
        }

        if (entry.Collided)
        {
            detail = "doua metode diferite ale jocului au cazut pe cheia asta";
            return Blocked.KeyCollision;
        }

        if (method.IsAbstract)
        {
            detail = "metoda abstracta - nu are corp de chemat";
            return Blocked.NoBody;
        }

        if (method.IsGenericMethodDefinition || method.ContainsGenericParameters)
        {
            detail = "metoda generica fara instantiere";
            return Blocked.OpenGeneric;
        }

        if (method.IsConstructor && method.IsStatic)
        {
            detail = "constructor static - nu se cheama direct";
            return Blocked.NoBody;
        }

        if (!method.IsStatic)
        {
            var declaring = method.DeclaringType;

            if (declaring == null)
            {
                detail = "metoda de instanta fara tip declarant";
                return Blocked.Abstract;
            }

            if (declaring.IsInterface)
            {
                detail = "receptorul este o interfata - nu se poate aloca";
                return Blocked.Abstract;
            }

            if (declaring.IsAbstract)
            {
                // Nu este o limita a uneltei, este aceeasi limita si la il2cpp_object_new si la
                // GetUninitializedObject. O clasa derivata concreta ar merge, dar atunci metoda chemata ar
                // putea fi suprascrisa si nu am mai compara acelasi corp.
                detail = "tipul declarant este abstract - nu se poate aloca receptor pe niciuna dintre parti";
                return Blocked.Abstract;
            }

            if (declaring.ContainsGenericParameters)
            {
                detail = "tipul declarant este generic deschis";
                return Blocked.OpenGeneric;
            }

            if (declaring.IsByRefLike)
            {
                detail = "tipul declarant este ref struct - nu poate sta intr-un obiect";
                return Blocked.Abstract;
            }
        }

        ParameterInfo[] parameters;
        try
        {
            parameters = method.GetParameters();
        }
        catch (Exception ex)
        {
            detail = "parametrii nu se pot citi: " + ex.GetType().Name;
            return Blocked.NoBody;
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            var t = parameters[i].ParameterType;
            if (t.IsByRef || t.IsPointer || t.IsGenericParameter || t.ContainsGenericParameters)
            {
                detail = "parametrul " + i + " (" + SafeTypeName(t) + ") nu se poate fabrica identic pe cele doua parti";
                return Blocked.ByRefOrPointer;
            }
        }

        var returns = (method as MethodInfo)?.ReturnType;
        if (returns != null && (returns.IsByRef || returns.IsPointer))
        {
            detail = "tipul intors (" + SafeTypeName(returns) + ") nu se poate compara";
            return Blocked.ByRefOrPointer;
        }

        return null;
    }

    // ------------------------------------------------------------------------------------------------
    // Argumentele
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Reteta fiecarui argument si a receptorului.
    ///
    /// Argumentele sunt cetateni de rangul intai aici, nu o completare. Un parametru de tip clasa primeste
    /// null fiindca nu exista nimic mai bun care sa fie IDENTIC pe amandoua partile - nu fiindca este mai
    /// usor - si rezultatul poarta eticheta care spune asta. Un sir primeste un sir adevarat, un tablou de
    /// primitive primeste un tablou plin, un enum primeste una dintre valorile lui declarate.
    /// </summary>
    public static PlannedCall Plan(Candidate candidate, GameIndex game, ulong seed)
    {
        var random = new DeterministicRandom(DeterministicRandom.SeedFor(seed, candidate.Key));
        var method = candidate.Recovered;

        var arguments = new List<Recipe>();
        foreach (var parameter in method.GetParameters())
            arguments.Add(For(parameter.ParameterType, ref random));

        var receiver = "static";
        var seeds = "";
        var seeded = false;

        if (!method.IsStatic)
        {
            receiver = "blank";
            seeds = SeedReceiver(method.DeclaringType, candidate.Game, game, ref random);

            if (seeds.Length > 0)
            {
                receiver = "seeded";
                seeded = true;
            }
        }

        var weight = 0;
        foreach (var recipe in arguments)
            weight += recipe.Weight;

        // Un receptor semanat valoreaza cat un argument adevarat, si din acelasi motiv: este intrarea prin
        // care metoda vede starea. Un receptor gol nu valoreaza nimic - toate campurile pe zero inseamna ca
        // aproape orice metoda de instanta iese pe prima ramura, la fel pe amandoua partile.
        if (seeded)
            weight += 2;

        return new PlannedCall
        {
            Key = candidate.Key,
            Assembly = candidate.Assembly,
            Receiver = receiver,
            ReceiverSeed = seeds,
            Arguments = Recipe.Join(arguments),
            ReturnPlan = ReturnPlanFor((method as MethodInfo)?.ReturnType, method.IsConstructor),
            Quality = Cpp2IL.VerifyCore.Quality.Of(method.IsStatic, seeded, arguments),
            Weight = weight,
        };
    }

    private static Recipe For(Type type, ref DeterministicRandom random)
    {
        if (type == typeof(string))
            return Recipe.OfText(Leaves.RandomText(ref random));

        var shape = ValueShape.For(type);
        if (shape != null)
            return Bits(shape, ref random);

        if (type.IsArray && type.GetArrayRank() == 1)
        {
            var element = type.GetElementType();

            if (element == typeof(string))
                return TextArray(ref random);

            var elementShape = ValueShape.For(element);

            // Numai tablouri de frunze, si numai de frunze simple: un tablou de structuri s-ar putea cladi
            // pe partea noastra, dar pe partea jocului ar trebui asezat in memoria il2cpp cu layout-ul
            // NATIV al elementului, iar daca layout-ul recuperat difera - adica exact ce vrem sa aflam -
            // cele doua tablouri n-ar mai avea acelasi continut si diferenta ar fi a noastra, nu a lor.
            if (elementShape != null && elementShape.IsLeaf)
                return PrimitiveArray(elementShape, ref random);

            Note(type);
            return Recipe.OfNull;
        }

        if (type.IsValueType)
            return Recipe.OfZero;

        Note(type);
        return Recipe.OfNull;
    }

    private static Recipe Bits(ValueShape shape, ref DeterministicRandom random)
    {
        var leaves = new List<ValueShape>();
        shape.CollectLeaves(leaves);

        var kinds = new LeafKind[leaves.Count];
        var bits = new long[leaves.Count];

        for (var i = 0; i < leaves.Count; i++)
        {
            kinds[i] = leaves[i].Kind;
            bits[i] = Leaves.Bits(leaves[i].Kind, LeafValue(leaves[i], ref random));
        }

        return Recipe.OfBits(kinds, bits);
    }

    /// <summary>
    /// Valoarea unei frunze. Pentru un enum se trage dintre valorile DECLARATE, nu de pe tot intervalul
    /// intregului de dedesubt.
    ///
    /// Nu este o rafinare de stil, este o reparatie cu nume si prenume. Dintre cele cinci metode care au
    /// omorat procesul in prima sesiune, doua au exact aceeasi forma - CompressionUtils::GetHttpName(
    /// CompressionAlgorithm) si QualityTermInterpreter::QualityLevelToText(QualityLevel): primesc un enum si
    /// intorc un string. Un joc scrie asa ceva ca o cautare intr-un tabel indexat cu enumul, iar IL2CPP
    /// compilat pentru livrare nu mai emite verificari de interval. Un enum fuzzat cu int.MinValue citeste
    /// atunci mult in afara tabelului si intoarce un pointer de gunoi, pe care invelisul Il2CppInterop il
    /// desface ca pe un obiect - de acolo pana la moartea procesului nu mai e nimic de facut.
    ///
    /// Ce se pierde, spus pe fata: ramura "valoare necunoscuta" a metodei nu mai este exersata. Este un
    /// schimb constient - acea ramura costa, masurat, doua morti de proces din cinci.
    /// </summary>
    private static object LeafValue(ValueShape leaf, ref DeterministicRandom random)
    {
        if (leaf.EnumUnderlying == null)
            return Leaves.Random(leaf.Kind, ref random);

        Array declared;
        try
        {
            declared = Enum.GetValues(leaf.Type);
        }
        catch (Exception)
        {
            return Leaves.Random(leaf.Kind, ref random);
        }

        if (declared.Length == 0)
            return Leaves.Random(leaf.Kind, ref random);

        var picked = declared.GetValue((int)(random.Next() % (ulong)declared.Length));

        try
        {
            return Convert.ChangeType(picked, leaf.EnumUnderlying, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return Leaves.Random(leaf.Kind, ref random);
        }
    }

    private static Recipe PrimitiveArray(ValueShape element, ref DeterministicRandom random)
    {
        // Lungimile mici sunt cele care conteaza: 0 si 1 sunt cazurile de margine pe care codul le trateaza
        // prost, iar peste vreo opt elemente nu se mai castiga nimic in schimbul memoriei.
        var length = (int)(random.Next() % 9);
        var bits = new long[length];

        for (var i = 0; i < length; i++)
            bits[i] = Leaves.Bits(element.Kind, LeafValue(element, ref random));

        return Recipe.OfArray(element.Kind, bits);
    }

    private static Recipe TextArray(ref DeterministicRandom random)
    {
        var length = (int)(random.Next() % 5);
        var texts = new string[length];

        for (var i = 0; i < length; i++)
            texts[i] = Leaves.RandomText(ref random);

        return Recipe.OfTextArray(texts);
    }

    /// <summary>
    /// Campurile pe care le seamana receptorul, si de ce asta si nu un constructor.
    ///
    /// Cererea era "receptori adevarati": azi se aloca un obiect il2cpp zeroizat, si aproape orice metoda de
    /// instanta se impiedica de primul camp citit. Drumul evident - sa chemam un constructor public pe
    /// fiecare parte - este tocmai cel care NU merge, si merita spus limpede: constructorul jocului este cod
    /// NATIV, al nostru este cod RECUPERAT, adica exact bucata pe care vrem sa o masuram. Chemate amandoua,
    /// ar aseza campuri diferite ori de cate ori recuperarea este gresita, iar de acolo orice diferenta
    /// masurata mai departe nu s-ar mai putea citi: nu s-ar sti daca metoda difera sau doar receptorul.
    ///
    /// Ce se face in loc: se aloca pe zero pe amandoua partile, ca acum, si apoi SCRIEM NOI aceleasi valori
    /// in aceleasi campuri. Scrisul este simetric prin constructie - aceiasi biti, prin reflectie, in campuri
    /// cu acelasi nume - deci receptorii raman echivalenti fara sa ruleze niciun cod al niciuneia dintre
    /// parti.
    ///
    /// Se seamana numai campurile pe care le are si partea jocului, cu acelasi fel de frunza. Un camp semanat
    /// doar de o parte ar face receptorii diferiti, iar un receptor diferit produce un "nu se comporta la
    /// fel" mincinos - mai rau decat o metoda nemasurata.
    /// </summary>
    private static string SeedReceiver(Type declaring, GameIndex.Entry entry, GameIndex game, ref DeterministicRandom random)
    {
        if (entry == null || string.IsNullOrEmpty(entry.ReceiverType))
            return "";

        FieldInfo[] fields;
        try
        {
            fields = ValueShape.InstanceFields(declaring);
        }
        catch (Exception)
        {
            return "";
        }

        var builder = new System.Text.StringBuilder();
        var written = 0;

        foreach (var field in fields)
        {
            // O limita pusa cu socoteala: un tip cu doua sute de campuri ar face un rand de lista de lucru
            // cat o pagina, iar castigul se aduna in primele cateva. Ordinea este ordinea de declarare, deci
            // se iau campurile dinspre inceputul tipului, care sunt si cele pe care codul le atinge cel mai
            // des.
            if (written >= 12)
                break;

            if (field.IsLiteral || field.IsInitOnly)
                continue;

            // Numele se cauta pe partea jocului STALCIT, fiindca asa il tine Il2CppInterop: un camp de
            // sprijin al unei proprietati are in codul recuperat paranteze unghiulare in nume, iar acolo
            // toate sunt inlocuite cu '_'. Potrivit nestalcit, nu s-ar semana niciodata - si tocmai
            // campurile de sprijin sunt cele in care isi tin proprietatile valoarea.
            var gameName = MethodKeys.MangleMember(field.Name);
            Recipe recipe;

            if (field.FieldType == typeof(string))
            {
                if (!game.HasTextField(entry.ReceiverType, gameName))
                    continue;

                recipe = Recipe.OfText(Leaves.RandomText(ref random));
            }
            else
            {
                var shape = ValueShape.For(field.FieldType);

                // Numai frunze simple, nu structuri intregi: un camp structura s-ar scrie prin reflectie si
                // pe partea jocului ar trebui sa treaca prin invelisul lui, unde forma poate sa difere - iar
                // atunci am face chiar noi receptorii diferiti.
                if (shape == null || !shape.IsLeaf)
                    continue;

                if (!game.HasField(entry.ReceiverType, gameName, shape.Kind))
                    continue;

                recipe = Recipe.OfBits(new[] { shape.Kind }, new[] { Leaves.Bits(shape.Kind, LeafValue(shape, ref random)) });
            }

            if (written > 0)
                builder.Append(Recipe.Separator);

            // Scris cu numele DE PE PARTEA NOASTRA. Harnasul il stalceste el cand cauta pe partea jocului,
            // ca amandoua partile sa porneasca de la acelasi nume si sa nu existe doua idei despre el.
            builder.Append(field.Name).Append('=').Append(recipe);
            written++;
        }

        return builder.ToString();
    }

    public static string ReturnPlanFor(Type type, bool isConstructor)
    {
        // Un constructor nu intoarce nimic si nu este o greseala ca nu intoarce: se masoara prin ce scrie in
        // receptor. Tocmai de aceea constructorii sunt printre cele mai bune tinte pe care le are unealta -
        // un corp de constructor NU face altceva decat sa scrie campuri, iar campurile se citesc inapoi.
        if (isConstructor || type == null || type == typeof(void))
            return "void";

        if (type.IsEnum || type.IsPrimitive)
            return "bits";

        if (type == typeof(string))
            return "text";

        if (type.IsArray && type.GetArrayRank() == 1)
        {
            var element = type.GetElementType();
            var elementShape = ValueShape.For(element);
            if (element == typeof(string) || (elementShape != null && elementShape.IsLeaf))
                return "array";

            return "nullness";
        }

        if (type.IsValueType)
            return ValueShape.For(type) != null ? "shape" : "nullness";

        return "nullness";
    }

    private static void Note(Type type)
    {
        var name = SafeTypeName(type);
        NullsByType.TryGetValue(name, out var count);
        NullsByType[name] = count + 1;
    }

    public static string SafeTypeName(Type type)
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
}
