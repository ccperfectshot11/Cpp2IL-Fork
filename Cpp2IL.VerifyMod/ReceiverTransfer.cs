using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Cpp2IL.VerifyMod;

/// <summary>
/// Cate campuri ale receptorului au ajuns de la obiectul REAL al jocului in obiectul recuperat, si cate
/// nu. Fara numerele astea un dezacord nu se poate atribui: o metoda care raspunde altceva pentru ca nu
/// i-am dat starea completa arata exact la fel ca una recuperata gresit.
/// </summary>
internal sealed class TransferReport
{
    public int Copied;
    public int NotFound;            // campul recuperat nu are corespondent pe obiectul jocului
    public int SkippedReference;    // exista, dar tine o referinta pe care nu o putem traduce
    public int Failed;              // exista si este primitiv, dar citirea sau scrierea a aruncat
    public string FirstProblem = "";

    // Singura stare in care un dezacord inseamna cu adevarat "corpul recuperat calculeaza altceva".
    public bool Complete => NotFound == 0 && SkippedReference == 0 && Failed == 0;

    public int Total => Copied + NotFound + SkippedReference + Failed;

    public void Note(string problem)
    {
        if (FirstProblem.Length == 0)
            FirstProblem = problem;
    }
}

/// <summary>
/// Umple un obiect de tipul RECUPERAT cu starea unui obiect REAL al jocului, camp cu camp, dupa nume.
///
/// Asta este piesa care lipsea si din cauza careia doua treimi din cod sunt nemasurabile. O metoda de
/// instanta pe o clasa nu se poate compara prin fuzzing fiindca receptorul ar trebui FABRICAT identic in
/// doua procese, ceea ce nu se poate: Il2CppInterop nu proiecteaza o clasa a jocului ca pe o clasa cu
/// campurile ei, ci ca pe un invelis peste un pointer nativ. Dar receptorul nu trebuie fabricat - jocul
/// are deja unul, viu si coerent. Trebuie doar CITIT.
///
/// Forma proiectiei a fost verificata pe binarul real, nu presupusa: in
/// MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll sunt 11.414 campuri native - cate un
/// NativeFieldInfoPtr_ pentru fiecare - si 11.323 dintre ele au si un "get_&lt;nume&gt;", 11.325 un
/// "set_&lt;nume&gt;". Adica 99,2% sunt expuse ca PROPRIETATI cu exact numele campului. De aceea
/// cautarea de mai jos incearca intai proprietatea si abia apoi campul.
///
/// Obiectul recuperat se cladeste cu GetUninitializedObject si nu cu un constructor recuperat, din
/// acelasi motiv pentru care o face si recensamantul (vezi CensusArguments.PlanReceiver): un constructor
/// este el insusi cod neverificat, iar daca receptorul este cladit de cod gresit atunci raspunsul
/// metodei nu spune nimic despre metoda.
/// </summary>
internal static class ReceiverTransfer
{
    // Aceeasi lista ca Selector.IsStubbedModule si ca AsmResolverDllOutputFormatIlRecovery: modulele pe
    // care Cpp2IL nu le analizeaza deloc. Campurile mostenite de acolo - m_CachedPtr al lui
    // UnityEngine.Object, de pilda - nu exista pe invelisul interop si ar umple raportul cu "not found"
    // care nu spune nimic despre codul recuperat.
    // Internal si nu private: ReceiverSeed taie exact acelasi lant de mostenire cand SCRIE campuri, si o
    // a doua copie a listei ar putea sa se desincronizeze - iar atunci un camp ar fi citit dintr-un modul
    // ciot de o parte si sarit de cealalta.
    internal static bool IsStubbedModule(string assemblyName) =>
        assemblyName == null
        || assemblyName.StartsWith("UnityEngine.", StringComparison.Ordinal)
        || assemblyName.StartsWith("Unity.", StringComparison.Ordinal)
        || assemblyName.StartsWith("System.", StringComparison.Ordinal)
        || assemblyName == "System"
        || assemblyName.StartsWith("mscorlib", StringComparison.Ordinal);

    public static object Build(object gameReceiver, Type recoveredType, TransferReport report)
    {
        var target = RuntimeHelpers.GetUninitializedObject(recoveredType);
        var gameType = gameReceiver.GetType();

        for (var type = recoveredType; type != null && type != typeof(object); type = type.BaseType)
        {
            if (IsStubbedModule(type.Assembly.GetName().Name))
                break;

            FieldInfo[] fields;
            try
            {
                fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch (Exception)
            {
                break;
            }

            foreach (var field in fields)
                CopyOne(gameReceiver, gameType, target, field, report);
        }

        return target;
    }

    private static void CopyOne(object gameReceiver, Type gameType, object target, FieldInfo field, TransferReport report)
    {
        // Verificarea vine INAINTEA citirii, si asta nu e o optimizare.
        //
        // Un camp de tip referinta nu se poate transfera oricum - tipul recuperat si cel din interop sunt
        // tipuri diferite pentru runtime - deci Coerce l-ar refuza cateva linii mai jos. Dar pana acolo
        // apucam sa-l CITIM, iar citirea trece prin getterul de interop, care face Il2CppObjectPool.Get pe
        // pointerul nativ. Cand pointerul ala e nul sau invalid, il2cpp_object_get_class calca pe memorie
        // protejata si procesul moare - nu arunca, moare, deci niciun try nu-l prinde.
        //
        // S-a intamplat la BackendBattlePass::get_HasPurchased: transferul a citit _FreePassRewards, un
        // camp de referinta neinitializat in acel moment, si a luat tot jocul cu el.
        if (!field.FieldType.IsValueType && field.FieldType != typeof(string))
        {
            report.SkippedReference++;
            report.Note("referinta, nu se citeste " + field.Name + " (" + field.FieldType.Name + ")");
            return;
        }

        if (!TryRead(gameReceiver, gameType, field.Name, out var raw))
        {
            report.NotFound++;
            report.Note("lipseste " + field.Name);
            return;
        }

        if (!Coerce(raw, field.FieldType, out var converted))
        {
            report.SkippedReference++;
            report.Note("netradus " + field.Name + " (" + field.FieldType.Name + ")");
            return;
        }

        try
        {
            field.SetValue(target, converted);
            report.Copied++;
        }
        catch (Exception ex)
        {
            report.Failed++;
            report.Note("scriere " + field.Name + ": " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// Proprietatea intai, campul dupa. Ordinea conteaza: pe partea jocului campul nativ ESTE o
    /// proprietate, iar un camp cu acelasi nume ar fi, in cel mai bun caz, cache-ul intern al
    /// invelisului - deci valoarea gresita, citita fara nicio eroare.
    /// </summary>
    private static bool TryRead(object instance, Type type, string name, out object value)
    {
        value = null;

        for (var walk = type; walk != null && walk != typeof(object); walk = walk.BaseType)
        {
            try
            {
                var property = walk.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
                {
                    value = property.GetValue(instance);
                    return true;
                }

                var field = walk.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    value = field.GetValue(instance);
                    return true;
                }
            }
            catch (Exception)
            {
                // O citire care arunca inseamna un camp nativ care nu se poate atinge de aici - acelasi
                // lucru, din punctul nostru de vedere, cu unul care nu exista.
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Aduce valoarea citita din joc la TIPUL recuperat. Numai primitive, enum-uri si structuri facute
    /// doar din ele: orice altceva ar cere un graf de obiecte, adica exact problema pe care substitutia
    /// nu o rezolva si nu pretinde ca o rezolva.
    /// </summary>
    internal static bool Coerce(object raw, Type targetType, out object converted)
    {
        converted = null;

        if (raw == null)
            return !targetType.IsValueType;

        if (targetType.IsInstanceOfType(raw))
        {
            converted = raw;
            return true;
        }

        // Un enum este un intreg pe ambele parti - verificat pe metadate de faza 1/2: toate cele 1.761 de
        // enum-uri recuperate au acelasi tip de baza ca ale jocului. Drumul trece obligatoriu prin
        // intregul de dedesubt, fiindca cele doua tipuri enum sunt tipuri CLR diferite si o atribuire
        // directa ar fi refuzata de reflection.
        var sourceType = raw.GetType();
        var targetLeaf = targetType.IsEnum ? Enum.GetUnderlyingType(targetType) : targetType;
        var sourceLeaf = sourceType.IsEnum ? Enum.GetUnderlyingType(sourceType) : sourceType;

        if (IsPrimitiveLeaf(targetLeaf) && IsPrimitiveLeaf(sourceLeaf))
        {
            try
            {
                var number = sourceType.IsEnum ? Convert.ChangeType(raw, sourceLeaf, CultureInfo.InvariantCulture) : raw;
                var widened = Convert.ChangeType(number, targetLeaf, CultureInfo.InvariantCulture);
                converted = targetType.IsEnum ? Enum.ToObject(targetType, widened) : widened;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // O structura numai-primitive: aceleasi campuri, aceleasi nume, pe ambele parti. Il2CppInterop
        // proiecteaza un tip valoare blittable ca pe o structura adevarata, deci campurile chiar sunt
        // acolo si se citesc la fel ca ale oricarei structuri.
        if (targetType.IsValueType && !targetType.IsPrimitive && !targetType.IsEnum && sourceType.IsValueType)
            return CoerceStruct(raw, sourceType, targetType, out converted);

        return false;
    }

    private static bool CoerceStruct(object raw, Type sourceType, Type targetType, out object converted)
    {
        converted = null;

        object box;
        try
        {
            box = Activator.CreateInstance(targetType);
        }
        catch (Exception)
        {
            return false;
        }

        FieldInfo[] fields;
        try
        {
            fields = targetType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch (Exception)
        {
            return false;
        }

        foreach (var field in fields)
        {
            if (!TryRead(raw, sourceType, field.Name, out var inner))
                return false;

            if (!Coerce(inner, field.FieldType, out var innerConverted))
                return false;

            try
            {
                field.SetValue(box, innerConverted);
            }
            catch (Exception)
            {
                return false;
            }
        }

        converted = box;
        return true;
    }

    private static bool IsPrimitiveLeaf(Type type) =>
        type == typeof(bool) || type == typeof(char) || type == typeof(sbyte) || type == typeof(byte)
        || type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint)
        || type == typeof(long) || type == typeof(ulong) || type == typeof(float) || type == typeof(double);
}
