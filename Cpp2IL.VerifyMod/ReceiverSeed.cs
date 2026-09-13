using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cpp2IL.VerifyCore;

namespace Cpp2IL.VerifyMod;

/// <summary>Cate campuri ale receptorului au primit valori, si cate nu s-au putut oglindi.</summary>
internal sealed class SeedReport
{
    public int Written;
    public int Missing;      // campul recuperat nu are corespondent scriibil pe invelisul jocului
    public int Mismatched;   // are corespondent, dar forma difera - nu se poate da aceeasi valoare
    public int Failed;       // scrierea a aruncat pe una dintre parti
    public string FirstProblem = "";

    /// <summary>
    /// Adevarat cand un camp a ramas SCRIS pe o parte si nescris pe cealalta, adica receptorii nu mai sunt
    /// egali. Se intampla numai daca si scrierea jocului si punerea la loc a campului nostru au aruncat.
    /// Cand este pornit, metoda nu se mai cheama: un verdict dat pe receptori diferiti este mai rau decat
    /// niciun verdict.
    /// </summary>
    public bool Tainted;

    public void Note(string problem)
    {
        if (FirstProblem.Length == 0)
            FirstProblem = problem;
    }
}

/// <summary>
/// Umple AMANDOI receptorii - cel al jocului si cel recuperat - cu aceleasi valori, camp cu camp.
///
/// De ce trebuie facut. Pana acum receptorul era un obiect proaspat cu toate campurile pe zero pe ambele
/// parti. Simetric si corect, dar aproape mut: masuratoarea arata ca randurile etichetate
/// "uninitialised-receiver" ies aproape numai IL_INVALID sau acorduri slabe, fiindca o metoda de instanta
/// citeste primul ei camp, gaseste zero, si iese pe ramura "nu am nimic de facut" - aceeasi ramura pe
/// ambele parti, fara sa spuna nimic despre restul corpului. In dump sunt 24.881 de randuri chemabile cu
/// receptor pe zero, adica marea majoritate a universului.
///
/// De ce NU se cheama constructorul, desi ar fi drumul evident. Pe partea recuperata constructorul este el
/// insusi cod recuperat, adica tocmai codul pe care il masuram. Doi constructori diferiti - unul nativ,
/// unul recuperat - ar aseza doua stari diferite, iar dezacordul de dupa ar fi al constructorilor, nu al
/// metodei. Aceeasi hotarare o ia si ReceiverTransfer, si din acelasi motiv.
///
/// Ce se face in loc: obiectele raman cladite pe zero, si apoi li se SCRIU aceleasi valori. Pe partea
/// recuperata printr-un FieldInfo obisnuit; pe partea jocului prin proprietatea generata de Il2CppInterop
/// pentru acel camp nativ.
///
/// Regula care tine scrierea in afara pericolului, si care nu se scoate: se scrie numai prin proprietati
/// care sunt DOVEDIT accesoare de camp nativ, adica au langa ele un camp static "NativeFieldInfoPtr_&lt;
/// nume&gt;". Il2CppInterop genereaza cate unul pentru fiecare camp nativ - in Assembly-CSharp.dll a
/// jocului sunt 11.414, numarate in binar, nu presupuse - iar setterul unei astfel de proprietati este o
/// singura scriere la un offset, fara niciun apel in codul jocului. O proprietate scrisa de mana, cu logica
/// inauntru, NU are un asemenea camp si deci nu este atinsa: altfel "fabricarea receptorului" ar ajunge sa
/// ruleze cod al jocului inainte de masuratoare.
///
/// A doua regula: se scriu numai campuri de forma primitiva - primitive, enum-uri si structuri facute doar
/// din ele (ValueShape). Un camp referinta nu se scrie NICIODATA. Motivul este cel invatat pe pielea
/// noastra in ReceiverTransfer: atingerea unui camp referinta al unui obiect il2cpp trece prin
/// il2cpp_object_get_class, iar pe un pointer nul sau invechit acela nu arunca, ci ia procesul cu el.
///
/// Ce ramane totusi periculos, spus pe fata: un camp primitiv poate fi folosit de metoda drept lungime de
/// bucla sau indice de tablou, iar IL2CPP compilat pentru livrare nu mai are verificari de interval. De
/// aceea valorile vin din FuzzInputs.TameValue si nu din generatorul obisnuit - marginite intre -8 si 64,
/// fara NaN si fara infinitati. Riscul nu este zero, si de aceea drumul se poate opri intreg cu
/// CPP2IL_ACTIVE_RECEIVER_FIELDS=0 fara recompilare.
/// </summary>
internal static class ReceiverSeed
{
    /// <summary>
    /// Cate campuri se scriu cel mult pe un receptor. Un plafon, nu o optimizare: un tip cu sute de campuri
    /// ar plati sute de apeluri de reflectie pentru fiecare metoda a lui, iar dupa primele cateva zeci de
    /// campuri scrise nu se mai deschide nicio ramura noua.
    /// </summary>
    private const int MaxFields = 64;

    /// <summary>
    /// Scrie aceleasi valori in amandoi receptorii si intoarce cate campuri au primit valoare.
    ///
    /// Zero nu este o eroare: inseamna doar ca tipul nu are campuri primitive pe care sa le putem oglindi,
    /// si atunci receptorul ramane exact ce era pana acum - un obiect pe zero, simetric pe ambele parti.
    /// </summary>
    public static int Fill(object gameReceiver, object ourReceiver, Type ourType,
        ref DeterministicRandom random, SeedReport report)
    {
        if (gameReceiver == null || ourReceiver == null || ourType == null)
            return 0;

        var gameType = gameReceiver.GetType();
        var done = new HashSet<string>(StringComparer.Ordinal);

        for (var walk = ourType; walk != null && walk != typeof(object); walk = walk.BaseType)
        {
            // Aceeasi taietura ca in ReceiverTransfer: campurile mostenite dintr-un modul ciot - m_CachedPtr
            // al lui UnityEngine.Object, de pilda - nu exista pe invelisul interop si nu au ce cauta aici.
            if (ReceiverTransfer.IsStubbedModule(walk.Assembly.GetName().Name))
                break;

            FieldInfo[] fields;
            try
            {
                fields = walk.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch (Exception ex)
            {
                report.Note("campurile nu se pot citi: " + ex.GetType().Name);
                break;
            }

            foreach (var field in fields)
            {
                if (report.Tainted || report.Written >= MaxFields)
                    return report.Written;

                // Un nume se scrie o singura data. Cand un tip derivat ascunde un camp al bazei, cel mai de
                // jos castiga - si tot el a fost cautat pe lantul jocului, deci cele doua scrieri merg in
                // acelasi camp, nu una in cel derivat si alta in cel al bazei.
                if (!done.Add(field.Name))
                    continue;

                Seed(gameReceiver, gameType, ourReceiver, field, ref random, report);
            }
        }

        return report.Written;
    }

    private static void Seed(object gameReceiver, Type gameType, object ourReceiver, FieldInfo field,
        ref DeterministicRandom random, SeedReport report)
    {
        var ourShape = ValueShape.For(field.FieldType);
        if (ourShape == null || ourShape.LeafCount == 0)
            return;     // referinta, structura cu referinte inauntru, sau structura goala - nu se atinge

        var setter = NativeFieldSetter(gameType, field.Name);
        if (setter == null)
        {
            report.Missing++;
            report.Note("fara accesor nativ pentru " + field.Name);
            return;
        }

        var gameShape = ValueShape.For(setter.GetParameters()[0].ParameterType);
        if (gameShape == null || gameShape.LeafCount != ourShape.LeafCount)
        {
            report.Mismatched++;
            report.Note("forma difera la " + field.Name);
            return;
        }

        var leaves = new object[ourShape.LeafCount];
        var shapes = new List<ValueShape>(ourShape.LeafCount);
        Leaves(ourShape, shapes);

        for (var i = 0; i < leaves.Length && i < shapes.Count; i++)
            leaves[i] = Leaf(shapes[i], ref random);

        object ourValue;
        object gameValue;
        try
        {
            var ourAt = 0;
            var gameAt = 0;
            ourValue = ourShape.Materialise(leaves, ref ourAt);
            gameValue = gameShape.Materialise(leaves, ref gameAt);
        }
        catch (Exception ex)
        {
            report.Mismatched++;
            report.Note("valoarea nu s-a putut turna la " + field.Name + ": " + ex.GetType().Name);
            return;
        }

        // Partea recuperata prima, fiindca este cea care practic nu poate esua: un FieldInfo obisnuit pe un
        // obiect obisnuit. Partea jocului dupa, fiindca acolo se poate intampla orice - si daca acolo se
        // intampla, campul nostru se pune la loc pe zero, ca cele doua obiecte sa ramana egale. Ordinea
        // inversa ar lasa jocul cu o valoare pe care noi nu o avem, adica exact asimetria pe care tot
        // fisierul asta o evita.
        object previous;
        try
        {
            previous = field.GetValue(ourReceiver);
            field.SetValue(ourReceiver, ourValue);
        }
        catch (Exception ex)
        {
            report.Failed++;
            report.Note("scriere recuperata " + field.Name + ": " + ex.GetType().Name);
            return;
        }

        try
        {
            setter.Invoke(gameReceiver, new[] { gameValue });
            report.Written++;
        }
        catch (Exception ex)
        {
            report.Failed++;
            report.Note("scriere joc " + field.Name + ": " + ex.GetType().Name);

            try
            {
                field.SetValue(ourReceiver, previous);
            }
            catch (Exception)
            {
                // Nu se mai poate face nimic aici, si tacerea ar fi cea mai rea alegere: receptorul a ramas
                // diferit intre parti, deci verdictul metodei nu mai inseamna nimic. Se insemneaza, iar
                // apelantul renunta la metoda cu totul.
                report.Tainted = true;
                report.Note("campul " + field.Name + " a ramas diferit intre parti");
            }
        }
    }

    /// <summary>
    /// Valoarea unei frunze de camp. Pentru un enum se trage dintre valorile DECLARATE, exact ca la
    /// argumente si din exact acelasi motiv: un enum in afara domeniului lui ajunge indice intr-un tabel pe
    /// care IL2CPP compilat pentru livrare nu il mai verifica, si de acolo pana la moartea procesului nu
    /// mai e nimic de facut. Intr-un camp de receptor primejdia este chiar mai mare decat intr-un argument,
    /// fiindca metoda il citeste ca pe starea ei, nu ca pe o intrare de la altcineva.
    /// </summary>
    private static object Leaf(ValueShape shape, ref DeterministicRandom random)
    {
        if (shape.EnumUnderlying == null)
            return FuzzInputs.TameValue(shape.Kind, ref random);

        try
        {
            var declared = Enum.GetValues(shape.Type);
            if (declared.Length > 0)
            {
                var picked = declared.GetValue((int)(random.Next() % (ulong)declared.Length));
                return Convert.ChangeType(picked, shape.EnumUnderlying, CultureInfo.InvariantCulture);
            }
        }
        catch (Exception)
        {
            // Un enum pe care reflectia refuza sa-l descrie: se cade pe valoarea blanda obisnuita.
        }

        return FuzzInputs.TameValue(shape.Kind, ref random);
    }

    private static void Leaves(ValueShape shape, List<ValueShape> into)
    {
        if (shape.IsLeaf)
        {
            into.Add(shape);
            return;
        }

        foreach (var child in shape.Children)
            Leaves(child, into);
    }

    /// <summary>
    /// Setterul proprietatii care corespunde campului nativ cu numele dat, sau null.
    ///
    /// Doua conditii, amandoua obligatorii: sa existe proprietatea cu setter, SI sa existe langa ea campul
    /// static NativeFieldInfoPtr_&lt;nume&gt;. A doua este cea care deosebeste un accesor generat de camp
    /// nativ - o scriere la un offset - de o proprietate scrisa de mana, al carei setter ar rula cod al
    /// jocului.
    ///
    /// Numele trece prin aceeasi stalcire ca in MethodFuzzer: Il2CppInterop inlocuieste '&lt;', '&gt;' si
    /// '.' cu '_', deci un camp de sprijin "&lt;Foo&gt;k__BackingField" ajunge acolo "_Foo_k__BackingField".
    /// </summary>
    private static MethodInfo NativeFieldSetter(Type gameType, string fieldName)
    {
        var name = Mangle(fieldName);

        for (var walk = gameType; walk != null && walk != typeof(object); walk = walk.BaseType)
        {
            try
            {
                var pointer = walk.GetField("NativeFieldInfoPtr_" + name,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                if (pointer == null)
                    continue;

                // Pointerul nu se ia doar ca sa vedem ca exista campul, ci si ca sa vedem ca este UMPLUT.
                // Il2CppClassPointerStore si fratii lui se umplu lenes, in constructorul static al
                // invelisului: pe un tip al bazei pe care jocul nu l-a atins inca, pointerul este zero. Iar
                // setterul, cu un pointer zero, ajunge la il2cpp_field_get_offset(0) - care nu arunca, ci
                // omoara procesul, exact felul de moarte pe care NativeReceiver il descrie la clase. Deci:
                // se forteaza constructorul static o data si se verifica din nou; daca tot este zero,
                // campul se lasa in pace.
                if (!Filled(pointer, walk))
                    continue;

                var property = walk.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property == null || !property.CanWrite || property.GetIndexParameters().Length != 0)
                    continue;

                var setter = property.GetSetMethod(true);
                if (setter != null && setter.GetParameters().Length == 1)
                    return setter;
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }

    private static bool Filled(FieldInfo pointer, Type declaring)
    {
        try
        {
            if (pointer.GetValue(null) is IntPtr first && first != IntPtr.Zero)
                return true;

            RuntimeHelpers.RunClassConstructor(declaring.TypeHandle);

            return pointer.GetValue(null) is IntPtr second && second != IntPtr.Zero;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Mangle(string name)
    {
        if (name == null)
            return "";

        if (name.IndexOf('<') < 0 && name.IndexOf('>') < 0 && name.IndexOf('.') < 0)
            return name;

        var characters = name.ToCharArray();
        for (var i = 0; i < characters.Length; i++)
            if (characters[i] == '<' || characters[i] == '>' || characters[i] == '.')
                characters[i] = '_';

        return new string(characters);
    }
}
