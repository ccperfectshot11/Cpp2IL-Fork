using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Cpp2IL.VerifyCore;

// The twelve primitives a fuzzed value can bottom out in. IntPtr/UIntPtr are deliberately absent: their
// width is a property of the host, so a signature containing one would differ between a 64-bit harness
// and a 32-bit game build for reasons that have nothing to do with whether the code was recovered right.
public enum LeafKind
{
    Bool,
    Char,
    SByte,
    Byte,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Single,
    Double,
}

// A type reduced to the flat list of primitives it is made of. Everything downstream - value generation,
// materialisation, hashing - works off the shape rather than off the Type, because the two hosts do NOT
// agree on Type identity: Phase 1 loads Cpp2IL's recovered PhotonDeterministic.dll, Phase 2 talks to
// Il2CppInterop's projection of the real one. They agree on "a struct holding one long", and that is
// exactly what a shape is.
public sealed class ValueShape
{
    private ValueShape(Type type, LeafKind kind, Type enumUnderlying = null)
    {
        Type = type;
        Kind = kind;
        IsLeaf = true;
        Fields = EmptyFields;
        Children = EmptyShapes;
        LeafCount = 1;
        EnumUnderlying = enumUnderlying;
    }

    private ValueShape(Type type, FieldInfo[] fields, ValueShape[] children)
    {
        Type = type;
        IsLeaf = false;
        Fields = fields;
        Children = children;
        var total = 0;
        foreach (var child in children)
            total += child.LeafCount;
        LeafCount = total;
    }

    private static readonly FieldInfo[] EmptyFields = new FieldInfo[0];
    private static readonly ValueShape[] EmptyShapes = new ValueShape[0];

    public Type Type { get; }
    public LeafKind Kind { get; }
    public bool IsLeaf { get; }
    public FieldInfo[] Fields { get; }
    public ValueShape[] Children { get; }

    // How many primitives this type flattens to. Zero is legal (an empty struct) and means the value
    // carries no information - the fuzzer still emits it, it just contributes nothing to the hash.
    public int LeafCount { get; }

    // Nenul doar cand frunza este un enum: tipul intreg de sub el. Valoarea generata este intregul,
    // dar apelul are nevoie de enum, iar hash-ul are nevoie iar de intreg - vezi Materialise si Absorb.
    public Type EnumUnderlying { get; }

    public static ValueShape For(Type type) => Build(type, new HashSet<Type>(), 0);

    private static ValueShape Build(Type type, HashSet<Type> open, int depth)
    {
        if (type == null || depth > 8)
            return null;

        // Anything that can point somewhere is out. A by-ref or pointer parameter would let the method
        // write through it, and an array or class argument would need a heap graph the two hosts could
        // never build identically.
        if (type.IsByRef || type.IsPointer || type.IsArray || !type.IsValueType)
            return null;

        var kind = KindOf(type);
        if (kind.HasValue)
            return new ValueShape(type, kind.Value);

        if (type.IsEnum)
            return EnumLeaf(type);

        if (type.IsGenericType || type.ContainsGenericParameters)
            return null;

        // A struct cannot contain itself, but a struct recovered from a wrong field layout can appear to,
        // and that reads as an infinite shape rather than as the bug it is.
        if (!open.Add(type))
            return null;

        try
        {
            var fields = InstanceFields(type);
            var children = new ValueShape[fields.Length];
            for (var i = 0; i < fields.Length; i++)
            {
                var child = Build(fields[i].FieldType, open, depth + 1);
                if (child == null)
                    return null;

                children[i] = child;
            }

            return new ValueShape(type, fields, children);
        }
        finally
        {
            open.Remove(type);
        }
    }

    // Un enum ESTE un intreg la rulare, si - spre deosebire de ce spunea nota de dinainte - cele doua
    // gazde SUNT de acord asupra lui, fara nicio tabela de corespondenta. Verificat pe metadatele reale,
    // nu presupus: toate cele 1.761 de enum-uri din build-ul recuperat exista si in Il2CppAssemblies ale
    // jocului, toate 1.761 se potrivesc pe nume dupa ce MethodKeys.Normalise taie prefixul, si toate
    // 1.761 au ACELASI tip intreg dedesubt. Zero nepotriviri. Deci o frunza de enum genereaza aceiasi
    // biti pe ambele parti.
    //
    // Dinadins NU este pus dupa un comutator, desi latirea corespunzatoare din Selector este: selectia
    // decide ce metode sunt CERUTE, iar faza 2 raspunde doar la ce i se cere. Daca doar una dintre
    // gazde ar sti sa construiasca un enum, metoda ar fi masurata pe o singura parte si ar cadea din
    // comparatie fara sa spuna nimic - exact asimetria pe care restul fisierului o evita.
    private static ValueShape EnumLeaf(Type type)
    {
        var underlying = Enum.GetUnderlyingType(type);
        var kind = KindOf(underlying);

        // Numai intregi. Un enum pe virgula mobila nu este legal in C# si nu exista niciunul in build,
        // dar un enum recuperat gresit ar putea arata asa, si Enum.ToObject nu stie sa-l construiasca.
        if (!kind.HasValue || kind.Value == LeafKind.Single || kind.Value == LeafKind.Double
            || kind.Value == LeafKind.Bool || kind.Value == LeafKind.Char)
            return null;

        return new ValueShape(type, kind.Value, underlying);
    }

    // Declaration order, not reflection order: GetFields makes no ordering promise, and a shape whose
    // leaf order depends on which runtime enumerated it would produce two different hashes for identical
    // behaviour. Metadata tokens are handed out in declaration order by both metadata systems.
    public static FieldInfo[] InstanceFields(Type type)
    {
        var fields = new List<FieldInfo>(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        fields.Sort((a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
        return fields.ToArray();
    }

    private static LeafKind? KindOf(Type type)
    {
        if (type == typeof(bool)) return LeafKind.Bool;
        if (type == typeof(char)) return LeafKind.Char;
        if (type == typeof(sbyte)) return LeafKind.SByte;
        if (type == typeof(byte)) return LeafKind.Byte;
        if (type == typeof(short)) return LeafKind.Int16;
        if (type == typeof(ushort)) return LeafKind.UInt16;
        if (type == typeof(int)) return LeafKind.Int32;
        if (type == typeof(uint)) return LeafKind.UInt32;
        if (type == typeof(long)) return LeafKind.Int64;
        if (type == typeof(ulong)) return LeafKind.UInt64;
        if (type == typeof(float)) return LeafKind.Single;
        if (type == typeof(double)) return LeafKind.Double;
        return null;
    }

    // Fills the leaf slots of a fresh instance from the supplied values, left to right. Structs are built
    // through boxing because setting a field on an unboxed struct through reflection would write to a
    // copy - the classic silent no-op.
    public object Materialise(object[] leaves, ref int next)
    {
        if (IsLeaf)
        {
            var leaf = leaves[next++];

            // Reflection refuza un Int32 acolo unde semnatura cere enum-ul, asa ca valoarea trebuie
            // imbracata inapoi in tipul ei. Bitii raman exact aceiasi.
            return EnumUnderlying == null ? leaf : Enum.ToObject(Type, leaf);
        }

        var box = Activator.CreateInstance(Type);
        for (var i = 0; i < Fields.Length; i++)
            Fields[i].SetValue(box, Children[i].Materialise(leaves, ref next));

        return box;
    }

    // Reads the leaves back OUT of a value rather than trusting the ones we generated. A struct with
    // overlapping [FieldOffset]s - which is what a mis-recovered layout looks like - does not hold what
    // was written into it, and the method sees what the struct actually holds, so that is what the
    // signature has to cover.
    public void Absorb(object value, SignatureHash hash)
    {
        if (IsLeaf)
        {
            // Dezbracat inapoi la intregul de dedesubt: un enum in cutie nu se poate despacheta direct
            // in int - conversia arunca - iar Bits.Of lucreaza pe bitii primitivei. Conversia se face
            // catre TIPUL LUI de baza, nu catre long, ca sa nu dea peste cap un enum pe UInt64.
            hash.AbsorbLeaf(Kind, EnumUnderlying == null || value == null
                ? value
                : Convert.ChangeType(value, EnumUnderlying, CultureInfo.InvariantCulture));
            return;
        }

        if (value == null)
        {
            hash.AbsorbTag(SignatureHash.TagNull);
            return;
        }

        for (var i = 0; i < Fields.Length; i++)
            Children[i].Absorb(Fields[i].GetValue(value), hash);
    }
}
