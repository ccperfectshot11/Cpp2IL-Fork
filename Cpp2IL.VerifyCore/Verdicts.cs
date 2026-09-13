namespace Cpp2IL.VerifyCore;

/// <summary>
/// Ce poate spune o masuratoare, si - la fel de important - ce NU poate.
///
/// Vocabularul de dinainte avea o gaura prin care se scurgea zgomotul in coloana de succes: "amandoua au
/// intors null" si "void, nimic de comparat" erau amandoua un fel de acord. Masurat pe cele 2.574 de
/// rezultate reale, asta inseamna 75 de randuri "amandoua null" si 92 de randuri "nimic de comparat"
/// langa 79 de acorduri adevarate - adica, daca s-ar aduna, doua treimi din "succes" ar fi metode despre
/// care nu s-a aflat nimic.
///
/// Regula care face gaura imposibila: un verdict de ACORD se scrie numai cand a existat ceva de privit si
/// ce s-a privit s-a potrivit. "Nu a existat nimic de privit" are verdict propriu si nu se aduna nicaieri.
/// Coloana de alaturi, <see cref="Observed"/>, spune CE anume s-a privit, deci orice cifra din raport se
/// poate desface inapoi in observatii.
/// </summary>
public static class Verdict
{
    /// <summary>
    /// Cele doua implementari au fost privite si s-au potrivit. Se scrie NUMAI daca <see cref="Observed"/>
    /// nu este <see cref="Observed.Nothing"/>.
    /// </summary>
    public const string Same = "SAME";

    /// <summary>Ceva ce s-a putut privi difera. La fel de pretios ca un acord, si mai util.</summary>
    public const string Different = "DIFFERENT";

    /// <summary>Amandoua au aruncat acelasi fel de exceptie. Un acord slab, dar real: aceeasi intrare, acelasi refuz.</summary>
    public const string BothThrewSame = "BOTH_THREW_SAME";

    /// <summary>Amandoua au aruncat, dar altceva. Un dezacord la fel de adevarat ca doua numere diferite.</summary>
    public const string BothThrewDifferent = "BOTH_THREW_DIFFERENT";

    /// <summary>Numai a noastra a aruncat. Aproape intotdeauna o eroare de recuperare.</summary>
    public const string OnlyOursThrew = "ONLY_OURS_THREW";

    /// <summary>Numai a jocului a aruncat. Mai des un argument prost decat o recuperare buna - de citit impreuna cu calitatea.</summary>
    public const string OnlyGameThrew = "ONLY_GAME_THREW";

    /// <summary>
    /// Amandoua au mers, niciuna nu a aruncat, si nu a ramas NIMIC de privit: void, fara campuri de citit
    /// inapoi din receptor, fara argumente de iesire. NU este acord. Este o metoda chemata degeaba, iar
    /// numarul lor spune cat de mult mai are de castigat partea de observatii.
    /// </summary>
    public const string NothingObserved = "NOTHING_OBSERVED";

    /// <summary>
    /// Amandoua au intors null si nimic altceva nu s-a schimbat. NU este acord: doua implementari care ies
    /// pe prima ramura nu au demonstrat nimic despre restul corpului.
    /// </summary>
    public const string BothNull = "BOTH_NULL";

    /// <summary>Corpul recuperat nu a trecut de JIT. Ar fi trebuit prins pe disc; aici inseamna ca planificatorul si jocul nu au fost de acord.</summary>
    public const string RecoveredRefused = "RECOVERED_REFUSED";

    /// <summary>Argumentul sau valoarea intoarsa are alta FORMA pe cele doua parti - alt numar de frunze. Nu este o diferenta de comportare, ci de layout, si se numara separat.</summary>
    public const string LayoutDiffers = "LAYOUT_DIFFERS";

    /// <summary>A luat procesul cu ea. Scris din jurnal la pornirea urmatoare, fiindca nimeni nu il mai poate scrie atunci.</summary>
    public const string Killed = "KILLED";

    /// <summary>Nu s-a putut ajunge la apel: perechea a disparut, receptorul nu s-a putut fabrica, un argument nu s-a putut materializa. Motivul sta in ultima coloana.</summary>
    public const string NotReached = "NOT_REACHED";
}

/// <summary>
/// CE anume s-a privit. Fara coloana asta, un SAME nu se poate citi: unul obtinut din biti si unul obtinut
/// din nulitatea unei referinte sunt lucruri foarte diferite, iar raportul le-ar aduna.
/// </summary>
public static class Observed
{
    /// <summary>Valoarea intoarsa, bit cu bit. Cea mai tare dovada pe care o are unealta.</summary>
    public const string ReturnBits = "return-bits";

    /// <summary>Sirul intors, caracter cu caracter. La fel de tare ca bitii.</summary>
    public const string ReturnText = "return-text";

    /// <summary>Structura intoarsa, frunza cu frunza.</summary>
    public const string ReturnShape = "return-shape";

    /// <summary>Un tablou intors, element cu element.</summary>
    public const string ReturnArray = "return-array";

    /// <summary>Numai daca referinta intoarsa este nula sau nu. Slab, si scris asa ca sa se vada ca este slab.</summary>
    public const string ReturnNullness = "return-nullness";

    /// <summary>Campurile receptorului dupa apel. Asta transforma o metoda void intr-una masurabila.</summary>
    public const string ReceiverFields = "receiver-fields";

    /// <summary>Si valoarea intoarsa si campurile receptorului.</summary>
    public const string ReturnAndReceiver = "return-and-receiver";

    /// <summary>Continutul tablourilor date ca argument, dupa apel: o metoda care scrie intr-un tablou este masurabila prin el.</summary>
    public const string ArgumentArrays = "argument-arrays";

    /// <summary>Felul exceptiei.</summary>
    public const string ExceptionKind = "exception-kind";

    /// <summary>Nimic. Singurul loc unde are voie sa apara este langa NOTHING_OBSERVED.</summary>
    public const string Nothing = "none";
}

/// <summary>
/// De ce o metoda nu ajunge niciodata in lista de lucru. Toate se hotarasc PE DISC, fara sa porneasca
/// jocul, si fiecare este un rand in plan-blocked.tsv - un rezultat despre metoda, nu o metoda pierduta.
/// </summary>
public static class Blocked
{
    public const string NoGamePair = "no-game-pair";
    public const string KeyCollision = "key-collision";
    public const string Safety = "safety-list";
    public const string StubbedModule = "stubbed-module";
    public const string PublicLibrary = "public-library";
    public const string Abstract = "abstract-receiver";
    public const string OpenGeneric = "open-generic";
    public const string ByRefOrPointer = "byref-or-pointer";
    public const string NoBody = "no-body";

    /// <summary>
    /// Corpul recuperat nu trece de JIT fiindca cere din mscorlib membri pe care runtime-ul gazda nu ii are.
    ///
    /// Este categoria cea mai mare si cea mai prost inteleasa dintre toate. Masurat pe cele 1.931 de randuri
    /// IL_INVALID din rularile de pana acum: 338 cer System.ThrowHelper, 302 campul
    /// System.RuntimeTypeHandle.value, 161 System.SpanHelpers, 48 System.ReadOnlySpan`1 din mscorlib, 13
    /// System.Number, 11 CultureInfo.invariant_culture_info, 7 Delegate.m_target. Toate acestea sunt
    /// amanunte ale BCL-ului Mono cu care a fost compilat jocul; codul recuperat le cere pe drept, dar
    /// harnasul ruleaza pe .NET 6, unde ele nu exista sau au alta forma. Nu este o greseala de recuperare
    /// si nu se repara cu alte argumente - metoda pur si simplu nu poate fi chemata in procesul acesta.
    /// </summary>
    public const string HostBclMismatch = "host-bcl-mismatch";

    /// <summary>
    /// Corpul recuperat este el insusi invalid: JIT-ul il refuza fara sa ii lipseasca nimic din afara.
    ///
    /// ASTA este categoria care spune ceva despre Cpp2IL, si tocmai de aceea nu se amesteca cu cea de mai
    /// sus. Masurat pe aceleasi 1.931 de randuri: 512 "invalid program", 169 "invalid IL code or an
    /// internal limitation", 46 "Bad IL format" - 727 cu totul, adica nici jumatate din cate arata numarul
    /// agregat de dinainte.
    /// </summary>
    public const string InvalidIl = "invalid-il";

    /// <summary>Corpul recuperat a omorat procesul la compilare. Aflat pe disc, unde o moarte costa o secunda, nu o repornire de joc.</summary>
    public const string KilledOnPrepare = "killed-on-prepare";

    /// <summary>
    /// Runtime-ul refuza corpul dintr-un motiv care nu este nici IL stricat, nici membru lipsa.
    ///
    /// Masurat pe aceleasi randuri: 32 PlatformNotSupportedException, adica metode externe fara
    /// implementare, plus cateva SecurityException ("ECall methods must be packaged into a system module")
    /// si InvalidOperationException pe tipuri abstracte. Tinuta separat fiindca nu spune nimic despre
    /// calitatea recuperarii - un P/Invoke recuperat perfect tot nu are cum sa ruleze aici - si ar strica
    /// media daca ar fi varsata peste oricare dintre celelalte doua.
    /// </summary>
    public const string HostRefused = "host-refused";
}
