using System;
using System.Collections.Generic;

namespace Cpp2IL.VerifyCore;

// Lista de siguranta si impartirea universului, MUTATE aici din Cpp2IL.VerifyMod fara nicio schimbare de
// continut: fragmentele, prefixele si numerele masurate din comentarii sunt cele de dinainte, litera cu
// litera.
//
// De ce s-au mutat. Acum exista doua unelte care trebuie sa fie de acord asupra a ce nu se atinge:
// planificatorul de pe disc, care hotaraste ce intra in lista de lucru, si harnasul din joc, care o
// executa. Doua copii ale acelorasi liste ar putea ajunge sa difere dupa o singura editare neatenta, iar
// urmarea ar fi o metoda de plati sau de cont lasata in lista de o parte si chemata de cealalta. O
// singura definitie, folosita de amandoua, face nepotrivirea imposibila.
//
// Ce NU s-a mutat: SubstSafety.MaySubstitute si lista NeverSubstitute de langa ea. Acelea spuneau ce
// raspuns nu se scrie inapoi in joc, iar acum nu se mai scrie NICIUN raspuns inapoi in joc - se cheama
// ambele implementari si se compara ce au intors. O lista care raspunde la o intrebare pe care nimeni nu
// o mai pune ar fi doar cod mort care arata a plasa de siguranta.
/// <summary>
/// De care parte cade un assembly: cod scris de EI, biblioteca publica luata de-a gata, sau modul pe
/// care Cpp2IL il inlocuieste cu cioturi.
///
/// Impartirea nu este cosmetica, este chiar definitia universului de masurat. Recuperarea are rost numai
/// pentru codul lor: o biblioteca publica se poate lua in forma originala de la autor, deci daca Cpp2IL
/// o reconstruieste gresit nu pierde nimeni nimic. Un modul ciot este mai rau decat inutil - corpurile
/// lui sunt literalmente "ldc.r4 0; ret", deci ar raspunde zero, ar aparea ca dezacord si ar umple
/// raportul cu mii de diferente care nu spun nimic despre recuperare.
///
/// Numerele de mai jos sunt MASURATE pe census.jsonl (81 de assembly-uri, 71.826 de metode cu corp), nu
/// estimate: cod propriu 58.972 de metode in 54 de assembly-uri, biblioteci publice 11.519, transport
/// de retea public 1.318, plati 17.
/// </summary>
public enum AssemblyClass
{
    /// <summary>Codul lor sau al furnizorilor lor. Se masoara.</summary>
    InHouse = 0,

    /// <summary>Biblioteca publica: originalul se poate lua de la autor, deci recuperarea ei nu conteaza.</summary>
    PublicLibrary = 1,

    /// <summary>Modul pe care Cpp2IL nu il analizeaza deloc - fiecare corp este un ciot.</summary>
    Stubbed = 2,
}

public static class TargetUniverse
{
    /// <summary>
    /// Cioturile. Aceeasi lista ca Selector.IsStubbedModule, ca AsmResolverDllOutputFormatIlRecovery si
    /// ca SubstSafety.IsStubbedModule - repetata aici DINADINS si nu refolosita prin delegare, fiindca
    /// este lista pe care se sprijina si decizia de siguranta: daca cele doua idei despre "ce este un
    /// ciot" ar ajunge sa difere, un modul ar putea fi masurat de o parte si ocolit de cealalta.
    /// </summary>
    private static readonly string[] StubbedPrefixes =
    {
        "UnityEngine.", "Unity.", "System.", "mscorlib",
    };

    /// <summary>
    /// Bibliotecile publice, pe prefix de nume de assembly.
    ///
    /// Lista vine de la utilizator si este completata cu ce s-a gasit efectiv in recensamant, nu cu ce ar
    /// putea exista. Fiecare rand este o biblioteca al carei cod sursa sau binar original se poate obtine
    /// de la autorul ei, deci masurarea recuperarii ei ar consuma o sesiune fara sa raspunda la intrebarea
    /// care ne intereseaza.
    /// </summary>
    private static readonly string[] PublicPrefixes =
    {
        "Newtonsoft.Json", "Cinemachine", "Firebase.", "Facebook.", "GooglePlayGames", "AppleAuth",
        "Antlr4.", "LiteNetLib", "SuperSocket", "WebSocket4Net", "PusherClient",
        "com.rlabrecque.steamworks.net", "BugsnagUnity", "DOTween", "Coffee.", "ASPlugins.Dreamteck.",
        "EnhancedScroller", "KTK_", "Mono.Security", "I18N", "Microsoft.",

        // Transport si sesiune Photon. Publice, si pe deasupra exact ce SubstSafety.NeverTouch tine la
        // distanta: sunt canalul catre serverele jocului. PhotonDeterministic NU este aici - acela este
        // simularea, nu transportul, si intra in univers.
        "Photon3Unity3D", "PhotonRealtime", "Photon.PhotonLibs",

        // Plati. Publica prin Unity, dar ar fi exclusa oricum: nu se cheama nimic din lantul de
        // cumparare, oricat de inofensiv ar arata numele metodei.
        "Purchasing.Common",
    };

    public static AssemblyClass Classify(string assembly)
    {
        if (assembly == null)
            return AssemblyClass.Stubbed;

        if (assembly == "System")
            return AssemblyClass.Stubbed;

        foreach (var prefix in StubbedPrefixes)
            if (assembly.StartsWith(prefix, StringComparison.Ordinal))
                return AssemblyClass.Stubbed;

        foreach (var prefix in PublicPrefixes)
            if (assembly.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return AssemblyClass.PublicLibrary;

        // Implicit INAUNTRU, si asta este o alegere explicita a utilizatorului: cand nu este limpede de
        // care parte cade un assembly, se masoara. Costul unei includeri gresite este o sesiune care
        // masoara si o biblioteca publica; costul unei excluderi gresite este cod de-al lor care nu se
        // verifica niciodata si despre care nimeni nu afla ca lipseste.
        //
        // Cele care au intrat pe drumul acesta, si care trebuie privite cu ochiul liber macar o data:
        // Rewired_Core (12.568 de metode) si Rewired_Windows (1.601) - biblioteca comerciala de input,
        // adica aproape un sfert din univers; AraTrail (87), UnityFx.Outline (162), NaughtyAttributes.Core
        // (75), StompyRobot.SRF (511), MessagePack (753) si EnhancedScroller-ul lor. Se pot scoate oricand
        // fara recompilare, prin CPP2IL_ACTIVE_SKIP.
        return AssemblyClass.InHouse;
    }

    /// <summary>
    /// Fragmente de NUME DE METODA care nu se cheama, oricat de inofensiv ar fi tipul din care fac parte.
    ///
    /// Exista pentru ca faza 4 este prima care CHEAMA metodele, nu doar le observa, iar filtrul de siguranta
    /// mostenit de la faza 3 se uita numai la numele TIPULUI. Gasit cu degetul pe recensamant, nu presupus:
    /// "MainMenu::AttemptQuickLogin" sta intr-un tip numit MainMenu, deci trece de fiecare fragment de tip
    /// din lista - "Login" prinde LoginMenuHandler, dar nu prinde o metoda de login dintr-un tip cu nume
    /// nevinovat. Fara randurile de mai jos, exact aceea ar fi fost chemata.
    ///
    /// Lista este scurta dinadins si tine numai ce nu se poate lua inapoi: bani, cont, date sterse, trafic
    /// trimis in afara. Verbe foarte des intalnite - Reset, Clear, Save, Send - au fost lasate AFARA anume,
    /// si nu din neglijenta: ar taia mii de metode de desen si de pooling, iar pe un receptor fabricat pe
    /// zero, care este un obiect de unica folosinta ce nu apartine jocului, un Reset nu atinge nimic viu.
    /// Primejdia adevarata sunt metodele STATICE, care lucreaza pe stare globala - si tocmai acelea se
    /// maturaza primele.
    ///
    /// Fragmentele au fost si taiate dupa ce au fost numarate, nu doar adaugate dupa ureche. "Register" a
    /// iesit fiindca prindea RegisterCallback, RegisterListener si RegisterHandler - 116 metode de plumbarie
    /// inofensiva pentru care lista de tipuri acopera oricum partea de cont; "Subscribe" a iesit din acelasi
    /// motiv, 130 de metode de evenimente, iar abonamentele platite sunt prinse de fragmentul de TIP
    /// "Subscription". Costul masurat al listei, dupa taiere: 917 de metode din 58.972, adica 1,55%.
    /// </summary>
    private static readonly string[] NeverCallMethodFragments =
    {
        // bani
        "Purchase", "Buy", "Checkout", "Refund", "Redeem", "Consume", "RestoreTransaction",
        // cont si identitate
        "Login", "LogIn", "SignIn", "SignUp", "Logout", "LogOut", "SignOut",
        "Authenticate", "Authorize", "Credential", "Password", "RefreshToken",
        // date care nu se mai intorc
        "Delete", "Erase", "Wipe", "Purge", "Unlink",
        // trafic catre afara
        "Upload", "SendEvent", "LogEvent", "TrackEvent", "ReportEvent", "Flush",
        // sesiuni de joc in retea
        "Matchmak", "JoinRoom", "LeaveRoom", "JoinLobby", "Connect",
        // sanctiuni. "Banned", nu "Ban": potrivirea este pe subsir, iar "Ban" prinde Banner, Band si
        // Bandwidth - adica exact codul de interfata pe care vrem sa-l masuram. Masurat pe recensamant,
        // "Ban" taia 154 de metode, "Banned" taie 9.
        "Banned", "Kick",
    };

    /// <summary>
    /// Adevarat daca metoda nu are voie sa fie chemata din cauza numelui ei. Potrivirea este pe subsir si
    /// fara diferenta de litere mari si mici, la fel ca la numele de tip.
    /// </summary>
    public static bool NeverCall(string method)
    {
        if (method == null)
            return false;

        foreach (var fragment in NeverCallMethodFragments)
            if (method.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

        return false;
    }

    public static bool InUniverse(string assembly) => Classify(assembly) == AssemblyClass.InHouse;

    /// <summary>
    /// Numele assembly-ului asa cum il stie partea recuperata, pornind de la un nume de fisier care poate
    /// fi si cel al jocului. Il2CppInterop prefixeaza fiecare assembly cu "Il2Cpp", deci
    /// "Il2Cppquantum.code" si "quantum.code" sunt acelasi lucru si trebuie sa cada in aceeasi clasa.
    /// </summary>
    public static string Strip(string assembly)
    {
        if (assembly != null && assembly.StartsWith("Il2Cpp", StringComparison.Ordinal))
            return assembly.Substring("Il2Cpp".Length);

        return assembly;
    }

    /// <summary>
    /// Potrivire pe fragmente separate prin virgula, folosita si de includere si de excludere. Un filtru
    /// gol inseamna "tot", nu "nimic": altfel o variabila de mediu uitata nemontata ar goli tacut
    /// universul si sesiunea ar raporta zero metode ca si cum nu ar fi fost nimic de masurat.
    /// </summary>
    public static bool Matches(string assembly, string fragments)
    {
        if (string.IsNullOrEmpty(fragments))
            return true;

        foreach (var fragment in fragments.Split(','))
        {
            var trimmed = fragment.Trim();
            if (trimmed.Length > 0 && assembly != null
                && assembly.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    public static HashSet<string> Split(string fragments)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(fragments))
            return set;

        foreach (var fragment in fragments.Split(','))
        {
            var trimmed = fragment.Trim();
            if (trimmed.Length > 0)
                set.Add(trimmed);
        }

        return set;
    }
}

/// <summary>
/// Ce nu se atinge, si de ce.
///
/// Harnasul CHEAMA metodele jocului inauntrul unui joc conectat la serverele lui. Un apel intr-un loc
/// obisnuit inseamna un pixel prost desenat; acelasi apel in codul de cont, de plata, de antifrauda sau
/// de telemetrie inseamna date stricate, o cerere trimisa in afara sau un cont sanctionat, si asta nu se
/// poate lua inapoi cu o repornire. Numele listei a ramas cel de dinainte anume, ca notele si rapoartele
/// vechi sa se poata cauta mai departe dupa el. Lista nu este ghicita: numele vin din chiar universul
/// recensamantului (81 de assembly-uri, 71.826 de metode) si din spatiile de nume ale Assembly-CSharp.
/// </summary>
public static class SubstSafety
{
    // Nici macar citite. Harnasul citeste campuri de pe receptorul pe care il fabrica el, dar tot el
    // cheama si metode ale jocului, iar un getter nativ de retea sau de plata poate face mult mai mult
    // decat sa intoarca un camp.
    private static readonly string[] NeverTouch =
    {
        // transport si sesiune de retea
        "Photon3Unity3D", "PhotonRealtime", "LiteNetLib", "SuperSocket.ClientEngine", "PusherClient",
        "WebSocketDotNet", "MessagePack", "Mono.Security",
        // cont, magazin, atribuire, telemetrie, antifrauda
        "BackboneUnity", "Firebase.", "Facebook.Unity", "GooglePlayGames",
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

    // Aici statea lista NeverSubstitute - quantum.code, quantum.core, PhotonDeterministic, Scopely.,
    // Playgami., Tag.SwapShop - adica assembly-urile care se puteau observa, dar al caror raspuns nu se
    // scria niciodata inapoi in joc. A fost scoasa odata cu substitutia, fiindca acum nu se mai scrie
    // NICIUN raspuns inapoi in joc: se cheama amandoua implementarile si se compara ce au intors. Lasata
    // pe loc, ar fi aratat a plasa de siguranta fara sa o citeasca nimeni, si asta este mai rau decat
    // lipsa ei - te bizui pe ceva ce nu exista.
    //
    // Metodele acelor assembly-uri raman CHEMATE, ca si pana acum, iar ce le tine in frau este MayTouch de
    // mai jos, impreuna cu TargetUniverse.NeverCall.

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

}
