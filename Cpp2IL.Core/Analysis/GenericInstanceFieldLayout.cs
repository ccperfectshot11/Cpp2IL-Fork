using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

//Resolves field offsets on generic types, which are all 0 in the metadata.
public static class GenericInstanceFieldLayout
{
    public static FieldAnalysisContext? FindFieldAtOffset(TypeAnalysisContext definition, long targetOffset)
        => FindFieldAtOffset(definition, targetOffset, null);

    /// <summary>
    /// <paramref name="genericArguments"/> sunt argumentele instantei careia i se calculeaza asezarea.
    /// Cand sunt date, un camp de tipul unui parametru generic este masurat dupa argumentul ADEVARAT, nu
    /// presupus de marimea unui pointer. Cand lipsesc - tipul e deschis - se pastreaza presupunerea veche.
    /// </summary>
    public static FieldAnalysisContext? FindFieldAtOffset(TypeAnalysisContext definition, long targetOffset, IReadOnlyList<TypeAnalysisContext>? genericArguments)
    {
        var pointerSize = definition.AppContext.Binary.PointerSizeBytes;

        // TODO Support anything outside the trivial case.
        for (var baseType = definition.BaseType; baseType != null; baseType = baseType.BaseType)
            if (baseType.Fields.Any(f => !f.IsStatic))
                return null;

        var offset = 2L * pointerSize;

        foreach (var field in definition.Fields)
        {
            if (field.IsStatic)
                continue;

            if (GetSizeAndAlignment(Substitute(field.FieldType, genericArguments), pointerSize) is not var (size, alignment))
                return null;

            offset = (offset + alignment - 1) & ~(alignment - 1);

            if (offset == targetOffset)
                return field;

            offset += size;
        }

        return null;
    }

    /// <summary>
    /// Inlocuieste un camp de tipul `T` cu argumentul adevarat. Numai parametrul gol se inlocuieste: un
    /// `T[]` este oricum o referinta, deci are marimea unui pointer indiferent de argument, iar o instanta
    /// generica imbricata care il pomeneste pe T ramane neatinsa si cade singura in bail-ul de mai jos,
    /// fiindca nu e in tabelul de marimi cunoscute. Asa nimic nu se ghiceste.
    /// </summary>
    private static TypeAnalysisContext Substitute(TypeAnalysisContext fieldType, IReadOnlyList<TypeAnalysisContext>? genericArguments)
        => genericArguments != null
            && fieldType is GenericParameterTypeAnalysisContext { Index: var index }
            && index >= 0 && index < genericArguments.Count
            ? genericArguments[index]
            : fieldType;

    private static (long Size, long Alignment)? GetSizeAndAlignment(TypeAnalysisContext fieldType, int pointerSize)
    {
        // TODO support user-defined value types
        if (fieldType is GenericParameterTypeAnalysisContext or PointerTypeAnalysisContext || !fieldType.IsValueType)
            return (pointerSize, pointerSize);

        if (fieldType.IsEnumType && fieldType.Fields.FirstOrDefault(f => !f.IsStatic) is { } underlying)
            return GetSizeAndAlignment(underlying.FieldType, pointerSize);

        return fieldType.FullName switch
        {
            "System.Boolean" or "System.Byte" or "System.SByte" => (1, 1),
            "System.Int16" or "System.UInt16" or "System.Char" => (2, 2),
            "System.Int32" or "System.UInt32" or "System.Single" => (4, 4),
            "System.Int64" or "System.UInt64" or "System.Double" => (8, 8),
            "System.IntPtr" or "System.UIntPtr" => (pointerSize, pointerSize),
            _ => null // an arbitrary struct needs its own layout computed, bail rather than guess
        };
    }
}
