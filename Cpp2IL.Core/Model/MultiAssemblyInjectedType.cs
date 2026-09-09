using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Model;

// keyAssemblies drives the shared-assembly mode: the type is injected once, into a single assembly, but
// callers still look members up by the assembly they are decorating. Mapping every one of them onto that
// single member leaves their code untouched while the output carries one definition instead of 150, so
// referencing several output assemblies together no longer trips CS0433 on every injected name.
public class MultiAssemblyInjectedType(InjectedTypeAnalysisContext[] injectedTypes, AssemblyAnalysisContext[]? keyAssemblies = null)
{
    public InjectedTypeAnalysisContext[] InjectedTypes { get; } = injectedTypes;

    private Dictionary<AssemblyAnalysisContext, T> KeyedByEveryAssembly<T>(T member) where T : notnull
        => keyAssemblies!.ToDictionary(a => a, _ => member);

    public Dictionary<AssemblyAnalysisContext, InjectedMethodAnalysisContext> InjectMethodToAllAssemblies(string name, TypeAnalysisContext returnType, MethodAttributes attributes, params ReadOnlySpan<TypeAnalysisContext> args)
    {
        if (keyAssemblies != null)
            return KeyedByEveryAssembly(InjectedTypes[0].InjectMethodContext(name, returnType, attributes, args));

        var dictionary = new Dictionary<AssemblyAnalysisContext, InjectedMethodAnalysisContext>();
        foreach (var type in InjectedTypes)
        {
            dictionary[type.DeclaringAssembly] = type.InjectMethodContext(name, returnType, attributes, args);
        }
        return dictionary;
    }

    public Dictionary<AssemblyAnalysisContext, InjectedMethodAnalysisContext> InjectConstructor(bool isStatic, params ReadOnlySpan<TypeAnalysisContext> args)
    {
        var attributes = isStatic
            ? MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.HideBySig | MethodAttributes.Static
            : MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.HideBySig;
        return InjectMethodToAllAssemblies(isStatic ? ".cctor" : ".ctor", InjectedTypes.First().AppContext.SystemTypes.SystemVoidType, attributes, args);
    }

    public Dictionary<AssemblyAnalysisContext, InjectedFieldAnalysisContext> InjectFieldToAllAssemblies(string name, TypeAnalysisContext fieldType, FieldAttributes attributes)
        => keyAssemblies != null
            ? KeyedByEveryAssembly(InjectedTypes[0].InjectFieldContext(name, fieldType, attributes))
            : InjectedTypes.ToDictionary(t => t.DeclaringAssembly, t => t.InjectFieldContext(name, fieldType, attributes));

    public Dictionary<AssemblyAnalysisContext, InjectedPropertyAnalysisContext> InjectPropertyToAllAssemblies(string name, TypeAnalysisContext propertyType, Dictionary<AssemblyAnalysisContext, InjectedMethodAnalysisContext>? getter, Dictionary<AssemblyAnalysisContext, InjectedMethodAnalysisContext>? setter, PropertyAttributes attributes)
        => InjectedTypes.ToDictionary(t => t.DeclaringAssembly, t => t.InjectPropertyContext(name, propertyType, getter?[t.DeclaringAssembly], setter?[t.DeclaringAssembly], attributes));

    public Dictionary<AssemblyAnalysisContext, InjectedEventAnalysisContext> InjectEventToAllAssemblies(string name, TypeAnalysisContext eventType, Dictionary<AssemblyAnalysisContext, InjectedMethodAnalysisContext>? adder, Dictionary<AssemblyAnalysisContext, InjectedMethodAnalysisContext>? remover, Dictionary<AssemblyAnalysisContext, InjectedMethodAnalysisContext>? invoker, EventAttributes attributes)
        => InjectedTypes.ToDictionary(t => t.DeclaringAssembly, t => t.InjectEventContext(name, eventType, adder?[t.DeclaringAssembly], remover?[t.DeclaringAssembly], invoker?[t.DeclaringAssembly], attributes));

    public MultiAssemblyInjectedType InjectNestedType(string name, TypeAnalysisContext? baseType, TypeAttributes attributes = TypeAttributes.NestedPublic | TypeAttributes.Sealed)
        => new(InjectedTypes.Select(t => t.InjectNestedType(name, baseType, attributes)).ToArray());
}
