using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Redirects a constant stored at the exact start of a value-type field to the primitive that actually
/// sits there.
///
/// The offset of a struct field and of the first thing inside it are the same number, so
/// <c>mov [buffer+8], 0x3f800000</c> over a <c>Vector2 max</c> resolves to <c>max</c> - and the
/// generator then emits stfld of an int into a struct field, which is the wrong value and does not
/// verify. The store really wrote <c>max.x</c>. All fifteen Rewired.UI.UIAnchor getters build their
/// result this way, writing the four floats of the returned struct one at a time through the hidden
/// return buffer.
///
/// Runs after constant folding, because the value is usually a register that was zeroed with
/// <c>xor eax, eax</c> and is only a constant once that has been folded - at field-resolution time it
/// is still a local.
/// </summary>
public static class ScalarFieldStore
{
    // On by default; CPP2IL_SCALAR_FIELD=0 disables.
    private static readonly bool Enabled = System.Environment.GetEnvironmentVariable("CPP2IL_SCALAR_FIELD") != "0";

    private const int MaxNestingDepth = 8;

    public static void Run(MethodAnalysisContext method)
    {
        if (!Enabled)
            return;

        foreach (var instruction in method.ControlFlowGraph!.Blocks.SelectMany(block => block.Instructions))
        {
            if (instruction.OpCode != OpCode.Move
                || instruction.Operands is not [FieldReference { InnerPath.Length: 0 } field, { } source])
                continue;

            FieldAnalysisContext[]? path = source switch
            {
                Immediate constant => LeadingPrimitivePath(field.Field, constant),

                // A register holding a primitive wrote exactly that primitive's width, so the same descent
                // applies with the type in place of the width check. Without it the store keeps the struct
                // as its destination and the coercion that has to follow builds a whole new struct around
                // the value, which zeroes the fields after the one the instruction wrote.
                LocalVariable { Type: { } sourceType } when ScalarWidth(sourceType) != null
                    => LeadingPrimitivePath(field.Field, sourceType.FullName),

                _ => null,
            };

            // A new reference rather than a write into this one: copy propagation can hand the same
            // operand object to more than one instruction, and only this store is being redirected.
            if (path is { Length: > 0 })
                instruction.SetOperand(0, new FieldReference(field.Field, field.Local, field.Offset, path));
        }
    }

    /// <summary>
    /// The chain of leading fields from <paramref name="field"/> down to the primitive at its start, or
    /// null when the field is already a primitive, when nothing sits exactly at offset 0, or when
    /// <paramref name="constant"/> is too wide to fit in that primitive.
    /// </summary>
    /// <remarks>
    /// The width check is what keeps this honest. ISIL does not record how many bytes the store wrote,
    /// so a constant that fits the leading primitive is the same store either way: if the instruction
    /// was narrow it wrote exactly that primitive, and if it was wide the bytes above it were zeros,
    /// which is what a freshly zeroed return buffer already holds. A constant that does not fit was
    /// certainly a wide store carrying a second field's value, and splitting that is not this pass's
    /// business - it is left exactly as it was.
    /// </remarks>
    private static FieldAnalysisContext[]? LeadingPrimitivePath(FieldAnalysisContext field, Immediate constant)
        => LeadingPrimitivePath(field, primitive => ScalarWidth(primitive) is { } width
            && (width >= 8 || constant.UnsignedValue >> (width * 8) == 0));

    /// <summary>
    /// The same, for a value whose type is already known: the descent stops at a primitive only where it is
    /// the one being written, so no conversion is invented on the way.
    /// </summary>
    private static FieldAnalysisContext[]? LeadingPrimitivePath(FieldAnalysisContext field, string sourceType)
        => LeadingPrimitivePath(field, primitive => primitive?.FullName == sourceType);

    private static FieldAnalysisContext[]? LeadingPrimitivePath(FieldAnalysisContext field, Func<TypeAnalysisContext?, bool> accepts)
    {
        var current = field.FieldType;
        var path = new List<FieldAnalysisContext>();

        for (var depth = 0; depth < MaxNestingDepth; depth++)
        {
            if (ScalarWidth(current) is not null)
                return path.Count > 0 && accepts(current) ? path.ToArray() : null;

            if (current is not { IsValueType: true } || current.IsEnumType)
                return null;

            if (FieldAtStart(current) is not { } first)
                return null;

            path.Add(first);
            current = first.FieldType;
        }

        return null;
    }

    private static FieldAnalysisContext? FieldAtStart(TypeAnalysisContext type)
    {
        for (var candidate = type; candidate != null; candidate = candidate.BaseType)
            foreach (var field in candidate.Fields)
            {
                // A const has no storage, but its metadata offset is 0, which would match.
                if (field.IsStatic || (field.Attributes & FieldAttributes.Literal) != 0)
                    continue;

                if (field.Offset == 0)
                    return field;
            }

        return null;
    }

    // Compared by name for the same reason FloatLiteralRecovery does: a field on a generic type resolves
    // to its own Single/Double context rather than the canonical one in SystemTypes.
    private static int? ScalarWidth(TypeAnalysisContext? type) => type?.FullName switch
    {
        "System.Boolean" or "System.SByte" or "System.Byte" => 1,
        "System.Char" or "System.Int16" or "System.UInt16" => 2,
        "System.Int32" or "System.UInt32" or "System.Single" => 4,
        "System.Int64" or "System.UInt64" or "System.Double" or "System.IntPtr" or "System.UIntPtr" => 8,
        _ => null,
    };
}
