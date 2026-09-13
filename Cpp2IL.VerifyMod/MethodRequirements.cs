using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Cpp2IL.VerifyCore;

namespace Cpp2IL.VerifyMod;

/// <summary>
/// Cum se fabrica fiecare valoare de care are nevoie o metoda. Numele sunt scrise in fisier ca atare,
/// deci se pot numara cu grep fara sa stie nimeni ce inseamna enumul de aici.
/// </summary>
internal static class ArgPlans
{
    // primitiva, enum sau structura numai-primitive: valoare din generator, aceiasi biti pe ambele parti
    public const string Generated = "generated";

    // structura cu referinte inauntru: instanta implicita, adica tot pe zero pe ambele parti
    public const string Zeroed = "zeroed";

    // clasa, interfata, tablou sau string: null pe ambele parti
    public const string Null = "null";

    // byref, pointer sau parametru generic: nu exista valoare pe care sa o dam
    public const string Cannot = "cannot";
}

/// <summary>
/// Etichetele de CALITATE a intrarilor. Scrise ca atare in coloana arg_quality, si numarate cu grep.
///
/// De ce sunt un vocabular si nu niste siruri imprastiate prin cod. Coloana asta este singurul lucru care
/// spune cat valoreaza un verdict, si masuratoarea o arata fara drept de apel: pe "generated" au iesit 54
/// AGREES si 29 DISAGREES - cunoastere; pe "null-reference" au iesit 91 THREW_BOTH si 67 "amandoua null" -
/// zgomot. Daca doua bucati de cod ar scrie eticheta putin altfel, cele doua gramezi s-ar amesteca si
/// deosebirea asta s-ar pierde.
///
/// Treptele, de la cea mai tare la cea mai slaba:
///
///   generated          valori din generator, aceiasi biti pe ambele parti (primitive, enum, structuri)
///   ref-string         un sir cu continut adevarat, acelasi pe ambele parti
///   ref-array          un tablou cu elemente adevarate
///   ref-object-string  un System.Object caruia i s-a dat un sir - drum separat, ca sa se poata scoate
///   ref-array-empty    un tablou GOL: slab, dar tot altceva decat null, fiindca Length si foreach merg
///   zeroed-struct      o structura cu referinte inauntru, adica instanta implicita pe ambele parti
///   null-reference     null pe ambele parti - simetric, dar steril
///
/// Si pentru receptor:
///
///   generated-receiver     receptor structura, fabricat din generator ca orice alta structura
///   seeded-receiver        obiect pe zero caruia i s-au SCRIS aceleasi campuri primitive pe ambele parti
///   zeroed-receiver        structura pe zero
///   uninitialised-receiver obiect pe zero, neatins - cea mai slaba treapta
/// </summary>
internal static class ArgQuality
{
    public const string Generated = "generated";
    public const string ZeroedStruct = "zeroed-struct";
    public const string Null = "null-reference";
    public const string None = "no-arguments";

    public const string Text = "ref-string";
    public const string Array = "ref-array";
    public const string EmptyArray = "ref-array-empty";
    public const string ObjectText = "ref-object-string";

    public const string UninitialisedReceiver = "uninitialised-receiver";
    public const string SeededReceiver = "seeded-receiver";
    public const string ZeroedReceiver = "zeroed-receiver";
    public const string GeneratedReceiver = "generated-receiver";
}

/// <summary>
/// Felul unui tip, in vocabularul cerut pentru dump: primitiv / enum / structura / clasa / tablou /
/// generic / byref / pointer.
/// </summary>
internal static class TypeKinds
{
    public static string Of(Type type)
    {
        if (type == null) return "void";
        if (type.IsByRef) return "byref";
        if (type.IsPointer) return "pointer";
        if (type.IsGenericParameter) return "generic-parameter";
        if (type.IsArray) return "array";
        if (type == typeof(void)) return "void";
        if (type == typeof(string)) return "string";
        if (type.IsEnum) return "enum";
        if (type.IsPrimitive) return "primitive";
        if (type.IsInterface) return "interface";
        if (type.IsValueType) return ValueShape.For(type) != null ? "struct-primitive" : "struct-mixed";
        if (type.ContainsGenericParameters) return "generic-open";
        return "class";
    }

    /// <summary>
    /// Ce se poate face cu o valoare de tipul asta cand trebuie fabricata. Separat de Of fiindca doua
    /// tipuri cu acelasi fel se fabrica altfel: o structura numai-primitive primeste valori din generator,
    /// una cu referinte inauntru primeste zero.
    /// </summary>
    public static string PlanFor(Type type)
    {
        if (type == null || type == typeof(void)) return ArgPlans.Cannot;
        if (type.IsByRef || type.IsPointer || type.IsGenericParameter) return ArgPlans.Cannot;
        if (type.ContainsGenericParameters) return ArgPlans.Cannot;
        if (type.IsEnum || type.IsPrimitive) return ArgPlans.Generated;
        if (!type.IsValueType) return ArgPlans.Null;
        return ValueShape.For(type) != null ? ArgPlans.Generated : ArgPlans.Zeroed;
    }

    /// <summary>
    /// Cum se poate compara o valoare intoarsa. Trepte de tarie, si trebuie citite ca atare: "bits" este
    /// o dovada, "nullness" abia o urma - doua implementari care intorc null fiindca amandoua au iesit pe
    /// prima ramura nu au demonstrat nimic despre restul corpului.
    /// </summary>
    public static string ReturnPlanFor(Type type)
    {
        if (type == null || type == typeof(void)) return "void";
        if (type.IsByRef || type.IsPointer) return "cannot";
        if (type.IsEnum || type.IsPrimitive) return "bits";
        if (type == typeof(string)) return "text";
        if (type.IsValueType) return ValueShape.For(type) != null ? "shape" : "cannot";
        return "nullness";
    }
}

/// <summary>
/// Un rand din dump: tot ce trebuie stiut despre o metoda ca sa se poata spune daca se poate chema si,
/// cand nu se poate, DE CE anume.
/// </summary>
internal sealed class MethodRequirement
{
    public string Key;
    public string Assembly;
    public string Type;
    public string Method;
    public string Token;
    public bool IsStatic;
    public string ReceiverPlan;
    public string ReceiverType;
    public string Parameters = "";
    public int ParameterCount;
    public string ReturnType;
    public string ReturnKind;
    public string ReturnPlan;
    public string GameSide;
    public string Blockers = "";
    public bool Callable;
    public string Quality;
}

/// <summary>
/// Dump-ul de cerinte: un rand pe metoda, scris INAINTE de orice apel.
///
/// De ce este cerut separat de maturare si de ce merita scris chiar daca maturarea nu apuca sa ruleze.
/// Pana acum fiecare unealta din proiect raporta "N metode nu se pot masura" fara sa spuna de ce, iar cand
/// motivele au fost in sfarsit numarate s-a vazut ca 18.255 dintre esecuri aveau o singura cauza - un apel
/// catre IntPtr::get_Zero emis peste tot si niciodata definit - si s-au reparat dintr-o data. Un numar
/// agregat ascunde exact felul asta de descoperire; o coloana cu motivul exact o face posibila.
///
/// Costa numai metadate: nu se executa niciun corp, deci fisierul exista si atunci cand maturarea moare la
/// a treia metoda. Din acelasi motiv se scrie primul.
/// </summary>
internal static class MethodRequirements
{
    private const char Sep = '\u001f';

    public const string FileName = "active-requirements.tsv";

    private static readonly string[] Header =
    {
        "key", "assembly", "type", "method", "token", "static", "receiver_plan", "receiver_type",
        "param_count", "parameters", "return_type", "return_kind", "return_plan", "game_side",
        "blockers", "callable", "arg_quality",
    };

    /// <summary>
    /// Descrie o metoda recuperata si, daca exista, perechea ei din joc. Nu arunca niciodata: un tip pe
    /// care reflectia refuza sa il descrie este el insusi un rezultat si trebuie sa ajunga in fisier ca
    /// atare, nu sa opreasca dump-ul cu treizeci de mii de randuri inainte de final.
    /// </summary>
    public static MethodRequirement Describe(string assembly, MethodBase recovered, MethodBase game, bool safetyOn, bool keyCollided = false)
    {
        var requirement = new MethodRequirement
        {
            Assembly = assembly,
            GameSide = game != null ? "present" : "absent",
            Type = "",
            Method = "",
            Token = "",
            ReceiverPlan = "",
            ReceiverType = "",
            ReturnType = "void",
            ReturnKind = "void",
            ReturnPlan = "void",
        };

        var blockers = new List<string>();

        try
        {
            requirement.Type = recovered.DeclaringType?.FullName ?? "";
            requirement.Method = recovered.Name;
            requirement.Token = "0x" + recovered.MetadataToken.ToString("x8", CultureInfo.InvariantCulture);
            requirement.IsStatic = recovered.IsStatic;
        }
        catch (Exception ex)
        {
            blockers.Add("reflectia nu descrie metoda: " + ex.GetType().Name);
        }

        try
        {
            requirement.Key = MethodKeys.For(recovered);
        }
        catch (Exception ex)
        {
            requirement.Key = requirement.Type + "::" + requirement.Method + "(?)";
            blockers.Add("cheia nu se poate calcula: " + ex.GetType().Name);
        }

        DescribeReceiver(requirement, recovered, blockers);
        DescribeParameters(requirement, recovered, blockers);
        DescribeReturn(requirement, recovered, blockers);

        if (recovered.IsGenericMethodDefinition || recovered.ContainsGenericParameters)
            blockers.Add("metoda este generica si nu are instantiere");

        if (recovered.IsAbstract)
            blockers.Add("metoda este abstracta - nu are corp de chemat");

        if (recovered.IsConstructor && recovered.IsStatic)
            blockers.Add("constructor static - nu se cheama direct");

        // Cele doua feluri de "nu am pereche" se numara separat fiindca se repara altfel. "Nu exista"
        // inseamna ca normalizarea inca nu ajunge la forma jocului si se poate castiga cu o regula noua.
        // "A iesit din pereche" inseamna ca forma a ajuns PREA departe: doua metode diferite ale jocului
        // au cazut pe aceeasi cheie si niciuna nu mai poate fi aleasa fara sa dam cu banul. Numarul
        // acesta este singurul care spune cat ne costa ciocnirile IN SCOPUL NOSTRU - restul ciocnirilor
        // sunt pe chei pe care codul recuperat nu le cere niciodata.
        if (game == null && keyCollided)
            blockers.Add("cheia a iesit din pereche - doua metode diferite ale jocului au cazut pe ea");
        else if (game == null)
            blockers.Add("cheia nu exista in indexul jocului - nu exista cu ce compara");

        if (safetyOn && !SubstSafety.MayTouch(assembly, requirement.Type))
            blockers.Add("exclusa de lista de siguranta pe tip (plati, cont, telemetrie, retea)");

        // Filtrul pe numele METODEI, nu al tipului. Faza 4 este prima care cheama, nu doar observa, si un
        // tip cu nume nevinovat poate avea o metoda care nu are ce cauta intr-un apel de proba.
        if (safetyOn && TargetUniverse.NeverCall(requirement.Method))
            blockers.Add("exclusa de lista de siguranta pe numele metodei");

        var universe = TargetUniverse.Classify(assembly);
        if (universe == AssemblyClass.Stubbed)
            blockers.Add("modul ciot - Cpp2IL ii inlocuieste fiecare corp");
        else if (universe == AssemblyClass.PublicLibrary)
            blockers.Add("biblioteca publica - originalul se poate lua de la autor");

        requirement.Blockers = string.Join("|", blockers);
        requirement.Callable = blockers.Count == 0;
        requirement.Quality = QualityOf(requirement);
        return requirement;
    }

    private static void DescribeReceiver(MethodRequirement requirement, MethodBase method, List<string> blockers)
    {
        if (method.IsStatic)
        {
            requirement.ReceiverPlan = "static";
            requirement.ReceiverType = "";
            return;
        }

        var declaring = method.DeclaringType;
        requirement.ReceiverType = Name(declaring);

        if (declaring == null)
        {
            requirement.ReceiverPlan = ArgPlans.Cannot;
            blockers.Add("metoda de instanta fara tip declarant");
            return;
        }

        if (declaring.IsInterface)
        {
            requirement.ReceiverPlan = ArgPlans.Cannot;
            blockers.Add("receptorul este o interfata - nu se poate aloca");
            return;
        }

        if (declaring.IsAbstract)
        {
            // Un tip abstract nu se poate aloca pe NICIUNA dintre parti, si asta conteaza: nu este o limita
            // a uneltei, este aceeasi limita si la il2cpp_object_new si la GetUninitializedObject. O clasa
            // derivata concreta ar merge, dar atunci metoda chemata ar putea fi suprascrisa si nu am mai
            // compara acelasi corp.
            requirement.ReceiverPlan = ArgPlans.Cannot;
            blockers.Add("tipul declarant este abstract - nu se poate aloca receptor");
            return;
        }

        if (declaring.ContainsGenericParameters)
        {
            requirement.ReceiverPlan = ArgPlans.Cannot;
            blockers.Add("tipul declarant este generic deschis");
            return;
        }

        if (declaring.IsByRefLike)
        {
            requirement.ReceiverPlan = ArgPlans.Cannot;
            blockers.Add("tipul declarant este ref struct - nu poate sta intr-un obiect");
            return;
        }

        requirement.ReceiverPlan = declaring.IsValueType ? ArgPlans.Zeroed : "uninitialised";
    }

    private static void DescribeParameters(MethodRequirement requirement, MethodBase method, List<string> blockers)
    {
        ParameterInfo[] parameters;
        try
        {
            parameters = method.GetParameters();
        }
        catch (Exception ex)
        {
            blockers.Add("parametrii nu se pot citi: " + ex.GetType().Name);
            return;
        }

        requirement.ParameterCount = parameters.Length;
        var builder = new StringBuilder();

        for (var i = 0; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType;
            var kind = TypeKinds.Of(type);
            var plan = TypeKinds.PlanFor(type);

            if (i > 0)
                builder.Append('|');

            builder.Append(parameters[i].Name ?? ("arg" + i)).Append(':')
                .Append(Name(type)).Append(':')
                .Append(kind).Append(':')
                .Append(plan);

            if (plan == ArgPlans.Cannot)
                blockers.Add("parametrul " + i + " (" + Name(type) + ") este " + kind + " - nu se poate fabrica");
        }

        requirement.Parameters = builder.ToString();
    }

    private static void DescribeReturn(MethodRequirement requirement, MethodBase method, List<string> blockers)
    {
        var type = (method as MethodInfo)?.ReturnType;

        // Un constructor nu intoarce nimic si nu este o greseala ca nu intoarce: se masoara prin ce scrie
        // in receptor, iar asta este in afara a ce poate compara unealta de fata. Intra in fisier cu
        // "void", nu cu un blocaj, ca sa se poata numara cati sunt.
        if (method.IsConstructor)
            type = null;

        requirement.ReturnType = Name(type);
        requirement.ReturnKind = TypeKinds.Of(type);
        requirement.ReturnPlan = TypeKinds.ReturnPlanFor(type);

        if (requirement.ReturnPlan == "cannot")
            blockers.Add("tipul intors (" + requirement.ReturnType + ") nu se poate compara");
    }

    /// <summary>
    /// Calitatea argumentelor, ca sir de etichete separate prin bara verticala - aceeasi forma ca
    /// "degeneracies" din recensamant, ca sa se poata numara la fel.
    ///
    /// ATENTIE la ce este si ce nu este eticheta din DUMP: este o previziune facuta numai din metadatele
    /// partii recuperate, inainte sa existe partea jocului. Cand maturarea chiar cheama metoda, ea
    /// recalculeaza calitatea din ce a reusit sa fabrice - un parametru "null" poate ajunge acolo
    /// "ref-string", iar un receptor "uninitialised-receiver" poate ajunge "seeded-receiver". In
    /// active-results.tsv se scrie eticheta ADEVARATA, cea de la apel; in active-requirements.tsv ramane
    /// previziunea. Cele doua coloane NU sunt acelasi lucru si nu se compara intre ele.
    ///
    /// Fara coloana asta raportul ar fi o minciuna prin omisiune. O metoda de instanta chemata cu receptor
    /// fabricat pe zero care raspunde acelasi lucru pe ambele parti NU se aduna cu una statica chemata cu
    /// valori din generator: prima poate sa fi iesit pe prima ramura, aceeasi pe ambele parti, fara sa
    /// atinga nimic din ce voiam masurat.
    /// </summary>
    private static string QualityOf(MethodRequirement requirement)
    {
        var labels = new List<string>();

        if (!requirement.IsStatic)
            labels.Add(requirement.ReceiverPlan == ArgPlans.Zeroed ? ArgQuality.ZeroedReceiver : ArgQuality.UninitialisedReceiver);

        if (requirement.Parameters.Length > 0)
        {
            if (requirement.Parameters.IndexOf(":" + ArgPlans.Null, StringComparison.Ordinal) >= 0)
                labels.Add(ArgQuality.Null);

            if (requirement.Parameters.IndexOf(":" + ArgPlans.Zeroed, StringComparison.Ordinal) >= 0)
                labels.Add(ArgQuality.ZeroedStruct);

            if (requirement.Parameters.IndexOf(":" + ArgPlans.Generated, StringComparison.Ordinal) >= 0)
                labels.Add(ArgQuality.Generated);
        }

        if (labels.Count == 0)
            labels.Add(ArgQuality.None);

        return string.Join("|", labels);
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
    /// Antetul si randul stau AICI, langa definitia antetului, si nu la locul unde se scrie fisierul.
    /// Maturarea citeste dump-ul inapoi pe indici de coloana, deci ordinea coloanelor este un contract
    /// intre doua bucati de cod: tinuta intr-un singur loc, o coloana mutata se muta peste tot deodata;
    /// tinuta in doua, s-ar desincroniza tacut si lista de lucru ar citi "callable" din coloana gresita.
    /// </summary>
    public static string HeaderLine() => string.Join(Sep.ToString(), Header);

    public static string RowLine(MethodRequirement r) => string.Join(Sep.ToString(),
        Clean(r.Key), Clean(r.Assembly), Clean(r.Type), Clean(r.Method), Clean(r.Token),
        r.IsStatic ? "1" : "0", Clean(r.ReceiverPlan), Clean(r.ReceiverType),
        r.ParameterCount.ToString(CultureInfo.InvariantCulture), Clean(r.Parameters),
        Clean(r.ReturnType), Clean(r.ReturnKind), Clean(r.ReturnPlan), Clean(r.GameSide),
        Clean(r.Blockers), r.Callable ? "1" : "0", Clean(r.Quality));

    private static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        return text.Replace(Sep, ' ').Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }
}
