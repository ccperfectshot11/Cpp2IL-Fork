using System;
using System.Collections.Generic;

namespace Cpp2IL.VerifyMod;

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
internal enum AssemblyClass
{
    /// <summary>Codul lor sau al furnizorilor lor. Se masoara.</summary>
    InHouse = 0,

    /// <summary>Biblioteca publica: originalul se poate lua de la autor, deci recuperarea ei nu conteaza.</summary>
    PublicLibrary = 1,

    /// <summary>Modul pe care Cpp2IL nu il analizeaza deloc - fiecare corp este un ciot.</summary>
    Stubbed = 2,
}

internal static class TargetUniverse
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
