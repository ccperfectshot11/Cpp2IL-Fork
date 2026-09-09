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
/// </summary>
public class IdentifierSanitizerProcessingLayer : Cpp2IlProcessingLayer
{
    public override string Name => "Identifier Sanitizer";

    public override string Id => "sanitizenames";

    public override void Process(ApplicationAnalysisContext appContext, Action<int, int>? progressCallback = null)
    {
        var done = 0;

        foreach (var assembly in appContext.Assemblies)
        {
            foreach (var type in assembly.Types)
            {
                if (type.Name != "<Module>" && Sanitize(type.Name) is { } typeName)
                    type.OverrideName = typeName;

                // Collisions are only possible against the other members of the same type, and only when a
                // real member already carries the sanitised spelling. Rare, but a duplicate name is worse
                // than an ugly one, so the existing names are held and any clash gets a suffix.
                var taken = new HashSet<string>(StringComparer.Ordinal);

                foreach (var method in type.Methods)
                    taken.Add(method.Name);
                foreach (var field in type.Fields)
                    taken.Add(field.Name);

                foreach (var method in type.Methods)
                {
                    if (method.Name is ".ctor" or ".cctor")
                        continue;

                    if (Sanitize(method.Name) is { } methodName)
                        method.OverrideName = Unique(methodName, taken);
                }

                foreach (var field in type.Fields)
                {
                    // A property backing field keeps its name so the decompiler can still collapse the pair
                    // into `{ get; set; }`, which is what the original source said. Cross-type reads of one
                    // are routed through the property instead - see IlGenerator.TryEmitBackingFieldRead.
                    if (field.Name.EndsWith(">k__BackingField", StringComparison.Ordinal))
                        continue;

                    if (Sanitize(field.Name) is { } fieldName)
                        field.OverrideName = Unique(fieldName, taken);
                }
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
