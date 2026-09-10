using System;
using System.Collections.Generic;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ProcessingLayers;

/// <summary>
/// Renames every member whose metadata name contains a character C# cannot write, so that the name in the
/// output is the same name the decompiler produces at every use of it.
///
/// The C# compiler never emits these names from source: <c>&lt;Game&gt;k__BackingField</c>,
/// <c>&lt;&gt;c__DisplayClass14_0</c>, <c>&lt;Start&gt;b__0</c>. A decompiler has to sanitise them to print
/// anything at all, and it writes them with the angle brackets replaced by underscores. Inside one file
/// that is consistent, because the declaration is sanitised too - but the declaration usually lives in
/// another assembly, which is still holding the original name, and then the use resolves against nothing:
/// "'QuantumRunner' does not contain a definition for '_Game_k__BackingField'". It is the single largest
/// remaining CS1061 shape, and the same problem produces CS0426 for display-class types.
///
/// Renaming in the metadata makes both sides agree, using exactly the substitution the decompiler applies
/// so the printed name is unchanged and only the declaration moves to meet it.
///
/// <c>.ctor</c>, <c>.cctor</c> and <c>&lt;Module&gt;</c> are left alone: those names are structural, the
/// runtime looks them up by name, and the emitter already handles them.
///
/// MEASURED HARMFUL - do not put this in the pipeline. The decompiler recognises compiler-generated
/// constructs by exactly these names: <c>&lt;&gt;c__DisplayClass</c> is how it knows a closure, and
/// <c>&lt;X&gt;b__0</c> how it knows the lambda inside it. Rename them and it stops recognising them, so
/// the lambdas stop being folded back into their parent method and are emitted as standalone methods on a
/// visible display class. That is the opposite of the goal: the output moves further from the original
/// source, not closer. It shows up as the method count going 16,676 -> 18,390 on the two Assembly-CSharp
/// DLLs, which also makes every percentage measured with this layer incomparable to one measured without.
///
/// The problem it was written for - a cross-assembly read of <c>&lt;X&gt;k__BackingField</c> not resolving
/// against the sanitised spelling the decompiler prints - is solved properly in
/// <c>IlGenerator.TryEmitBackingFieldRead</c>, which routes the read through the property instead and
/// leaves every generated name intact. Kept only so the experiment can be repeated.
/// </summary>
public class IdentifierSanitizerProcessingLayer : Cpp2IlProcessingLayer
{
    public override string Name => "Identifier Sanitizer";

    public override string Id => "sanitizenames";

    // Only fields are renamed, and only because their declaration is being LOST. The decompiler hides
    // `<X>k__BackingField` unconditionally, expecting to collapse the field and its two accessors back into
    // `{ get; set; }` - but that collapse is driven by the accessor body's shape and never fires on a
    // Cpp2IL body, which carries dead locals and unrecovered stores. So the property is written with
    // explicit bodies, the field declaration is suppressed, and every use of it is printed with the
    // sanitised spelling `_X_k__BackingField`, which now refers to nothing: 4,431 references, zero
    // declarations, 2,886 methods of CS1061 - by far the largest single cause left.
    //
    // Renaming the field makes the decompiler stop hiding it, so the declaration appears and matches the
    // uses. Nothing is lost, because the collapse this defeats was never happening.
    //
    // Types and methods are deliberately NOT renamed - see the class comment.
    private static readonly bool BackingFields = Environment.GetEnvironmentVariable("CPP2IL_SANITIZE_FIELDS") != "0";

    public override void Process(ApplicationAnalysisContext appContext, Action<int, int>? progressCallback = null)
    {
        var done = 0;

        foreach (var assembly in appContext.Assemblies)
        {
            foreach (var type in assembly.Types)
            {
                var taken = new HashSet<string>(StringComparer.Ordinal);

                foreach (var method in type.Methods)
                    taken.Add(method.Name);
                foreach (var field in type.Fields)
                    taken.Add(field.Name);

                foreach (var field in type.Fields)
                    if (BackingFields && Sanitize(field.Name) is { } fieldName)
                        field.OverrideName = Unique(fieldName, taken);
            }

            progressCallback?.Invoke(++done, appContext.Assemblies.Count);
        }
    }

    // Null when there is nothing to change, so a name that is already writable keeps its own instance and
    // no override is recorded for it.
    private static string? Sanitize(string name)
    {
        if (name.IndexOf('<') < 0 && name.IndexOf('>') < 0)
            return null;

        return name.Replace('<', '_').Replace('>', '_');
    }

    private static string Unique(string name, HashSet<string> taken)
    {
        var candidate = name;

        for (var suffix = 2; !taken.Add(candidate); suffix++)
            candidate = name + "_" + suffix;

        return candidate;
    }
}
