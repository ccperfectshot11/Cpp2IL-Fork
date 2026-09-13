using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Cpp2IL.VerifyCore;

namespace Cpp2IL.VerifyMod;

/// <summary>
/// Imbraca o valoare de referinta fabricata o singura data - un sir, un tablou - in fiecare dintre cele
/// doua universuri de tipuri, si refuza sa o dea daca nu incape in amandoua.
///
/// Problema pe care o rezolva. Pana acum un parametru de tip clasa primea null pe ambele parti. Corect ca
/// test, dar steril: masuratoarea arata 91 THREW_BOTH si 67 "amandoua null" pe randurile cu null-uri, si
/// 54 AGREES contra 29 DISAGREES pe cele cu valori fabricate. Un null nu dovedeste ca doua implementari se
/// poarta la fel; dovedeste ca amandoua se impiedica de acelasi null.
///
/// De ce nu se poate da pur si simplu acelasi obiect amandurora. Partea jocului este un invelis
/// Il2CppInterop: un "byte[]" al jocului ajunge acolo Il2CppStructArray&lt;byte&gt;, un "Foo[]" ajunge
/// Il2CppReferenceArray&lt;Foo&gt;, iar un "string[]" ajunge Il2CppStringArray, care nici macar nu este
/// generic. Un System.String insa TRECE ca atare, fiindca interopul il marsaleaza el - de aceea primul
/// lucru incercat aici este pur si simplu "incape asa cum e".
///
/// Regula care nu se incalca: daca valoarea nu se poate turna in AMANDOUA formele, nu se foloseste
/// niciuna si parametrul ramane null de ambele parti. Un argument diferit intre parti ar produce un
/// DISAGREES mincinos, iar un DISAGREES mincinos costa mai mult decat o metoda nemasurata: trimite pe
/// cineva sa caute o eroare de recuperare care nu exista.
///
/// Ce NU se fabrica, si de ce. Colectii generice - List&lt;string&gt;, Dictionary&lt;,&gt; - nu se
/// fabrica: pe partea jocului ar insemna alocare il2cpp plus apeluri Add, adica RULAREA codului jocului
/// inainte de masuratoare, iar pe partea recuperata un List al gazdei, care nu este acelasi tip. Instante
/// de clase ale jocului nu se fabrica prin constructor public: constructorul recuperat este el insusi cod
/// neverificat, deci cele doua obiecte ar fi cladite de doua coduri diferite si orice dezacord de dupa ar
/// fi al constructorilor, nu al metodei masurate.
/// </summary>
internal static class ReferenceArguments
{
    /// <summary>Namespace-ul invelisurilor de tablou ale interopului. Acelasi sir ca in MethodFuzzer.</summary>
    private const string InteropArrays = "Il2CppInterop.Runtime.InteropTypes.Arrays";

    /// <summary>Numele complet al proiectiei lui System.String, cand interopul o genereaza ca tip separat.</summary>
    private const string InteropStringType = "Il2CppSystem.String";

    private static bool _bridgeResolved;
    private static MethodInfo _stringBridge;

    private static readonly Dictionary<Type, ConstructorInfo> ArrayCtorCache = new Dictionary<Type, ConstructorInfo>();

    /// <summary>
    /// Fabrica valoarea si o da in ambele forme. Intoarce false cand nu se poate - si atunci apelantul
    /// pune null pe ambele parti, ca pana acum.
    /// </summary>
    public static bool TryBuild(Type gameType, Type ourType, ref DeterministicRandom random,
        out object gameValue, out object ourValue, out string label)
    {
        gameValue = null;
        ourValue = null;
        label = "";

        if (gameType == null || ourType == null)
            return false;

        if (gameType.IsByRef || gameType.IsPointer || ourType.IsByRef || ourType.IsPointer)
            return false;

        if (ourType == typeof(string))
        {
            var text = ReferenceValues.Text(ref random);
            if (!Adapt(text, gameType, out gameValue))
                return false;

            ourValue = text;
            label = ArgQuality.Text;
            return true;
        }

        // System.Object primeste tot un sir. Pe partea recuperata este un System.String al gazdei, pe
        // partea jocului proiectia lui System.String - adica, in fiecare univers, EXACT tipul pe care
        // codul de acolo il cunoaste drept "string". Deci si intrebarea "obiectul este un string?" are
        // acelasi raspuns pe ambele parti. Eticheta este totusi separata, ca sa se poata scoate din
        // numaratoare daca masuratoarea arata ca drumul asta produce dezacorduri pe care celelalte nu le
        // produc.
        if (ourType == typeof(object))
        {
            var text = ReferenceValues.Text(ref random);
            if (!Adapt(text, gameType, out gameValue))
                return false;

            ourValue = text;
            label = ArgQuality.ObjectText;
            return true;
        }

        if (ourType.IsArray && ourType.GetArrayRank() == 1)
            return TryBuildArray(gameType, ourType, ref random, out gameValue, out ourValue, out label);

        return false;
    }

    private static bool TryBuildArray(Type gameType, Type ourType, ref DeterministicRandom random,
        out object gameValue, out object ourValue, out string label)
    {
        gameValue = null;
        ourValue = null;
        label = "";

        var element = ourType.GetElementType();
        if (element == null || element.IsPointer || element.IsByRef)
            return false;

        var length = ReferenceValues.Length(ref random);
        var kind = ReferenceValues.KindOfElement(element);

        Array seed;
        if (kind.HasValue)
        {
            seed = ReferenceValues.PrimitiveArray(kind.Value, length, ref random);
        }
        else if (element == typeof(string))
        {
            seed = ReferenceValues.TextArray(length, ref random);
        }
        else
        {
            // Element pe care nu stim sa-l umplem - o clasa a jocului, un enum, o structura. Tabloul se
            // da gol, si asta NU este acelasi lucru cu null: o metoda care face Length sau foreach
            // primeste 0 si merge mai departe, in loc sa se opreasca in NullReferenceException pe prima
            // linie. Este cea mai slaba treapta dintre cele adevarate, si de aceea are eticheta ei.
            try
            {
                seed = Array.CreateInstance(element, 0);
            }
            catch (Exception)
            {
                return false;
            }
        }

        if (!Adapt(seed, gameType, out gameValue))
            return false;

        ourValue = seed;
        label = seed.Length == 0 ? ArgQuality.EmptyArray : ArgQuality.Array;
        return true;
    }

    // --------------------------------------------------------------------------------------------
    // Turnarea intr-un tip anume
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// Aduce o valoare a gazdei la tipul <paramref name="target"/>, sau spune ca nu se poate. Nu arunca
    /// niciodata si nu construieste nimic pe jumatate.
    /// </summary>
    private static bool Adapt(object managed, Type target, out object value)
    {
        value = null;

        if (target == null)
            return false;

        if (managed == null)
            return !target.IsValueType;

        // Tablourile se intreaba INAINTEA lui IsInstanceOfType, si asta este o reparatie, nu o preferinta
        // de stil. Cand ambele parti cer acelasi tip - un byte[] recuperat si un byte[] al gazdei -
        // scurtatura "incape asa cum e" ar da ACELASI obiect amandurora. Un tablou este insa mutabil:
        // metoda jocului scrie in el, si apoi metoda recuperata ar primi ca intrare rezultatul celeilalte.
        // Cele doua apeluri nu ar mai fi independente, iar un dezacord de acolo n-ar spune nimic despre
        // codul recuperat. Deci un tablou se COPIAZA intotdeauna. Sirurile nu au nevoie de asta: sunt
        // neschimbatoare, deci acelasi obiect este acelasi lucru cu doua copii.
        if (managed is Array array)
            return AdaptArray(array, target, out value);

        // Drumul cel mai scurt si cel mai des: un System.String trece neatins prin interop, fiindca acolo
        // marsalarea sirurilor este facuta de invelis.
        if (target.IsInstanceOfType(managed))
        {
            value = managed;
            return true;
        }

        if (managed is string text)
            return AdaptString(text, target, out value);

        // Primitive si enum-uri: acelasi drum ca in ReceiverTransfer.Coerce, prin intregul de dedesubt.
        return AdaptPrimitive(managed, target, out value);
    }

    private static bool AdaptPrimitive(object managed, Type target, out object value)
    {
        value = null;

        var sourceType = managed.GetType();
        if (!sourceType.IsValueType || !target.IsValueType)
            return false;

        var targetLeaf = target.IsEnum ? Enum.GetUnderlyingType(target) : target;
        var sourceLeaf = sourceType.IsEnum ? Enum.GetUnderlyingType(sourceType) : sourceType;

        if (ReferenceValues.KindOfElement(targetLeaf) == null || ReferenceValues.KindOfElement(sourceLeaf) == null)
            return false;

        try
        {
            var number = sourceType.IsEnum ? Convert.ChangeType(managed, sourceLeaf, CultureInfo.InvariantCulture) : managed;
            var widened = Convert.ChangeType(number, targetLeaf, CultureInfo.InvariantCulture);
            value = target.IsEnum ? Enum.ToObject(target, widened) : widened;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Un sir catre un tip care nu este System.String. Doua drumuri, amandoua prin conversii declarate de
    /// interop - niciodata prin pointeri nativi facuti de noi.
    ///
    /// De ce nu prin pointeri. Un il2cpp_string_new facut aici da un sir nativ pe care nimeni nu il tine
    /// de mana: intre fabricare si apel poate trece o colectare de gunoi a jocului, iar atunci argumentul
    /// nu mai este un sir, ci o adresa moarta - si o adresa moarta data codului nativ nu arunca, ci omoara
    /// procesul. Conversiile interopului trec obiectul prin Il2CppObjectPool, care il tine cu un GCHandle;
    /// asta este singura forma in care sirul supravietuieste pana la apel.
    /// </summary>
    private static bool AdaptString(string text, Type target, out object value)
    {
        value = null;

        var direct = ImplicitFrom(target, typeof(string), target);
        if (direct != null)
            return Invoke(direct, text, out value);

        var bridge = StringBridge();
        if (bridge == null || !target.IsAssignableFrom(bridge.ReturnType))
            return false;

        return Invoke(bridge, text, out value);
    }

    private static bool Invoke(MethodInfo conversion, object argument, out object value)
    {
        value = null;

        try
        {
            value = conversion.Invoke(null, new[] { argument });
            return value != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Conversia string -&gt; proiectia jocului a lui System.String, cautata o singura data. Cand interopul
    /// nu genereaza un tip separat pentru String - fiindca il marsaleaza direct - nu se gaseste nimic, si
    /// atunci parametrii de tip System.Object raman null. Degradare pe fata, nu cadere tacuta.
    /// </summary>
    private static MethodInfo StringBridge()
    {
        if (_bridgeResolved)
            return _stringBridge;

        _bridgeResolved = true;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type projected;
            try
            {
                projected = assembly.GetType(InteropStringType, false);
            }
            catch (Exception)
            {
                continue;
            }

            if (projected == null)
                continue;

            _stringBridge = ImplicitFrom(projected, typeof(string), null);
            if (_stringBridge != null)
                return _stringBridge;
        }

        return _stringBridge;
    }

    /// <summary>
    /// Un op_Implicit sau op_Explicit public si static, declarat pe <paramref name="declaring"/>, care ia
    /// exact un parametru de tipul <paramref name="from"/>. Cand <paramref name="mustFit"/> este dat, se
    /// cere si ca rezultatul sa incapa in el.
    /// </summary>
    private static MethodInfo ImplicitFrom(Type declaring, Type from, Type mustFit)
    {
        MethodInfo[] methods;
        try
        {
            methods = declaring.GetMethods(BindingFlags.Public | BindingFlags.Static);
        }
        catch (Exception)
        {
            return null;
        }

        foreach (var method in methods)
        {
            if (method.Name != "op_Implicit" && method.Name != "op_Explicit")
                continue;

            var parameters = method.GetParameters();
            if (parameters.Length != 1 || parameters[0].ParameterType != from)
                continue;

            if (method.ReturnType == typeof(void) || method.ContainsGenericParameters)
                continue;

            if (mustFit != null && !mustFit.IsAssignableFrom(method.ReturnType))
                continue;

            return method;
        }

        return null;
    }

    // --------------------------------------------------------------------------------------------
    // Tablouri
    // --------------------------------------------------------------------------------------------

    private static bool AdaptArray(Array managed, Type target, out object value)
    {
        value = null;

        // Tot un tablou al gazdei, doar cu alt element.
        if (target.IsArray)
        {
            if (target.GetArrayRank() != 1)
                return false;

            return CopyInto(managed, target.GetElementType(), out value);
        }

        // Un parametru declarat System.Object, System.Array sau IEnumerable caruia i se da un tablou:
        // tot o copie, din exact acelasi motiv ca mai sus.
        if (!string.Equals(target.Namespace, InteropArrays, StringComparison.Ordinal))
        {
            if (!target.IsInstanceOfType(managed))
                return false;

            try
            {
                value = managed.Clone();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        var element = InteropElement(target);
        if (element == null)
            return false;

        if (!CopyInto(managed, element, out var bridged))
            return false;

        var content = (Array)bridged;

        var fromArray = ArrayConstructor(target, content.GetType());
        if (fromArray != null && TryConstruct(fromArray, new object[] { content }, out value))
            return true;

        // Al doilea drum: constructorul pe lungime plus indexatorul. Exista fiindca nu toate invelisurile
        // au acelasi set de constructori, iar un tablou gol construit asa este tot un tablou gol.
        return BuildBySize(target, content, out value);
    }

    /// <summary>Copiaza elementele intr-un tablou al gazdei cu alt tip de element, sau esueaza.</summary>
    private static bool CopyInto(Array source, Type element, out object value)
    {
        value = null;

        if (element == null)
            return false;

        Array result;
        try
        {
            result = Array.CreateInstance(element, source.Length);
        }
        catch (Exception)
        {
            return false;
        }

        for (var i = 0; i < source.Length; i++)
        {
            object item;
            try
            {
                item = source.GetValue(i);
            }
            catch (Exception)
            {
                return false;
            }

            if (!Adapt(item, element, out var converted))
                return false;

            try
            {
                result.SetValue(converted, i);
            }
            catch (Exception)
            {
                return false;
            }
        }

        value = result;
        return true;
    }

    /// <summary>
    /// Ce tine inauntru un invelis de tablou al interopului: Il2CppStringArray tine siruri,
    /// Il2CppStructArray&lt;T&gt; si Il2CppReferenceArray&lt;T&gt; tin T. Aceeasi desfacere ca in
    /// MethodFuzzer.InteropArrayElement - repetata aici fiindca acolo este privata si fiindca cele doua
    /// assembly-uri nu se vad unul pe altul la nivelul asta.
    /// </summary>
    private static Type InteropElement(Type type)
    {
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

    private static ConstructorInfo ArrayConstructor(Type target, Type arrayType)
    {
        ConstructorInfo found;

        lock (ArrayCtorCache)
        {
            if (!ArrayCtorCache.TryGetValue(target, out found))
            {
                found = FindArrayConstructor(target);
                ArrayCtorCache[target] = found;
            }
        }

        if (found == null)
            return null;

        return found.GetParameters()[0].ParameterType.IsAssignableFrom(arrayType) ? found : null;
    }

    private static ConstructorInfo FindArrayConstructor(Type target)
    {
        try
        {
            foreach (var constructor in target.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                var parameters = constructor.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType.IsArray)
                    return constructor;
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    private static bool TryConstruct(ConstructorInfo constructor, object[] arguments, out object value)
    {
        value = null;

        try
        {
            value = constructor.Invoke(arguments);
            return value != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool BuildBySize(Type target, Array content, out object value)
    {
        value = null;

        ConstructorInfo bySize = null;

        try
        {
            foreach (var constructor in target.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                var parameters = constructor.GetParameters();
                if (parameters.Length != 1)
                    continue;

                var parameterType = parameters[0].ParameterType;
                if (parameterType == typeof(long) || parameterType == typeof(int)
                    || parameterType == typeof(uint) || parameterType == typeof(ulong))
                {
                    bySize = constructor;
                    break;
                }
            }
        }
        catch (Exception)
        {
            return false;
        }

        if (bySize == null)
            return false;

        object built;
        try
        {
            var size = Convert.ChangeType(content.Length, bySize.GetParameters()[0].ParameterType, CultureInfo.InvariantCulture);
            built = bySize.Invoke(new[] { size });
        }
        catch (Exception)
        {
            return false;
        }

        if (built == null)
            return false;

        if (content.Length == 0)
        {
            value = built;
            return true;
        }

        MethodInfo setter;
        try
        {
            setter = target.GetMethod("set_Item", BindingFlags.Public | BindingFlags.Instance);
        }
        catch (Exception)
        {
            return false;
        }

        if (setter == null || setter.GetParameters().Length != 2)
            return false;

        var indexType = setter.GetParameters()[0].ParameterType;

        for (var i = 0; i < content.Length; i++)
        {
            try
            {
                var index = Convert.ChangeType(i, indexType, CultureInfo.InvariantCulture);
                setter.Invoke(built, new[] { index, content.GetValue(i) });
            }
            catch (Exception)
            {
                return false;
            }
        }

        value = built;
        return true;
    }
}
