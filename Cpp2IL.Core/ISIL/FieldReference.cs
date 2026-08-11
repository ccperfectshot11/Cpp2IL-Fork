using System.Linq;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ISIL;

/// <summary>
/// A field access on <see cref="Local"/>. <see cref="InnerPath"/> is non-empty when the offset landed
/// inside a value-type field, i.e. the source was a nested access such as <c>a.b.c</c>: <see cref="Field"/>
/// is then the outermost field and the path holds the remaining ones, in order.
/// </summary>
public class FieldReference(FieldAnalysisContext field, LocalVariable local, int offset, FieldAnalysisContext[]? innerPath = null) : IOperand
{
    public FieldAnalysisContext Field = field;
    public LocalVariable Local = local;
    public int Offset = offset;
    public FieldAnalysisContext[] InnerPath = innerPath ?? [];

    /// <summary>
    /// The type actually produced by the access, which for a nested access is the innermost field's.
    /// </summary>
    public TypeAnalysisContext ResultType => InnerPath.Length > 0 ? InnerPath[^1].FieldType : Field.FieldType;

    public override string ToString() => InnerPath.Length == 0
        ? $"{Local.Name}.{Field.Name} ({Field.FieldType.FullName})"
        : $"{Local.Name}.{Field.Name}.{string.Join(".", InnerPath.Select(f => f.Name))} ({ResultType.FullName})";
}
