using System;
using System.Collections.Generic;
using System.Reflection;
using Cpp2IL.VerifyCore;

namespace Cpp2IL.VerifyMod;

/// <summary>
/// Cat de multa traducere de tipuri cere o metoda ca sa poata fi rulata pe starea reala a jocului.
/// Impartirea nu este estetica: fiecare treapta in sus adauga un mecanism care poate minti in tacere,
/// asa ca rezultatele TREBUIE citite separat pe trepte.
/// </summary>
internal enum SubstTier
{
    /// <summary>
    /// Nicio traducere. Metoda este statica, toti parametrii si tipul intors sunt numai-primitive, deci
    /// argumentele culese la punctul de apel al jocului se pot da CA ATARE metodei recuperate. Aici nu
    /// exista niciun strat care sa greseasca intre cele doua implementari.
    /// </summary>
    None = 0,

    /// <summary>
    /// Doar receptorul. Metoda este de instanta pe o CLASA, iar parametrii si tipul intors sunt
    /// numai-primitive. Jocul ne da obiectul viu, iar starea lui trece in obiectul recuperat prin
    /// ReceiverTransfer - un pas care poate fi incomplet, si de aceea fiecare rezultat isi poarta
    /// socoteala campurilor copiate.
    /// </summary>
    Receiver = 1,
}

internal sealed class SubstEntry
{
    public string Key;
    public SubstTier Tier;
    public string Assembly;
    public string Type;
    public string Method;

    public string ToLine() => ((int)Tier) + "\t" + Assembly + "\t" + Key;

    public static SubstEntry FromLine(string line)
    {
        if (line == null)
            return null;

        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#')
            return null;

        var parts = trimmed.Split('\t');
        if (parts.Length < 3 || !int.TryParse(parts[0], out var tier))
            return null;

        return new SubstEntry { Tier = (SubstTier)tier, Assembly = parts[1], Key = parts[2] };
    }
}

/// <summary>
/// Ce nu se atinge, si de ce.
///
/// Substitutia ruleaza cod NEVERIFICAT inauntrul unui joc conectat la serverele lui. Un raspuns gresit
/// intr-un loc obisnuit inseamna un pixel prost desenat; acelasi raspuns gresit in codul de cont, de
/// plata, de antifrauda sau de simulare determinista inseamna date stricate sau un cont sanctionat, si
/// asta nu se poate lua inapoi cu o repornire. Lista nu este ghicita: numele vin din chiar universul
/// recensamantului (81 de assembly-uri, 71.826 de metode) si din spatiile de nume ale Assembly-CSharp.
/// </summary>
internal static class SubstSafety
{
    // Nici macar observate. Transferul de stare CITESTE proprietati de pe un obiect viu, iar un getter
    // nativ de retea sau de plata poate face mai mult decat sa intoarca un camp.
    private static readonly string[] NeverTouch =
    {
        // transport si sesiune de retea
        "Photon3Unity3D", "PhotonRealtime", "LiteNetLib", "SuperSocket.ClientEngine", "PusherClient",
        "WebSocketDotNet", "MessagePack", "Mono.Security",
        // cont, magazin, atribuire, telemetrie, antifrauda
        "Scopely.", "Playgami.", "BackboneUnity", "Firebase.", "Facebook.Unity", "GooglePlayGames",
        "AppleAuth", "com.rlabrecque.steamworks.net", "BugsnagUnity",
    };

    private static readonly string[] NeverTouchNamespaces =
    {
        "Stumble.Login", "Stumble.LocalUsers", "Stumble.Currencies", "Stumble.Matchmaking",
        "Stumble.PhotonRegions", "Stumble.DeepLinking", "Shop.", "ScopelyAccount.", "Analytics.",
        "Datadog.", "BattlePass.", "Rewards.", "Stumble.InAppMessaging", "Stumble.LootBox",
        // Gasita numarand candidatii, nu ghicita: Pusher.PusherManager sta in Assembly-CSharp, deci
        // filtrul pe assembly pentru "PusherClient" nu o prindea. Este canalul de mesaje in timp real.
        "Pusher.",
    };

    /// <summary>
    /// Fragmente de NUME de tip, nu de namespace.
    ///
    /// Filtrul pe namespace nu ajunge: masurat pe build-ul recuperat, 1.597 din tipurile lui
    /// Assembly-CSharp stau in namespace-ul global - AdManager, AppleLoginManager, BackboneIntegration,
    /// BannedPopupHelper, ConsentFlow, FriendsListNetworkController - si niciun prefix de namespace nu le
    /// atinge. Lista este DELIBERAT prea larga - "Store" prinde si ce doar seamana a magazin - si costa,
    /// masurat, 499 candidati din 7.148. Cand alegerea este intre a masura cu cinci sute de metode mai
    /// putin si a atinge codul de cont, de plata sau de sanctionare al unui joc conectat la serverele lui,
    /// pretul asta se plateste fara discutie.
    /// </summary>
    private static readonly string[] NeverTouchTypeFragments =
    {
        "Purchase", "IAP", "Store", "Shop", "Wallet", "Payment", "Receipt", "Subscription", "Currency",
        "Login", "Account", "Auth", "Consent", "Privacy", "Banned",
        "Backbone", "Analytics", "Telemetry", "Firebase", "Pusher", "Http", "Network", "Socket",
        "Session", "Token", "Server", "Friends", "Social", "Leaderboard", "Tournament",
        // Reclamele au nevoie de fragmente intregi, nu de "Ad": potrivirea se face pe subsir, iar "Ad"
        // ar prinde Shadow, Loader, Header si Gradient - adica exact codul de desen pe care vrem sa-l
        // masuram. Masurat pe out_w13on: cu fragmentele de mai jos filtrul taie 499 candidati, cu "Ad"
        // simplu taia 780 - 281 de metode pierdute degeaba.
        "Advert", "AdManager", "AdView", "AdUnit", "AdService", "Rewarded", "Interstitial",
    };

    // Se pot OBSERVA, dar raspunsul lor nu se inlocuieste niciodata. Simularea determinista a lui Quantum
    // este rulata identic pe toate masinile din meci; un rezultat diferit acolo nu este un bug local, ci
    // o desincronizare pe care serverul o vede.
    private static readonly string[] NeverSubstitute =
    {
        "quantum.code", "quantum.core", "PhotonDeterministic",
    };

    /// <summary>
    /// Modulele pe care Cpp2IL nu le analizeaza deloc: AsmResolverDllOutputFormatIlRecovery le inlocuieste
    /// FIECARE corp cu un ciot, deci "Mathf.Clamp" recuperat este literalmente "ldc.r4 0; ret".
    ///
    /// Nu sunt doar inutile ca tinte, sunt otravitoare: ar intra in plan cu semnaturi numai-primitive,
    /// ar fi carligate, ar raspunde zero si ar aparea ca DISAGREES - mii de dezacorduri care nu spun nimic
    /// despre recuperare. Masurat pe out_w13on: din 17.641 de candidati pe toate cele 150 de DLL-uri,
    /// 8.333 sunt in module ciot, adica aproape jumatate din lista ar fi fost zgomot.
    /// </summary>
    public static bool IsStubbedModule(string assembly) =>
        assembly != null
        && (assembly.StartsWith("UnityEngine.", StringComparison.Ordinal)
            || assembly.StartsWith("Unity.", StringComparison.Ordinal)
            || assembly.StartsWith("System.", StringComparison.Ordinal)
            || assembly == "System"
            || assembly.StartsWith("mscorlib", StringComparison.Ordinal));

    public static bool MayTouch(string assembly, string type)
    {
        if (IsStubbedModule(assembly))
            return false;

        foreach (var denied in NeverTouch)
            if (assembly != null && assembly.StartsWith(denied, StringComparison.OrdinalIgnoreCase))
                return false;

        foreach (var denied in NeverTouchNamespaces)
            if (type != null && type.StartsWith(denied, StringComparison.Ordinal))
                return false;

        // Potrivirea se face pe numele SCURT al tipului. Pe calea intreaga, un singur segment de namespace
        // nepotrivit ar arunca tot ce sta sub el - iar namespace-urile intregi care chiar trebuie aruncate
        // sunt deja in lista de mai sus, pe prefix.
        var shortName = type;
        if (shortName != null)
        {
            var dot = shortName.LastIndexOf('.');
            if (dot >= 0)
                shortName = shortName.Substring(dot + 1);

            foreach (var fragment in NeverTouchTypeFragments)
                if (shortName.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
        }

        return true;
    }

    public static bool MaySubstitute(string assembly, string type)
    {
        if (!MayTouch(assembly, type))
            return false;

        foreach (var denied in NeverSubstitute)
            if (assembly != null && assembly.StartsWith(denied, StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }
}

internal static class SubstPlanner
{
    /// <summary>
    /// Ce treapta i se potriveste unei metode recuperate, sau null daca niciuna.
    ///
    /// Predicatul pentru "numai-primitive" este chiar ValueShape.For, acelasi pe care il foloseste
    /// fuzzing-ul. Nu o copie: doua idei despre ce inseamna o valoare primitiva ar duce la metode alese
    /// aici si refuzate la rulare, exact capcana de care se fereste si Selector.IsSafeValue.
    /// </summary>
    public static SubstTier? Classify(MethodBase method)
    {
        if (method.IsGenericMethodDefinition || method.ContainsGenericParameters || method.IsAbstract)
            return null;

        // Un constructor ar cere sa cladim noi obiectul, adica fix ce nu putem face. Un .cctor nu este
        // nici macar o functie a argumentelor lui.
        if (method.IsConstructor)
            return null;

        if (method is not MethodInfo info)
            return null;

        // Void iese: fara valoare intoarsa nu exista nimic de comparat cu originalul, iar "nu a crapat"
        // este exact dovada slaba pe care unealta asta trebuie sa o inlocuiasca. Mutatia receptorului NU
        // tine loc de raspuns aici, fiindca receptorul nostru este o COPIE - jocul nu vede scrisul nostru
        // si noi nu vedem scrisul lui.
        if (info.ReturnType == typeof(void) || ValueShape.For(info.ReturnType) == null)
            return null;

        foreach (var parameter in info.GetParameters())
        {
            if (parameter.ParameterType.IsByRef || ValueShape.For(parameter.ParameterType) == null)
                return null;
        }

        if (info.IsStatic)
            return SubstTier.None;

        var declaring = info.DeclaringType;
        if (declaring == null || declaring.IsValueType)
            return null;

        return SubstTier.Receiver;
    }

    /// <summary>
    /// Intersectia dintre ce avem recuperat si ce exista in joc, filtrata de siguranta. Intersectia, nu
    /// doar lista recuperata: o metoda pe care indexul jocului nu o contine nu poate fi nici carligata,
    /// nici comparata, si numarata in plan ar umfla cifra fara sa aduca nicio masuratoare.
    /// </summary>
    public static List<SubstEntry> Plan(string assemblyName, Dictionary<string, MethodBase> recovered, Dictionary<string, MethodBase> game, HashSet<SubstTier> tiers, Dictionary<string, int> drops)
    {
        var entries = new List<SubstEntry>();

        foreach (var pair in recovered)
        {
            var tier = Classify(pair.Value);
            if (tier == null)
            {
                Bump(drops, "semnatura nu se potriveste niciunei trepte");
                continue;
            }

            if (!tiers.Contains(tier.Value))
            {
                Bump(drops, "treapta oprita din configurare");
                continue;
            }

            var typeName = pair.Value.DeclaringType?.FullName ?? "";
            if (SubstSafety.IsStubbedModule(assemblyName))
            {
                Bump(drops, "modul ciot - Cpp2IL nu ii recupereaza corpurile");
                continue;
            }

            if (!SubstSafety.MayTouch(assemblyName, typeName))
            {
                Bump(drops, "exclusa din motive de siguranta");
                continue;
            }

            if (!game.ContainsKey(pair.Key))
            {
                Bump(drops, "cheia nu exista in indexul jocului");
                continue;
            }

            entries.Add(new SubstEntry
            {
                Key = pair.Key,
                Tier = tier.Value,
                Assembly = assemblyName,
                Type = typeName,
                Method = pair.Value.Name,
            });
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return entries;
    }

    private static void Bump(Dictionary<string, int> counts, string reason)
    {
        counts.TryGetValue(reason, out var value);
        counts[reason] = value + 1;
    }
}
