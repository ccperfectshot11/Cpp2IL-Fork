using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Cpp2IL.VerifyMod;

/// <summary>
/// Un receptor pentru partea JOCULUI: un obiect il2cpp alocat si lasat pe zero, fara sa i se cheme
/// constructorul.
///
/// De ce trebuie alocat, desi ideea intregii sarcini este ca "in acelasi proces cele doua implementari
/// primesc acelasi pointer". Spus limpede, fiindca este singurul loc unde ideea aceea nu se tine: cele
/// doua metode NU pot primi acelasi obiect. Metoda jocului este un invelis Il2CppInterop al carui tip
/// declarant este, de pilda, Il2Cppquantum.Foo; metoda recuperata traieste in alt AssemblyLoadContext si
/// tipul ei declarant este quantum.Foo. Sunt doua tipuri CLR diferite, iar MethodBase.Invoke verifica
/// receptorul, deci niciunul nu il accepta pe al celuilalt. Un calli peste pointerul de functie ar ocoli
/// verificarea, dar atunci corpul recuperat ar citi campuri dupa layout-ul LUI dintr-un obiect asezat dupa
/// layout-ul nativ, adica ar citi gunoi si ar cadea procesul.
///
/// Ce se poate, si ce chiar se face aici: cele doua obiecte sa fie ECHIVALENTE prin constructie, nu
/// identice prin referinta. GetUninitializedObject da un obiect cu toate campurile pe zero;
/// il2cpp_object_new da tot un obiect cu toata memoria pe zero. Deci ambele implementari citesc aceleasi
/// valori din receptor - zero peste tot - iar restul argumentelor chiar sunt identice bit cu bit. Pentru
/// o metoda care se uita la campurile ei, comparatia ramane corecta; pentru una care dereferentiaza un
/// camp referinta, AMBELE parti dau peste null, ceea ce este tot un rezultat simetric.
///
/// Lectia din ReceiverTransfer se respecta la litera si este motivul pentru care fiecare pas de mai jos
/// verifica zeroul inainte sa mearga mai departe: un pointer nativ null sau invechit dat lui
/// il2cpp_object_get_class ia procesul cu el printr-o violare de acces pe care niciun try nu o prinde.
///
/// Legarea se face prin reflectie, nu prin referinta la compilare, dinadins: modulul nu poate fi
/// compilat sau rulat de cine il scrie, iar un nume de metoda gresit legat direct ar fi o eroare de
/// compilare descoperita de altcineva, pe cand prin reflectie este un mesaj clar in jurnal si o singura
/// categorie de rezultat.
/// </summary>
internal static class NativeReceiver
{
    private static bool _resolved;
    private static MethodInfo _objectNew;
    private static Type _classPointerStore;
    private static string _failure = "";

    public static string Failure => _failure;

    /// <summary>
    /// Leaga il2cpp_object_new si Il2CppClassPointerStore o singura data. Intoarce false daca lipsesc,
    /// caz in care maturarea merge mai departe NUMAI cu metode statice - o degradare pe fata, nu o cadere
    /// tacuta in care metodele de instanta ar disparea din raport fara explicatie.
    /// </summary>
    public static bool Resolve()
    {
        if (_resolved)
            return _objectNew != null && _classPointerStore != null;

        _resolved = true;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if ((assembly.GetName().Name ?? "") != "Il2CppInterop.Runtime")
                continue;

            try
            {
                var il2cpp = assembly.GetType("Il2CppInterop.Runtime.IL2CPP");
                _objectNew = il2cpp?.GetMethod("il2cpp_object_new", BindingFlags.Public | BindingFlags.Static);
                _classPointerStore = assembly.GetType("Il2CppInterop.Runtime.Il2CppClassPointerStore`1");
            }
            catch (Exception ex)
            {
                _failure = "legarea Il2CppInterop a aruncat: " + ex.GetType().Name;
                return false;
            }

            break;
        }

        if (_objectNew == null)
            _failure = "nu s-a gasit IL2CPP.il2cpp_object_new";
        else if (_classPointerStore == null)
            _failure = "nu s-a gasit Il2CppClassPointerStore<>";

        return _objectNew != null && _classPointerStore != null;
    }

    /// <summary>
    /// Clasa nativa din spatele unui tip invelis. Poate fi zero la prima cerere fiindca
    /// Il2CppClassPointerStore se umple lenes - de aceea, cand este zero, se forteaza constructorul static
    /// al invelisului, care este chiar codul generat de Il2CppInterop ce pune pointerul la loc. Fara pasul
    /// asta tipurile pe care jocul nu le-a atins inca ar raporta toate "clasa nativa lipseste", ceea ce ar
    /// arata ca o eroare de indexare si nu ca o incarcare lenesa.
    /// </summary>
    private static IntPtr NativeClassOf(Type interopType, out string reason)
    {
        reason = "";

        try
        {
            var store = _classPointerStore.MakeGenericType(interopType);
            var field = store.GetField("NativeClassPtr", BindingFlags.Public | BindingFlags.Static);

            if (field == null)
            {
                reason = "Il2CppClassPointerStore fara NativeClassPtr";
                return IntPtr.Zero;
            }

            var pointer = field.GetValue(null);
            if (pointer is IntPtr first && first != IntPtr.Zero)
                return first;

            RuntimeHelpers.RunClassConstructor(interopType.TypeHandle);

            var second = field.GetValue(null);
            if (second is IntPtr retry && retry != IntPtr.Zero)
                return retry;

            reason = "clasa nativa este zero si dupa initializarea tipului";
            return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            reason = "cautarea clasei native a aruncat: " + ex.GetType().Name;
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Aloca un obiect al jocului, pe zero, si il imbraca in invelisul lui. Null plus un motiv daca nu se
    /// poate - niciodata un obiect pe jumatate valid.
    ///
    /// il2cpp_object_new cheama si initializarea clasei, deci constructorul STATIC al tipului jocului poate
    /// rula aici. Acela este codul nativ al jocului, nu cod recuperat, si in mod normal a rulat deja demult
    /// cand jocul a atins prima data tipul; ramane totusi singurul cod al jocului pe care maturarea il
    /// porneste fara sa fie chemata metoda, si de aceea este scris aici negru pe alb.
    /// </summary>
    public static object Allocate(Type interopType, out string reason)
    {
        reason = "";

        if (!Resolve())
        {
            reason = _failure;
            return null;
        }

        if (interopType == null || interopType.IsAbstract || interopType.IsInterface || interopType.ContainsGenericParameters)
        {
            reason = "tipul jocului nu se poate aloca (abstract, interfata sau generic deschis)";
            return null;
        }

        // O structura a jocului nu se aloca pe heap-ul il2cpp: invelisul ei este o structura CLR obisnuita
        // si se face cu Activator, la fel ca pe partea recuperata, deci tot pe zero.
        if (interopType.IsValueType)
        {
            try
            {
                return Activator.CreateInstance(interopType);
            }
            catch (Exception ex)
            {
                reason = "structura jocului nu s-a putut construi: " + ex.GetType().Name;
                return null;
            }
        }

        var klass = NativeClassOf(interopType, out reason);
        if (klass == IntPtr.Zero)
            return null;

        object raw;
        try
        {
            raw = _objectNew.Invoke(null, new object[] { klass });
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            reason = "il2cpp_object_new a aruncat: " + inner.GetType().Name;
            return null;
        }

        // Verificarea care nu se sare NICIODATA. Un zero trecut mai departe ajunge, prin constructorul
        // invelisului, la il2cpp_object_get_class - si acela pe un pointer null nu arunca o exceptie, ci
        // omoara procesul.
        if (raw is not IntPtr pointer || pointer == IntPtr.Zero)
        {
            reason = "il2cpp_object_new a intors zero";
            return null;
        }

        try
        {
            // Fiecare invelis generat de Il2CppInterop are un constructor care ia IntPtr; acela este drumul
            // pe care intra si obiectele venite din joc, deci nu inventam aici o a doua forma de invelis.
            return Activator.CreateInstance(interopType, new object[] { pointer });
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            reason = "invelisul nu a acceptat pointerul: " + inner.GetType().Name;
            return null;
        }
    }
}
