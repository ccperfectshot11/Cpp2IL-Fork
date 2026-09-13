using System;
using System.Reflection;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ProcessingLayers;

/// <summary>
/// Widens every type, method and field to public.
///
/// il2cpp inlines aggressively, and an inlined callee leaves the game's own code calling members it could
/// never have named in the original source: <c>Transform.get_position</c> is gone and what is left is a
/// direct call to the private <c>get_position_Injected</c>. The decompiled C# reproduces that call
/// faithfully and it cannot compile - the member is real, it is just not accessible from another assembly,
/// which Roslyn reports as CS1061 ("no accessible extension method") rather than CS0122. The same happens
/// at type level with internal helpers like <c>ThrowHelper</c> and <c>SpanHelpers</c>.
///
/// The inlining cannot be undone, so the only way the call can compile is for the callee to be reachable.
/// Widening is safe against the "cannot change access modifiers when overriding" trap only because every
/// assembly in the output is widened together: a base member and its override move to public in step.
///
/// Explicit interface implementations are left alone. They are private by construction, their name carries
/// the interface it implements, and making one public turns a legal member into a name C# cannot express.
/// </summary>
public class PublicizerProcessingLayer : Cpp2IlProcessingLayer
{
    public override string Name => "Publicizer";

    public override string Id => "publicizer";

    public override void Process(ApplicationAnalysisContext appContext, Action<int, int>? progressCallback = null)
    {
        var done = 0;

        foreach (var assembly in appContext.Assemblies)
        {
            foreach (var type in assembly.Types)
            {
                // Tipurile pe care compilatorul le tese singur in assembly raman cum sunt. Roslyn le
                // verifica forma cand le intalneste, iar prima cerinta din lista este sa fie INTERNAL:
                // "The type 'Microsoft.CodeAnalysis.EmbeddedAttribute' must be non-generic, internal,
                // non-file, sealed, non-static, have a parameterless constructor, inherit from
                // System.Attribute" (CS9271). Largite la public, ele opresc compilarea inaintea oricarui
                // corp de metoda - masurat, o singura eroare de felul asta a ascuns tot restul codului.
                // Nu au ce cauta oricum in largire: nimeni nu le cheama din alt assembly, sunt marcaje
                // pentru compilator.
                if (IsCompilerEmbedded(type))
                    continue;

                type.OverrideAttributes = Publicize(type.Attributes);

                foreach (var method in type.Methods)
                {
                    // An explicit implementation is named "Namespace.IInterface.Member" and has to stay private.
                    if (method.Name.Contains(".") && method.Name is not (".ctor" or ".cctor"))
                        continue;

                    method.OverrideAttributes = (method.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public;
                }

                foreach (var field in type.Fields)
                    field.OverrideAttributes = (field.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Public;
            }

            progressCallback?.Invoke(++done, appContext.Assemblies.Count);
        }
    }

    // Numele sunt fixate de compilator, nu de noi, deci se pot compara literal. Lista e scurta si inchisa
    // intentionat: sunt exact tipurile pe care Roslyn le recunoaste dupa nume complet si le verifica forma.
    private static bool IsCompilerEmbedded(TypeAnalysisContext type) => type.Namespace switch
    {
        "Microsoft.CodeAnalysis" => type.Name is "EmbeddedAttribute",
        "System.Runtime.CompilerServices" => type.Name is "IsReadOnlyAttribute" or "IsUnmanagedAttribute"
            or "IsByRefLikeAttribute" or "NullableAttribute" or "NullableContextAttribute"
            or "NullablePublicOnlyAttribute" or "RefSafetyRulesAttribute" or "ScopedRefAttribute"
            or "RequiresLocationAttribute" or "NativeIntegerAttribute",
        _ => false,
    };

    // Visibility for a nested type is a different set of flags to a top-level one, and using the wrong one
    // produces metadata that is not just wrong but unloadable.
    private static TypeAttributes Publicize(TypeAttributes attributes)
    {
        var nested = (attributes & TypeAttributes.VisibilityMask) >= TypeAttributes.NestedPublic;

        return (attributes & ~TypeAttributes.VisibilityMask)
               | (nested ? TypeAttributes.NestedPublic : TypeAttributes.Public);
    }
}
