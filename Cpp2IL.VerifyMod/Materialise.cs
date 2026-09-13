using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Cpp2IL.VerifyCore;

namespace Cpp2IL.VerifyMod;

/// <summary>
/// Turnarea unei retete intr-un tip concret, si citirea inapoi.
///
/// Aici se tine promisiunea pe care o face tot restul uneltei: aceiasi biti, materializati separat in
/// fiecare dintre cele doua universuri de tipuri. Metoda jocului asteapta Il2Cppquantum.Foo, a noastra
/// asteapta quantum.Foo, si sunt doua tipuri CLR diferite in doua contexte de incarcare diferite - deci nu
/// se poate da acelasi obiect amandurora. Ce se da este aceeasi reteta, de doua ori, catre tipul fiecareia.
///
/// Regula care nu se calca, si de care atarna daca cifrele inseamna ceva: daca o valoare nu se poate
/// materializa identic pe amandoua partile, nu se foloseste deloc. Materialise intoarce false si spune de
/// ce, iar metoda ajunge in rezultate ca NOT_REACHED cu motivul scris. Un argument diferit intre parti ar
/// produce un "nu se comporta la fel" mincinos, care este mai rau decat o metoda nemasurata: pe cel din
/// urma il vezi in numaratoare, pe cel dintai il crezi.
/// </summary>
internal static class Materialise
{
    /// <summary>Namespace-ul in care Il2CppInterop isi tine invelisurile de tablou.</summary>
    private const string InteropArrays = "Il2CppInterop.Runtime.InteropTypes.Arrays";

    public static bool Value(Recipe recipe, Type target, out object value, out string problem)
    {
        value = null;
        problem = "";

        switch (recipe.Kind)
        {
            case RecipeKind.Null:
                if (target.IsValueType)
                {
                    problem = "reteta cere null pentru un tip valoare (" + Short(target) + ")";
                    return false;
                }

                return true;

            case RecipeKind.Zero:
                try
                {
                    value = target.IsValueType ? Activator.CreateInstance(target) : null;
                    return true;
                }
                catch (Exception ex)
                {
                    problem = "structura nu s-a putut construi pe zero: " + ex.GetType().Name;
                    return false;
                }

            case RecipeKind.Text:
                if (target != typeof(string))
                {
                    problem = "reteta cere un sir, tipul tinta este " + Short(target);
                    return false;
                }

                value = recipe.Text;
                return true;

            case RecipeKind.Bits:
                return Bits(recipe, target, out value, out problem);

            case RecipeKind.Array:
                return PrimitiveArray(recipe, target, out value, out problem);

            case RecipeKind.TextArray:
                return TextArray(recipe, target, out value, out problem);

            default:
                problem = "reteta nu spune nimic ce se poate fabrica";
                return false;
        }
    }

    private static bool Bits(Recipe recipe, Type target, out object value, out string problem)
    {
        value = null;
        problem = "";

        var shape = ValueShape.For(target);
        if (shape == null)
        {
            problem = "tipul " + Short(target) + " nu se reduce la frunze pe partea asta";
            return false;
        }

        var leaves = new List<ValueShape>();
        shape.CollectLeaves(leaves);

        // Numarul de frunze difera = layout diferit intre cele doua parti. Este un rezultat adevarat despre
        // recuperare, dar NU unul de comportare, si de aceea harnasul ii da verdict propriu in loc sa cada
        // inapoi pe zero, ca inainte - o cadere pe zero il ascundea cu totul.
        if (leaves.Count != recipe.Leaves.Length)
        {
            problem = "forma difera: reteta are " + recipe.Leaves.Length + " frunze, "
                + Short(target) + " are " + leaves.Count;
            return false;
        }

        var values = new object[leaves.Count];
        for (var i = 0; i < leaves.Count; i++)
        {
            if (leaves[i].Kind != recipe.Kinds[i])
            {
                problem = "frunza " + i + " este " + leaves[i].Kind + " aici si " + recipe.Kinds[i] + " in reteta";
                return false;
            }

            values[i] = Leaves.Narrow(leaves[i].Kind, recipe.Leaves[i]);
        }

        try
        {
            var at = 0;
            value = shape.Materialise(values, ref at);
            return true;
        }
        catch (Exception ex)
        {
            problem = "valoarea nu s-a putut cladi: " + ex.GetType().Name;
            return false;
        }
    }

    private static bool PrimitiveArray(Recipe recipe, Type target, out object value, out string problem)
    {
        value = null;
        problem = "";

        if (recipe.Leaves == null)
            return true;

        var element = ElementOf(target, out var interop);
        if (element == null)
        {
            problem = Short(target) + " nu este un tablou pe partea asta";
            return false;
        }

        var shape = ValueShape.For(element);
        if (shape == null || !shape.IsLeaf || shape.Kind != recipe.Kinds[0])
        {
            problem = "elementul tabloului este " + Short(element) + ", reteta cere " + recipe.Kinds[0];
            return false;
        }

        Array managed;
        try
        {
            managed = Array.CreateInstance(element, recipe.Leaves.Length);
            for (var i = 0; i < recipe.Leaves.Length; i++)
            {
                var leaf = Leaves.Narrow(shape.Kind, recipe.Leaves[i]);
                managed.SetValue(shape.EnumUnderlying == null ? leaf : Enum.ToObject(element, leaf), i);
            }
        }
        catch (Exception ex)
        {
            problem = "tabloul nu s-a putut cladi: " + ex.GetType().Name;
            return false;
        }

        if (!interop)
        {
            value = managed;
            return true;
        }

        return Wrap(target, managed, out value, out problem);
    }

    private static bool TextArray(Recipe recipe, Type target, out object value, out string problem)
    {
        value = null;
        problem = "";

        if (recipe.Texts == null)
            return true;

        var element = ElementOf(target, out var interop);
        if (element != typeof(string))
        {
            problem = Short(target) + " nu este un tablou de siruri pe partea asta";
            return false;
        }

        var managed = new string[recipe.Texts.Length];
        Array.Copy(recipe.Texts, managed, recipe.Texts.Length);

        if (!interop)
        {
            value = managed;
            return true;
        }

        return Wrap(target, managed, out value, out problem);
    }

    /// <summary>
    /// Elementul unui tablou, pe oricare dintre parti.
    ///
    /// Interopul nu are tablouri: un "byte[]" al jocului ajunge Il2CppStructArray&lt;byte&gt;, un "Foo[]"
    /// ajunge Il2CppReferenceArray&lt;Foo&gt;, iar un "string[]" ajunge Il2CppStringArray, care nici macar
    /// nu este generic. Verificarea se face pe NAMESPACE, nu doar pe numele tipului, ca un tip al jocului
    /// numit din intamplare "Il2CppStructArray" sa nu fie citit drept tablou - aceeasi grija ca in
    /// MethodKeys, unde desfacerea asta se face pentru chei.
    /// </summary>
    private static Type ElementOf(Type type, out bool interop)
    {
        interop = false;

        if (type.IsArray && type.GetArrayRank() == 1)
            return type.GetElementType();

        if (!string.Equals(type.Namespace, InteropArrays, StringComparison.Ordinal))
            return null;

        if (string.Equals(type.Name, "Il2CppStringArray", StringComparison.Ordinal))
        {
            interop = true;
            return typeof(string);
        }

        if (!type.IsGenericType || type.IsGenericTypeDefinition)
            return null;

        if (!string.Equals(type.Name, "Il2CppStructArray`1", StringComparison.Ordinal)
            && !string.Equals(type.Name, "Il2CppReferenceArray`1", StringComparison.Ordinal))
            return null;

        var arguments = type.GetGenericArguments();
        if (arguments.Length != 1)
            return null;

        interop = true;
        return arguments[0];
    }

    /// <summary>
    /// Imbraca un tablou CLR in invelisul de tablou al interopului.
    ///
    /// Prin reflectie, nu prin referinta la compilare, si nu din pedanterie: cine scrie fisierul asta nu
    /// poate sa il compileze si nici sa il ruleze, deci un constructor legat direct si numit gresit ar fi o
    /// eroare de compilare descoperita de altcineva, pe cand prin reflectie este un motiv limpede intr-o
    /// coloana de rezultat. Se incearca intai constructorul care ia tabloul de-a gata, apoi cel care ia o
    /// lungime plus scrierea element cu element prin indexator.
    /// </summary>
    private static bool Wrap(Type target, Array managed, out object value, out string problem)
    {
        value = null;
        problem = "";

        foreach (var constructor in target.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var parameters = constructor.GetParameters();
            if (parameters.Length != 1 || !parameters[0].ParameterType.IsInstanceOfType(managed))
                continue;

            try
            {
                value = constructor.Invoke(new object[] { managed });
                return true;
            }
            catch (Exception ex)
            {
                problem = "invelisul de tablou a refuzat tabloul: " + Inner(ex).GetType().Name;
                return false;
            }
        }

        foreach (var constructor in target.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var parameters = constructor.GetParameters();
            if (parameters.Length != 1)
                continue;

            var kind = parameters[0].ParameterType;
            if (kind != typeof(long) && kind != typeof(int) && kind != typeof(uint) && kind != typeof(ulong))
                continue;

            try
            {
                var wrapper = constructor.Invoke(new object[]
                {
                    Convert.ChangeType(managed.Length, kind, CultureInfo.InvariantCulture),
                });

                var setter = target.GetMethod("set_Item", BindingFlags.Public | BindingFlags.Instance);
                if (setter == null)
                {
                    problem = "invelisul de tablou nu are indexator de scriere";
                    return false;
                }

                for (var i = 0; i < managed.Length; i++)
                    setter.Invoke(wrapper, new[] { (object)i, managed.GetValue(i) });

                value = wrapper;
                return true;
            }
            catch (Exception ex)
            {
                problem = "invelisul de tablou nu s-a putut umple: " + Inner(ex).GetType().Name;
                return false;
            }
        }

        problem = "invelisul de tablou " + Short(target) + " nu are constructor folosibil";
        return false;
    }

    // ------------------------------------------------------------------------------------------------
    // Membrii receptorului
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Un camp al receptorului, pe oricare dintre parti.
    ///
    /// Pe partea noastra este un camp CLR adevarat. Pe partea jocului, pentru un tip referinta, este o
    /// PROPRIETATE al carei getter si setter citesc si scriu memoria nativa - invelisul Il2CppInterop nu are
    /// campuri de instanta deloc. Pentru un tip valoare al jocului este iar un camp adevarat. Deci se cauta
    /// in amandoua locurile, si de aceea exista clasa asta in loc de un FieldInfo dat mai departe.
    /// </summary>
    public sealed class Member
    {
        private readonly PropertyInfo _property;
        private readonly FieldInfo _field;

        private Member(PropertyInfo property, FieldInfo field)
        {
            _property = property;
            _field = field;
        }

        public Type Type => _property != null ? _property.PropertyType : _field.FieldType;

        public static Member Find(Type declaring, string name)
        {
            try
            {
                var property = declaring.GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (property != null && property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                    return new Member(property, null);
            }
            catch (Exception)
            {
                // Un tip cu doua proprietati cu acelasi nume - se intampla la mostenire - arunca aici.
            }

            try
            {
                var field = declaring.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null && !field.IsLiteral && !field.IsInitOnly)
                    return new Member(null, field);
            }
            catch (Exception)
            {
            }

            return null;
        }

        public bool Write(object instance, object value, out string problem)
        {
            problem = "";

            try
            {
                if (_property != null)
                    _property.SetValue(instance, value, null);
                else
                    _field.SetValue(instance, value);

                return true;
            }
            catch (Exception ex)
            {
                problem = Inner(ex).GetType().Name;
                return false;
            }
        }

        public bool Read(object instance, out object value)
        {
            try
            {
                value = _property != null ? _property.GetValue(instance, null) : _field.GetValue(instance);
                return true;
            }
            catch (Exception)
            {
                // Un getter nativ poate arunca pe un obiect pe jumatate initializat. Nu este o diferenta si
                // nu se numara ca una: campul iese pur si simplu din observatie, pe amandoua partile.
                value = null;
                return false;
            }
        }
    }

    /// <summary>
    /// Citeste un tablou - al nostru sau al jocului - ca sir de biti, ca sa poata fi comparat.
    ///
    /// Doua tablouri din universuri diferite nu se compara cu Equals si nici macar nu au acelasi tip, dar
    /// continutul lor se poate scoate de amandoua partile si atunci se compara element cu element.
    /// </summary>
    public static bool ReadArray(object value, out List<long> bits, out int length)
    {
        bits = new List<long>();
        length = 0;

        if (value == null)
            return true;

        if (value is Array array)
        {
            length = array.Length;
            for (var i = 0; i < array.Length; i++)
                if (!AbsorbElement(array.GetValue(i), bits))
                    return false;

            return true;
        }

        var type = value.GetType();
        if (!string.Equals(type.Namespace, InteropArrays, StringComparison.Ordinal))
            return false;

        try
        {
            var count = type.GetProperty("Length", BindingFlags.Public | BindingFlags.Instance)
                ?? type.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);

            var getter = type.GetMethod("get_Item", BindingFlags.Public | BindingFlags.Instance);
            if (count == null || getter == null)
                return false;

            length = Convert.ToInt32(count.GetValue(value, null), CultureInfo.InvariantCulture);

            for (var i = 0; i < length; i++)
                if (!AbsorbElement(getter.Invoke(value, new object[] { i }), bits))
                    return false;

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool AbsorbElement(object element, List<long> bits)
    {
        if (element == null)
        {
            bits.Add(long.MinValue);
            return true;
        }

        if (element is string text)
        {
            // Sirul intra in comparatie prin propriile caractere, ca doua tablouri de siruri sa se poata
            // compara element cu element fara sa iasa din acelasi sir de long-uri.
            bits.Add(text.Length);
            foreach (var c in text)
                bits.Add(c);

            return true;
        }

        var shape = ValueShape.For(element.GetType());
        if (shape == null)
            return false;

        shape.Absorb(element, bits);
        return true;
    }

    private static Exception Inner(Exception ex) =>
        ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;

    public static string Short(Type type)
    {
        if (type == null)
            return "void";

        try
        {
            return type.FullName ?? type.Name;
        }
        catch (Exception)
        {
            return "?";
        }
    }
}
