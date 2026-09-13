using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Cpp2IL.VerifyCore;

// The identity a signature is filed under. It must be computable on both hosts and identical there, so
// it is built from names and shapes only - never from a metadata token, which Cpp2IL and Il2CppInterop
// hand out independently, and never from the assembly name, which Il2CppInterop prefixes with Il2Cpp.
//
// Cheia nu este numele niciuneia dintre parti, ci o FORMA CANONICA spre care converg amandoua. Fiecare
// regula de mai jos este o ingrosare: trimite mai multe siruri vechi intr-unul nou si niciodata un sir
// vechi in doua. De aici iese proprietatea care conteaza cand se umbla la normalizare - doua chei care
// se potriveau inainte se potrivesc si dupa, deci NICIO pereche formata nu se poate pierde. Singurul
// pret posibil sunt perechile GRESITE, cand doua metode diferite ajung pe aceeasi cheie; de aceea cele
// doua indexuri (VerifyMod.IndexMember si RecoveredCode.Index) scot din pereche cheia ciocnita in loc
// sa pastreze prima venita.
//
// Cateva comentarii de mai jos pomenesc "faza 1" si "faza 2". Acelea erau unelte care nu mai exista -
// una rula assembly-urile recuperate pe desktop, cealalta metodele jocului in joc, si isi comparau
// hash-urile peste granita de proces. Numele au fost lasate acolo unde tin o MASURATOARE, ca sa nu se
// piarda de unde vine cifra; de citit ca "partea recuperata" si "partea jocului".
public static class MethodKeys
{
    /// <summary>
    /// Forma cheii SI a ce se scrie despre ea, ca sa se vada din afara ca un fisier scris mai demult nu
    /// mai este citibil. Se schimba odata cu orice regula de normalizare care muta cheile, si odata cu
    /// orice camp nou pe care il scrie cineva despre o cheie: fiecare fisier care tine chei pe disc -
    /// indexul jocului, lista de lucru, rezultatele - poarta semnul asta in antet si se reface cand nu se
    /// mai potriveste, in loc sa porneasca jocul degeaba peste un fisier care nu mai raspunde la
    /// intrebarea pusa.
    ///
    /// -2: indexul deosebeste acum "cheia nu exista in indexul jocului" de "cheia a iesit din pereche
    ///     fiindca doua metode diferite ale jocului au cazut pe ea". Cheile insele nu s-au mutat fata
    ///     de -1, dar raspunsul la "cat ne costa ciocnirile" se citeste numai dintr-un index nou.
    ///
    /// Nu s-a schimbat la rescrierea din w29, si asta este o veste buna cu bani in ea: cheile ies exact la
    /// fel, deci normalizarea - singura parte masurata care a iesit cum s-a prezis, de la 37.661 la 46.861
    /// de perechi din 47.012, cu zero ciocniri pe partea noastra - nu trebuie nici verificata din nou nici
    /// reglata.
    /// </summary>
    public const string FormatVersion = "w23-mangle-2";

    public static string For(MethodBase method)
    {
        var builder = new StringBuilder();
        builder.Append(Normalise(method.DeclaringType));
        builder.Append("::");
        builder.Append(NormaliseMemberName(method.Name));
        builder.Append('(');

        // The receiver is an input, so it is named in the key like one. Without it a static and an
        // instance overload taking the same arguments would file under a single identity, and Phase 2
        // could not tell from the key alone that it has a receiver to generate at all.
        var written = 0;
        if (!method.IsStatic)
        {
            builder.Append("this:");
            builder.Append(Normalise(method.DeclaringType));
            written++;
        }

        foreach (var parameter in method.GetParameters())
        {
            if (written++ > 0)
                builder.Append(',');

            builder.Append(Normalise(parameter.ParameterType));
        }

        builder.Append(')');
        builder.Append("->");
        builder.Append(Normalise((method as MethodInfo)?.ReturnType));
        return builder.ToString();
    }

    // Citit o singura data: Normalise sta pe drumul fierbinte al indexarii din faza 2, unde este chemat
    // de cateva sute de mii de ori. De aceea rezultatul se tine intr-un dictionar - aceleasi cateva zeci
    // de tipuri (System.String, UnityEngine.Vector3, tipul declarant) revin la fiecare metoda.
    private static readonly bool StripGlobalNamespace =
        Environment.GetEnvironmentVariable("CPP2IL_VERIFY_IL2CPP_GLOBAL_NS") != "0";

    private static readonly Dictionary<Type, string> Cache = new Dictionary<Type, string>();

    // Namespace-ul in care Il2CppInterop isi tine invelisurile de tablou. Verificarea se face pe el, nu
    // numai pe numele tipului, ca un tip AL JOCULUI numit din intamplare "Il2CppStructArray" sa nu fie
    // citit drept tablou.
    private const string InteropArrays = "Il2CppInterop.Runtime.InteropTypes.Arrays";

    private static string Normalise(Type type)
    {
        if (type == null)
            return "void";

        lock (Cache)
        {
            if (Cache.TryGetValue(type, out var cached))
                return cached;
        }

        var builder = new StringBuilder();
        Append(builder, type);
        var name = builder.ToString();

        lock (Cache)
        {
            Cache[type] = name;
        }

        return name;
    }

    private static void Append(StringBuilder builder, Type type)
    {
        if (type.IsByRef)
        {
            Append(builder, type.GetElementType());
            builder.Append('&');
            return;
        }

        if (type.IsPointer)
        {
            Append(builder, type.GetElementType());
            builder.Append('*');
            return;
        }

        if (type.IsArray)
        {
            Append(builder, type.GetElementType());
            builder.Append('[');
            builder.Append(',', type.GetArrayRank() - 1);
            builder.Append(']');
            return;
        }

        // Interopul nu are tablouri: un "byte[]" al jocului ajunge Il2CppStructArray<byte>, un "Foo[]"
        // ajunge Il2CppReferenceArray<Foo>, iar un "string[]" ajunge Il2CppStringArray, care nici macar
        // nu este generic. Aici este singurul loc unde DESFACEM ce a facut interopul in loc sa aplicam
        // noi aceeasi stalcire, si asta fiindca aici desfacerea nu este ambigua: invelisul spune el
        // insusi ce avea inauntru, iar namespace-ul spune ca este invelisul interopului si nu un tip al
        // jocului.
        var element = InteropArrayElement(type);
        if (element != null)
        {
            Append(builder, element);
            builder.Append("[]");
            return;
        }

        // Un parametru generic nelegat (T) nu are FullName deloc, doar Name.
        if (type.IsGenericParameter)
        {
            builder.Append(type.Name);
            return;
        }

        // Type.FullName scrie instantierea generica CALIFICATA CU ASSEMBLY: la noi
        // "List`1[[Foo, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null]]".
        // Inauntrul parantezelor sta un nume de tip caruia nimeni nu ii taie prefixul Il2Cpp, fiindca
        // taietura se uita numai la inceputul sirului. Reconstruim instantierea din bucati, ca fiecare
        // argument sa treaca prin aceeasi normalizare ca un tip de nivel intai - si scapam pe drum de
        // assembly, versiune, cultura si cheie publica, care nu spun nimic despre forma.
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            AppendName(builder, type.GetGenericTypeDefinition());
            builder.Append('<');

            var arguments = type.GetGenericArguments();
            for (var i = 0; i < arguments.Length; i++)
            {
                if (i > 0)
                    builder.Append(',');

                Append(builder, arguments[i]);
            }

            builder.Append('>');
            return;
        }

        AppendName(builder, type);
    }

    private static Type InteropArrayElement(Type type)
    {
        if (!string.Equals(type.Namespace, InteropArrays, StringComparison.Ordinal))
            return null;

        if (string.Equals(type.Name, "Il2CppStringArray", StringComparison.Ordinal))
            return typeof(string);

        if (!type.IsGenericType || type.IsGenericTypeDefinition)
            return null;

        if (!string.Equals(type.Name, "Il2CppStructArray`1", StringComparison.Ordinal) &&
            !string.Equals(type.Name, "Il2CppReferenceArray`1", StringComparison.Ordinal))
            return null;

        var arguments = type.GetGenericArguments();
        return arguments.Length == 1 ? arguments[0] : null;
    }

    // Numele unui tip, pe bucati: intai lantul de tipuri declarante, apoi numele propriu. Se construieste
    // asa si nu din FullName fiindca stalcirea trebuie sa cada pe FIECARE nume in parte - aplicata peste
    // FullName intreg ar sterge si punctele care despart namespace-ul, si nu mai ramane nimic de potrivit.
    private static void AppendName(StringBuilder builder, Type type)
    {
        var declaring = type.IsNested ? type.DeclaringType : null;
        if (declaring != null)
        {
            AppendName(builder, declaring);
            builder.Append('+');
            builder.Append(Mangle(type.Name));
            return;
        }

        // Un tip FARA namespace in joc nu ajunge fara namespace in interop: Il2CppInterop il pune in
        // namespace-ul "Il2Cpp", deci SRMath vine ca "Il2Cpp.SRMath". Taind doar cele sase litere ramane
        // ".SRMath" - cu punctul in fata - iar faza 1 scrie "SRMath", asa ca perechea nu se formeaza
        // niciodata si metoda cade tacut in "only in phase1".
        //
        // Masurat pe cele doua fisiere reale: din 247 de metode la care faza 2 nu a raspuns, 185 sunt
        // tipuri fara namespace, si ZERO din cele 978 la care a raspuns sunt. Nu e o coincidenta - este
        // rata de pierdere 100%. In build-ul jocului sunt 1.603 tipuri puse in namespace-ul "Il2Cpp",
        // iar cu punctul taiat toate cele 1.761 de enum-uri recuperate se potrivesc pe nume cu ale
        // jocului, ceea ce confirma forma taieturii. Niciun tip recuperat nu incepe cu "Il2Cpp", deci
        // faza 1 nu se misca deloc.
        var ns = type.Namespace ?? "";
        var name = ns.Length > 0 ? ns + "." + Mangle(type.Name) : Mangle(type.Name);

        if (StripGlobalNamespace && name.StartsWith("Il2Cpp.", StringComparison.Ordinal))
            name = name.Substring("Il2Cpp.".Length);
        else if (name.StartsWith("Il2Cpp", StringComparison.Ordinal))
            name = name.Substring("Il2Cpp".Length);

        builder.Append(name);
    }

    // Il2CppInterop scrie C#, deci nu poate pastra un nume care contine '<', '>' sau '.': le inlocuieste
    // pe toate cu '_'. Citit din tabela de siruri a lui Assembly-CSharp.dll din Il2CppAssemblies, unde
    // tipul "<Start>d__14" se cheama "_Start_d__14", "<>c__DisplayClass0_0" se cheama
    // "__c__DisplayClass0_0", "<PrivateImplementationDetails>" se cheama "_PrivateImplementationDetails_",
    // iar implementarea explicita de interfata "System.Collections.IEnumerator.Reset" se cheama
    // "System_Collections_IEnumerator_Reset". Formele cu paranteze unghiulare SE VAD si ele in fisier,
    // dar numai ca argument al atributului OriginalName, care pastreaza numele dinainte de redenumire -
    // cine le cauta cu un grep peste DLL le gaseste si crede ca tipurile n-au fost redenumite.
    //
    // Stalcim NOI la fel in loc sa desfacem stalcirea lor, fiindca desfacerea ar fi o ghiceala: din
    // "_A_b__1" nu se mai poate sti unde era '<'. Aplicarea, in schimb, este o functie. Pe partea
    // jocului regula este oricum fara efect - acolo nu mai exista niciun '<', '>' sau '.' de inlocuit.
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

    // Numele unei metode trece prin aceeasi stalcire, cu o singura scutire: ".ctor" si ".cctor" sunt
    // nume pe care si reflectia jocului le da tot asa, deci n-au ce castiga din inlocuire si ar putea
    // doar sa se ciocneasca cu o metoda chemata chiar "_ctor".
    private static string NormaliseMemberName(string name)
    {
        if (name == ".ctor" || name == ".cctor")
            return name;

        return Mangle(name);
    }

    /// <summary>
    /// Aceeasi stalcire, pentru numele unui CAMP.
    ///
    /// Trebuie sa fie chiar functia de mai sus si nu o copie a ei, fiindca raspunde la aceeasi intrebare pe
    /// alt fel de membru: semanarea receptorului potriveste campurile partii recuperate cu cele ale partii
    /// jocului dupa nume, iar Il2CppInterop a trecut deja fiecare nume prin inlocuirea lui '[', ']' si '.'
    /// cu '_'. Un camp de sprijin al unei proprietati se cheama in codul recuperat cu paranteze unghiulare
    /// si "k__BackingField", iar pe partea jocului acelasi camp are toate semnele acelea inlocuite cu '_';
    /// potrivite fara stalcire, cele doua nume nu s-ar intalni niciodata - si tocmai campurile de sprijin
    /// sunt cele in care isi tin proprietatile valoarea.
    /// </summary>
    public static string MangleMember(string name) => Mangle(name);
}
