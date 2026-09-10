using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AssetRipper.CIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Model.CustomAttributes;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Utils.AsmResolver;

public static class AsmResolverAssemblyPopulator
{
    /// <summary>
    /// il2cpp inlines an event's add and remove accessors into whoever calls them, so the recovered code
    /// writes the event's backing field directly - and that field carries the event's own name. A field
    /// named after an event is the event's storage, so the decompiler hides it and prints every access to
    /// it as the event itself: <c>this.OnEntityInstantiated = delegate4</c>. Only += and -= may name an
    /// event, which is 3,114 CS0079 over 725 methods, a fifth of them writing the field on another type.
    ///
    /// Renaming the field is what makes those accesses printable again, and the event keeps its own name,
    /// so every += and -= still reads the way the original source wrote it. The rename costs nothing: the
    /// decompiler folds an event back into <c>event T X;</c> only when it recognises the accessor bodies as
    /// the Interlocked.CompareExchange loop the C# compiler emits, and it recognises none of the 214 events
    /// recovered here - the field was never going to disappear into a field-like declaration anyway.
    /// </summary>
    private static readonly bool RenameEventBackingFields = Environment.GetEnvironmentVariable("CPP2IL_EVENT_FIELD") != "0";

    /// <summary>
    /// A named argument is the only reason an attribute property still has a setter: il2cpp emits the code
    /// that constructs the attribute and calls the setter once per named argument, while nothing in a player
    /// build ever reads the value back, so the getter is stripped. What is left is a write-only property,
    /// and C# takes only a read-write one as a named argument - CS0617 on every type carrying the attribute,
    /// 245 methods of it from <c>[CreateAssetMenu(menuName = ...)]</c> alone.
    ///
    /// The getter goes back rather than the argument coming out, because the original source cannot have
    /// said anything else: a name written on the left of = inside an attribute was a read-write property
    /// there, so the assembly declared one and stripping is the only thing that happened to it since.
    /// </summary>
    private static readonly bool RestoreStrippedAttributeGetters = Environment.GetEnvironmentVariable("CPP2IL_ATTR_GETTER") != "0";

    public static bool IsTypeContextModule(TypeAnalysisContext typeCtx)
    {
        return typeCtx.Name.StartsWith("<Module>") || typeCtx.FullName.StartsWith("<Module>");
    }

    public static void ConfigureHierarchy(AssemblyAnalysisContext asmCtx)
    {
        foreach (var typeCtx in asmCtx.Types)
        {
            if (IsTypeContextModule(typeCtx))
                continue;

            var typeDefinition = typeCtx.GetExtraData<TypeDefinition>("AsmResolverType") ?? throw new($"AsmResolver type not found in type analysis context for {typeCtx.FullName}");

            //Type generic params.
            PopulateGenericParamsForType(typeCtx, typeDefinition);

            //Set base type
            if(asmCtx.AppContext.MetadataVersion >= 35 && typeCtx is {Definition.IsEnumType: true })
                //v35 restructures this a bit so that enums now directly inherit from their primitive type, so we need to explicitly set this to enum
                typeDefinition.BaseType = typeCtx.AppContext.SystemTypes.EnumType.ToTypeSignature().ToTypeDefOrRef();
            else
                typeDefinition.BaseType = typeCtx.BaseType?.ToTypeSignature().ToTypeDefOrRef();

            //Set interfaces
            foreach (var interfaceType in typeCtx.InterfaceContexts)
                typeDefinition.Interfaces.Add(new(interfaceType.ToTypeSignature().ToTypeDefOrRef()));
        }

        var assemblyDefinition = asmCtx.GetExtraData<AssemblyDefinition>("AsmResolverAssembly") ?? throw new("AsmResolver assembly not found in assembly analysis context for " + asmCtx);
        var moduleDefinition = assemblyDefinition.ManifestModule!;
        foreach (var typeCtx in asmCtx.ExportedTypes)
        {
            var owningAssembly = typeCtx.DeclaringAssembly.GetExtraData<AssemblyDefinition>("AsmResolverAssembly") ?? throw new("AsmResolver assembly not found in assembly analysis context for " + typeCtx.DeclaringAssembly);
            moduleDefinition.ExportedTypes.Add(new ExportedType(owningAssembly.ToAssemblyReference(), typeCtx.Namespace, typeCtx.Name));
        }
    }

    private static void PopulateGenericParamsForType(TypeAnalysisContext cppTypeDefinition, TypeDefinition ilTypeDefinition)
    {
        foreach (var param in cppTypeDefinition.GenericParameters)
        {
            var p = new GenericParameter(param.Name, (GenericParameterAttributes)param.Attributes);

            ilTypeDefinition.GenericParameters.Add(p);

            param.ConstraintTypes
                .Select(c => new GenericParameterConstraint(c.ToTypeSignature().ToTypeDefOrRef()))
                .ToList()
                .ForEach(p.Constraints.Add);
        }
    }

    private static TypeSignature GetTypeSigFromAttributeArg(BaseCustomAttributeParameter parameter) =>
        parameter switch
        {
            CustomAttributePrimitiveParameter primitiveParameter => AsmResolverUtils.GetPrimitiveTypeDef(primitiveParameter.PrimitiveType).ToTypeSignature(),
            CustomAttributeEnumParameter enumParameter => enumParameter.EnumTypeContext.ToTypeSignature(),
            BaseCustomAttributeTypeParameter => TypeDefinitionsAsmResolver.Type.ToTypeSignature(),
            CustomAttributeArrayParameter arrayParameter => AsmResolverUtils.GetPrimitiveTypeDef(arrayParameter.ArrType).ToTypeSignature().MakeSzArrayType(),
            _ => throw new ArgumentException("Unknown custom attribute parameter type: " + parameter.GetType().FullName)
        };

    private static CustomAttributeArgument BuildArrayArgument(CustomAttributeArrayParameter arrayParameter)
    {
#if !DEBUG
        try
#endif
        {
            if (arrayParameter.IsNullArray)
                return BuildEmptyArrayArgument(arrayParameter);

            var typeSig = GetTypeSigFromAttributeArg(arrayParameter);

            var isObjectArray = arrayParameter.ArrType == Il2CppTypeEnum.IL2CPP_TYPE_OBJECT;

            var arrayElements = arrayParameter.ArrayElements.Select(e =>
            {
                var rawValue = e switch
                {
                    CustomAttributePrimitiveParameter primitiveParameter => primitiveParameter.PrimitiveValue,
                    CustomAttributeEnumParameter enumParameter => enumParameter.UnderlyingPrimitiveParameter.PrimitiveValue,
                    BaseCustomAttributeTypeParameter type => (object?)type.TypeContext?.ToTypeSignature(),
                    CustomAttributeNullParameter => null,
                    CustomAttributeArrayParameter array => BuildArrayArgument(array).Elements.ToArray(),
                    _ => throw new("Not supported array element type: " + e.GetType().FullName)
                };

                if (isObjectArray)
                    //Object params have to be boxed
                    return new BoxedArgument(GetTypeSigFromAttributeArg(e), rawValue);

                return rawValue;
            }).ToArray();

            return new(typeSig, arrayElements);
        }
#if !DEBUG
        catch (Exception e)
        {
            throw new("Failed to build array argument for " + arrayParameter, e);
        }
#endif
    }

    private static CustomAttributeArgument BuildEmptyArrayArgument(CustomAttributeArrayParameter arrayParameter)
    {
        //Need to resolve the type of the array because it's not in the blob and AsmResolver needs it.

        var typeSig = arrayParameter.Kind switch
        {
            CustomAttributeParameterKind.ConstructorParam => arrayParameter.Owner.Constructor.Parameters[arrayParameter.Index].ToTypeSignature(),
            CustomAttributeParameterKind.Property => arrayParameter.Owner.Properties[arrayParameter.Index].Property.ToTypeSignature(),
            CustomAttributeParameterKind.Field => arrayParameter.Owner.Fields[arrayParameter.Index].Field.ToTypeSignature(),
            CustomAttributeParameterKind.ArrayElement => throw new("Array element cannot be an array (or at least, not implemented!)"),
            _ => throw new("Unknown array parameter kind: " + arrayParameter.Kind)
        };

        return new(typeSig) { IsNullArray = true };
    }

    /// <summary>
    /// Converts the given parameter to a custom attribute argument, given the context of the parent assembly.
    /// </summary>
    /// <param name="parameter">The parameter to convert</param>
    /// <param name="boxIfNeeded">Whether the returned attribute will be used in context of a member that is typed as object. If true, the resulting attribute will be an object-typed one wrapping a BoxedArgument containing the real value. If false, the real value will be returned directly.</param>
    /// <remarks>
    /// BoxIfNeeded will cause the resulting attribute to be boxed if the parameter is an enum or a type parameter. This is required if, for example, the enum or type is being passed as the argument in a constructor for which the parameter is typed as object.
    /// </remarks>
    private static CustomAttributeArgument FromAnalyzedAttributeArgument(BaseCustomAttributeParameter parameter, bool boxIfNeeded)
    {
#if !DEBUG
        try
#endif
        {
            return parameter switch
            {
                CustomAttributePrimitiveParameter primitiveParameter when boxIfNeeded => new(TypeDefinitionsAsmResolver.Object.ToTypeSignature(), new BoxedArgument(GetTypeSigFromAttributeArg(primitiveParameter), primitiveParameter.PrimitiveValue)),
                CustomAttributePrimitiveParameter primitiveParameter => new(GetTypeSigFromAttributeArg(primitiveParameter), primitiveParameter.PrimitiveValue),
                
                CustomAttributeEnumParameter enumParameter when boxIfNeeded => new(TypeDefinitionsAsmResolver.Object.ToTypeSignature(), new BoxedArgument(GetTypeSigFromAttributeArg(enumParameter), enumParameter.UnderlyingPrimitiveParameter.PrimitiveValue)),
                CustomAttributeEnumParameter enumParameter => new(GetTypeSigFromAttributeArg(enumParameter), enumParameter.UnderlyingPrimitiveParameter.PrimitiveValue),
                
                //BaseCustomAttributeTypeParameter typeParameter when boxIfNeeded => new(TypeDefinitionsAsmResolver.Object.ToTypeSignature(), new BoxedArgument(GetTypeSigFromAttributeArg(parentAssembly, typeParameter), typeParameter.TypeContext?.ToTypeSignature(parentAssembly.ManifestModule!))),
                BaseCustomAttributeTypeParameter typeParameter => new(TypeDefinitionsAsmResolver.Type.ToTypeSignature(), typeParameter.TypeContext?.ToTypeSignature()),
                
                CustomAttributeArrayParameter arrayParameter => BuildArrayArgument(arrayParameter),
                _ => throw new ArgumentException("Unknown custom attribute parameter type: " + parameter.GetType().FullName)
            };
        }
#if !DEBUG
        catch (Exception e)
        {
            throw new("Failed to build custom attribute argument for " + parameter, e);
        }
#endif
    }

    private static CustomAttributeNamedArgument FromAnalyzedAttributeField(CustomAttributeField field)
        => new(CustomAttributeArgumentMemberType.Field, field.Field.Name, GetTypeSigFromAttributeArg(field.Value), FromAnalyzedAttributeArgument(field.Value, field.Field.FieldType == field.Field.AppContext.SystemTypes.SystemObjectType));

    private static CustomAttributeNamedArgument FromAnalyzedAttributeProperty(CustomAttributeProperty property)
        => new(CustomAttributeArgumentMemberType.Property, property.Property.Name, GetTypeSigFromAttributeArg(property.Value), FromAnalyzedAttributeArgument(property.Value, property.Property.PropertyType == property.Property.AppContext.SystemTypes.SystemObjectType));

    private static CustomAttribute? ConvertCustomAttribute(AnalyzedCustomAttribute analyzedCustomAttribute)
    {
        var ctor = analyzedCustomAttribute.Constructor.GetExtraData<MethodDefinition>("AsmResolverMethod") ?? throw new($"Found a custom attribute with no AsmResolver constructor: {analyzedCustomAttribute}");

        CustomAttributeSignature signature;
        var numNamedArgs = analyzedCustomAttribute.Fields.Count + analyzedCustomAttribute.Properties.Count;

#if !DEBUG
        try
#endif
        {
            if (!analyzedCustomAttribute.HasAnyParameters && numNamedArgs == 0)
                signature = new();
            else if (analyzedCustomAttribute.IsSuitableForEmission)
            {
                if (numNamedArgs == 0)
                {
                    //Only fixed arguments.
                    signature = new(analyzedCustomAttribute.ConstructorParameters.Select(p => FromAnalyzedAttributeArgument(p, analyzedCustomAttribute.Constructor.Parameters[p.Index].ParameterType == analyzedCustomAttribute.Constructor.AppContext.SystemTypes.SystemObjectType)));
                }
                else
                {
                    //Has named arguments.
                    signature = new(
                        analyzedCustomAttribute.ConstructorParameters.Select(p => FromAnalyzedAttributeArgument(p, analyzedCustomAttribute.Constructor.Parameters[p.Index].ParameterType == analyzedCustomAttribute.Constructor.AppContext.SystemTypes.SystemObjectType)),
                        analyzedCustomAttribute.Fields
                            .Select(FromAnalyzedAttributeField)
                            .Concat(analyzedCustomAttribute.Properties.Select(FromAnalyzedAttributeProperty))
                    );
                }
            }
            else
            {
                return null;
            }
        }
#if !DEBUG
        catch (Exception e)
        {
            throw new("Failed to build custom attribute signature for " + analyzedCustomAttribute, e);
        }
#endif

        return new CustomAttribute((ICustomAttributeType)ctor, signature);
    }

    private static void CopyCustomAttributes(HasCustomAttributes source, IList<CustomAttribute> destination)
    {
        if (source.CustomAttributes == null)
            return;

#if !DEBUG
        try
#endif
        {
            foreach (var analyzedCustomAttribute in source.CustomAttributes)
            {
                var asmResolverCustomAttribute = ConvertCustomAttribute(analyzedCustomAttribute);
                if (asmResolverCustomAttribute != null)
                    destination.Add(asmResolverCustomAttribute);
            }
        }
#if !DEBUG
        catch (Exception e)
        {
            throw new("Failed to copy custom attributes for " + source, e);
        }
#endif
    }

    public static void PopulateCustomAttributes(AssemblyAnalysisContext asmContext)
    {
#if !DEBUG
        try
#endif
        {
            var assembly = asmContext.GetExtraData<AssemblyDefinition>("AsmResolverAssembly")!;
            CopyCustomAttributes(asmContext, assembly.CustomAttributes);
            CopyCustomAttributes(asmContext.ManifestModule, assembly.ManifestModule!.CustomAttributes);

            foreach (var type in asmContext.Types)
            {
                if (IsTypeContextModule(type))
                    continue;

                CopyCustomAttributes(type, type.GetExtraData<TypeDefinition>("AsmResolverType")!.CustomAttributes);

                foreach (var method in type.Methods)
                {
                    var methodDef = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
                    CopyCustomAttributes(method, methodDef.CustomAttributes);

                    var parameterDefinitions = methodDef.ParameterDefinitions;
                    foreach (var parameterAnalysisContext in method.Parameters)
                    {
                        CopyCustomAttributes(parameterAnalysisContext, parameterDefinitions[parameterAnalysisContext.ParameterIndex].CustomAttributes);
                    }
                }

                foreach (var field in type.Fields)
                    CopyCustomAttributes(field, field.GetExtraData<FieldDefinition>("AsmResolverField")!.CustomAttributes);

                foreach (var property in type.Properties)
                    CopyCustomAttributes(property, property.GetExtraData<PropertyDefinition>("AsmResolverProperty")!.CustomAttributes);

                foreach (var eventDefinition in type.Events)
                    CopyCustomAttributes(eventDefinition, eventDefinition.GetExtraData<EventDefinition>("AsmResolverEvent")!.CustomAttributes);
            }
        }
#if !DEBUG
        catch (Exception e)
        {
            throw new($"Failed to populate custom attributes in {asmContext}", e);
        }
#endif
    }

    public static void CopyDataFromIl2CppToManaged(AssemblyAnalysisContext asmContext)
    {
        var managedAssembly = asmContext.GetExtraData<AssemblyDefinition>("AsmResolverAssembly") ?? throw new("AsmResolver assembly not found in assembly analysis context for " + asmContext);

        foreach (var typeContext in asmContext.Types)
        {
            if (IsTypeContextModule(typeContext))
                continue;

            var managedType = typeContext.GetExtraData<TypeDefinition>("AsmResolverType") ?? throw new($"AsmResolver type not found in type analysis context for {typeContext.Definition?.FullName}");
            // CopyCustomAttributes(typeContext, managedType.CustomAttributes);

#if !DEBUG
            try
#endif
            {
                CopyIl2CppDataToManagedType(typeContext, managedType);
            }
#if !DEBUG
            catch (Exception e)
            {
                throw new Exception($"Failed to process type {managedType.FullName} (module {managedType.DeclaringModule?.Name}, declaring type {managedType.DeclaringType?.FullName}) in {asmContext.Name}", e);
            }
#endif
        }
    }

    private static void CopyIl2CppDataToManagedType(TypeAnalysisContext typeContext, TypeDefinition ilTypeDefinition)
    {
        CopyFieldsInType(typeContext, ilTypeDefinition);

        CopyMethodsInType(typeContext, ilTypeDefinition);

        CopyPropertiesInType(typeContext, ilTypeDefinition);

        CopyEventsInType(typeContext, ilTypeDefinition);
    }

    private static void CopyFieldsInType(TypeAnalysisContext typeContext, TypeDefinition ilTypeDefinition)
    {
        foreach (var fieldContext in typeContext.Fields)
        {
            var fieldTypeSig = fieldContext.ToTypeSignature();

            var managedField = new FieldDefinition(fieldContext.Name, (FieldAttributes)fieldContext.Attributes, fieldTypeSig);

            //Field default values
            if (managedField.HasDefault)
                managedField.Constant = AsmResolverConstants.GetOrCreateConstant(fieldContext.ConstantValue);

            //Field Initial Values (used for allocation of Array Literals)
            if (managedField.HasFieldRva)
                managedField.FieldRva = new DataSegment(fieldContext.StaticArrayInitialValue);

            //Copy field offset
            if (ilTypeDefinition.IsExplicitLayout && !fieldContext.IsStatic)
                managedField.FieldOffset = fieldContext.Offset;

            fieldContext.PutExtraData("AsmResolverField", managedField);

            ilTypeDefinition.Fields.Add(managedField);
        }
    }

    private static void CopyMethodsInType(TypeAnalysisContext typeContext, TypeDefinition ilTypeDefinition)
    {
        foreach (var methodCtx in typeContext.Methods)
        {
            var returnType = methodCtx.ReturnType.ToTypeSignature();

            var paramData = methodCtx.Parameters;
            var parameterTypes = new TypeSignature[paramData.Count];
            var parameterDefinitions = new ParameterDefinition[paramData.Count];
            foreach (var parameterAnalysisContext in methodCtx.Parameters)
            {
                var i = parameterAnalysisContext.ParameterIndex;
                parameterTypes[i] = parameterAnalysisContext.ParameterType.ToTypeSignature();

                var sequence = (ushort)(i + 1); //Add one because sequence 0 is the return type
                parameterDefinitions[i] = new(sequence, parameterAnalysisContext.Name, (ParameterAttributes)parameterAnalysisContext.Attributes);

                if (parameterAnalysisContext.Attributes.HasFlag(System.Reflection.ParameterAttributes.HasDefault))
                    parameterDefinitions[i].Constant = AsmResolverConstants.GetOrCreateConstant(parameterAnalysisContext.DefaultValue);
            }


            var signature = methodCtx.IsStatic
                ? MethodSignature.CreateStatic(returnType, methodCtx.GenericParameters.Count, parameterTypes)
                : MethodSignature.CreateInstance(returnType, methodCtx.GenericParameters.Count, parameterTypes);

            var managedMethod = new MethodDefinition(methodCtx.Name, (MethodAttributes)methodCtx.Attributes, signature);

            managedMethod.ImplAttributes = (MethodImplAttributes)methodCtx.ImplAttributes;

            if (methodCtx.Definition != null)
            {
                if (methodCtx.Definition.IsUnmanagedCallersOnly && typeContext.AppContext.SystemTypes.UnmanagedCallersOnlyAttributeType != null)
                {
                    var unmanagedCallersOnlyType = typeContext.AppContext.SystemTypes.UnmanagedCallersOnlyAttributeType.GetExtraData<TypeDefinition>("AsmResolverType");
                    if(unmanagedCallersOnlyType != null)
                        managedMethod.CustomAttributes.Add(new CustomAttribute((ICustomAttributeType)unmanagedCallersOnlyType.GetConstructor()!, new()));
                }

            }

            //Add parameter definitions if we have them so we get names, defaults, out params, etc
            foreach (var parameterDefinition in parameterDefinitions)
            {
                managedMethod.ParameterDefinitions.Add(parameterDefinition);
            }

            //Handle generic parameters.
            methodCtx.GenericParameters
                .ForEach(p =>
                {
                    var gp = new GenericParameter(p.Name, (GenericParameterAttributes)p.Attributes);

                    if (!managedMethod.GenericParameters.Contains(gp))
                        managedMethod.GenericParameters.Add(gp);

                    p.ConstraintTypes
                        .Select(c => new GenericParameterConstraint(c.ToTypeSignature().ToTypeDefOrRef()))
                        .ToList()
                        .ForEach(gp.Constraints.Add);
                });


            methodCtx.PutExtraData("AsmResolverMethod", managedMethod);
            ilTypeDefinition.Methods.Add(managedMethod);
        }
    }

    private static void CopyPropertiesInType(TypeAnalysisContext typeContext, TypeDefinition ilTypeDefinition)
    {
        foreach (var propertyCtx in typeContext.Properties)
        {
            var propertyTypeSig = propertyCtx.ToTypeSignature();
            var propertySignature = propertyCtx.IsStatic
                ? PropertySignature.CreateStatic(propertyTypeSig)
                : PropertySignature.CreateInstance(propertyTypeSig);

            var managedProperty = new PropertyDefinition(propertyCtx.Name, (PropertyAttributes)propertyCtx.Attributes, propertySignature);

            var managedGetter = propertyCtx.Getter?.GetExtraData<MethodDefinition>("AsmResolverMethod");
            var managedSetter = propertyCtx.Setter?.GetExtraData<MethodDefinition>("AsmResolverMethod");

            if (managedGetter == null && managedSetter != null)
                managedGetter = RestoreStrippedGetter(propertyCtx, typeContext, ilTypeDefinition, managedSetter, propertyTypeSig);

            managedProperty.SetSemanticMethods(managedGetter, managedSetter);

            //Indexer parameters
            if (managedGetter != null && managedGetter.Parameters.Count > 0)
            {
                foreach (var parameter in managedGetter.Parameters)
                {
                    propertySignature.ParameterTypes.Add(parameter.ParameterType);
                }
            }
            else if (managedSetter != null && managedSetter.Parameters.Count > 1)
            {
                //value parameter is always last
                for (var i = 0; i < managedSetter.Parameters.Count - 1; i++)
                {
                    var parameter = managedSetter.Parameters[i];
                    propertySignature.ParameterTypes.Add(parameter.ParameterType);
                }
            }

            propertyCtx.PutExtraData("AsmResolverProperty", managedProperty);

            ilTypeDefinition.Properties.Add(managedProperty);
        }
    }

    private static MethodDefinition? RestoreStrippedGetter(PropertyAnalysisContext propertyCtx, TypeAnalysisContext typeContext, TypeDefinition ilTypeDefinition, MethodDefinition managedSetter, TypeSignature propertyTypeSig)
    {
        // Only on an attribute, where a write-only property has no other explanation. Anywhere else one is
        // a member the author meant to be write-only, and giving it a getter invents API that never existed.
        if (!RestoreStrippedAttributeGetters || managedSetter.IsAbstract || !InheritsAttribute(typeContext))
            return null;

        var signature = propertyCtx.IsStatic
            ? MethodSignature.CreateStatic(propertyTypeSig)
            : MethodSignature.CreateInstance(propertyTypeSig);

        // An indexer's getter takes the index parameters, which are the setter's minus the value handed to
        // it last.
        for (var i = 0; i < managedSetter.Parameters.Count - 1; i++)
            signature.ParameterTypes.Add(managedSetter.Parameters[i].ParameterType);

        var managedGetter = new MethodDefinition("get_" + propertyCtx.Name, managedSetter.Attributes, signature);

        ilTypeDefinition.Methods.Add(managedGetter);

        // An auto-property's getter read its backing field, and il2cpp keeps the field even where it drops
        // the accessor, so what goes back is the getter the source had rather than a stub. An indexer or a
        // property with a real body has no such field and returns default instead - the value is never read
        // at runtime either way, the getter is there so that C# will accept the named argument.
        var backingField = signature.ParameterTypes.Count == 0
            ? ilTypeDefinition.Fields.FirstOrDefault(f => f.Name == $"<{propertyCtx.Name}>k__BackingField" && f.IsStatic == propertyCtx.IsStatic)
            : null;

        if (backingField == null)
        {
            managedGetter.ReplaceMethodBodyWithMinimalImplementation();
            return managedGetter;
        }

        managedGetter.CilMethodBody = new();

        var instructions = managedGetter.CilMethodBody.Instructions;

        if (!propertyCtx.IsStatic)
            instructions.Add(CilOpCodes.Ldarg_0);

        instructions.Add(propertyCtx.IsStatic ? CilOpCodes.Ldsfld : CilOpCodes.Ldfld, backingField);
        instructions.Add(CilOpCodes.Ret);

        return managedGetter;
    }

    // A Unity PropertyAttribute subclass is two steps from System.Attribute and a game's own attribute base
    // can be further still, so the whole chain is walked rather than just the immediate base.
    private static bool InheritsAttribute(TypeAnalysisContext typeContext)
    {
        for (var current = typeContext.BaseType; current != null; current = current.BaseType)
        {
            if (current is { Namespace: "System", Name: "Attribute" })
                return true;
        }

        return false;
    }

    private static void CopyEventsInType(TypeAnalysisContext cppTypeDefinition, TypeDefinition ilTypeDefinition)
    {
        foreach (var eventCtx in cppTypeDefinition.Events)
        {
            var eventType = eventCtx.ToTypeSignature().ToTypeDefOrRef();

            var managedEvent = new EventDefinition(eventCtx.Name, (EventAttributes)eventCtx.Attributes, eventType);

            var managedAdder = eventCtx.Adder?.GetExtraData<MethodDefinition>("AsmResolverMethod");
            var managedRemover = eventCtx.Remover?.GetExtraData<MethodDefinition>("AsmResolverMethod");
            var managedInvoker = eventCtx.Invoker?.GetExtraData<MethodDefinition>("AsmResolverMethod");

            managedEvent.SetSemanticMethods(managedAdder, managedRemover, managedInvoker);

            RenameBackingFieldOf(managedEvent, ilTypeDefinition);

            eventCtx.PutExtraData("AsmResolverEvent", managedEvent);

            ilTypeDefinition.Events.Add(managedEvent);
        }
    }

    private static void RenameBackingFieldOf(EventDefinition managedEvent, TypeDefinition ilTypeDefinition)
    {
        if (!RenameEventBackingFields || managedEvent.Name?.ToString() is not { Length: > 0 } eventName)
            return;

        foreach (var field in ilTypeDefinition.Fields)
        {
            if (field.Name?.ToString() is not { } fieldName || !IsEventBackingFieldName(fieldName, eventName))
                continue;

            // The suffix a property backing field carries, without the angle brackets that are the one part
            // of that name C# cannot write.
            field.Name = UnusedMemberName(ilTypeDefinition, fieldName + "__BackingField");
            return;
        }
    }

    // The two spellings the decompiler takes for an event's storage and hides - the C# one and the VB one.
    private static bool IsEventBackingFieldName(string fieldName, string eventName)
        => fieldName == eventName || fieldName == eventName + "Event";

    // A duplicate member name is worse than an ugly one, so the new name is held against everything else
    // the type declares before it is used.
    private static string UnusedMemberName(TypeDefinition ilTypeDefinition, string name)
    {
        var candidate = name;

        for (var suffix = 2; NameIsDeclaredBy(ilTypeDefinition, candidate); suffix++)
            candidate = name + "_" + suffix;

        return candidate;
    }

    private static bool NameIsDeclaredBy(TypeDefinition ilTypeDefinition, string name)
        => ilTypeDefinition.Fields.Any(f => f.Name == name)
            || ilTypeDefinition.Methods.Any(m => m.Name == name)
            || ilTypeDefinition.Properties.Any(p => p.Name == name)
            || ilTypeDefinition.Events.Any(e => e.Name == name)
            || ilTypeDefinition.NestedTypes.Any(t => t.Name == name);

    public static void AddExplicitInterfaceImplementations(AssemblyAnalysisContext asmContext)
    {
        var managedAssembly = asmContext.GetExtraData<AssemblyDefinition>("AsmResolverAssembly") ?? throw new("AsmResolver assembly not found in assembly analysis context for " + asmContext);
        var runtimeContext = asmContext.AppContext.GetExtraData<RuntimeContext>("AsmResolverRuntimeContext") ?? throw new("AsmResolver runtime context not found in application analysis context");

        var module = managedAssembly.ManifestModule!;

        foreach (var typeContext in asmContext.Types)
        {
            if (IsTypeContextModule(typeContext))
                continue;

            var managedType = typeContext.GetExtraData<TypeDefinition>("AsmResolverType") ?? throw new($"AsmResolver type not found in type analysis context for {typeContext.Definition?.FullName}");

#if !DEBUG
            try
#endif
            {
                AddExplicitInterfaceImplementations(managedType, typeContext, runtimeContext);
            }
#if !DEBUG
            catch (Exception e)
            {
                throw new Exception($"Failed to process type {managedType.FullName} (module {managedType.DeclaringModule?.Name}, declaring type {managedType.DeclaringType?.FullName}) in {asmContext.Name}", e);
            }
#endif
        }
    }

    private static void AddExplicitInterfaceImplementations(TypeDefinition type, TypeAnalysisContext typeContext, RuntimeContext runtimeContext)
    {
        List<(PropertyDefinition InterfaceProperty, TypeSignature InterfaceType, MethodDefinition Method)>? getMethodsToCreate = null;
        List<(PropertyDefinition InterfaceProperty, TypeSignature InterfaceType, MethodDefinition Method)>? setMethodsToCreate = null;

        foreach (var methodContext in typeContext.Methods)
        {
            var isPrivate = (methodContext.Attributes & System.Reflection.MethodAttributes.MemberAccessMask) == System.Reflection.MethodAttributes.Private;

            foreach (var overrideContext in methodContext.Overrides)
            {
                if (overrideContext.Name == methodContext.Name && !isPrivate)
                    continue;

                var interfaceMethod = (IMethodDefOrRef)overrideContext.ToMethodDescriptor();
                var method = methodContext.GetExtraData<MethodDefinition>("AsmResolverMethod") ?? throw new($"AsmResolver method not found in method analysis context for {methodContext}");
                type.MethodImplementations.Add(new MethodImplementation(interfaceMethod, method));
                var resolutionStatus = interfaceMethod.Resolve(runtimeContext, out var interfaceMethodResolved);
                if (resolutionStatus == ResolutionStatus.Success && interfaceMethodResolved != null)
                {
                    if (interfaceMethodResolved.IsGetMethod && !method.IsGetMethod)
                    {
                        getMethodsToCreate ??= [];
                        var interfacePropertyResolved = interfaceMethodResolved.DeclaringType!.Properties.First(p => p.Semantics.Contains(interfaceMethodResolved.Semantics));
                        getMethodsToCreate.Add((interfacePropertyResolved, interfaceMethod.DeclaringType!.ToTypeSignature(runtimeContext), method));
                    }
                    else if (interfaceMethodResolved.IsSetMethod && !method.IsSetMethod)
                    {
                        setMethodsToCreate ??= [];
                        var interfacePropertyResolved = interfaceMethodResolved.DeclaringType!.Properties.First(p => p.Semantics.Contains(interfaceMethodResolved.Semantics));
                        setMethodsToCreate.Add((interfacePropertyResolved, interfaceMethod.DeclaringType!.ToTypeSignature(runtimeContext), method));
                    }
                }
            }
        }

        // Il2Cpp doesn't include properties for explicit interface implementations, so we have to create them ourselves.
        if (getMethodsToCreate is not null)
        {
            foreach (var entry in getMethodsToCreate)
            {
                var (interfaceProperty, interfaceType, getMethod) = entry;
                var setMethod = setMethodsToCreate?
                    .FirstOrDefault(e => e.InterfaceProperty == interfaceProperty && runtimeContext.SignatureComparer.Equals(e.InterfaceType, interfaceType))
                    .Method;

                var name = $"{interfaceType.FullName}.{interfaceProperty.Name}";
                var propertySignature = getMethod.IsStatic
                    ? PropertySignature.CreateStatic(getMethod.Signature!.ReturnType, getMethod.Signature.ParameterTypes)
                    : PropertySignature.CreateInstance(getMethod.Signature!.ReturnType, getMethod.Signature.ParameterTypes);
                var property = new PropertyDefinition(name, interfaceProperty.Attributes, propertySignature);
                type.Properties.Add(property);
                property.SetSemanticMethods(getMethod, setMethod);
            }
        }
        if (setMethodsToCreate is not null)
        {
            foreach (var entry in setMethodsToCreate)
            {
                var (interfaceProperty, interfaceType, setMethod) = entry;
                if (getMethodsToCreate?.Any(e => e.InterfaceProperty == interfaceProperty && runtimeContext.SignatureComparer.Equals(e.InterfaceType, interfaceType)) == true)
                    continue;
                var name = $"{interfaceType.FullName}.{interfaceProperty.Name}";
                var propertySignature = setMethod.IsStatic
                    ? PropertySignature.CreateStatic(setMethod.Signature!.ParameterTypes[^1], setMethod.Signature.ParameterTypes.Take(setMethod.Signature.ParameterTypes.Count - 1))
                    : PropertySignature.CreateInstance(setMethod.Signature!.ParameterTypes[^1], setMethod.Signature.ParameterTypes.Take(setMethod.Signature.ParameterTypes.Count - 1));
                var property = new PropertyDefinition(name, interfaceProperty.Attributes, propertySignature);
                type.Properties.Add(property);
                property.SetSemanticMethods(null, setMethod);
            }
        }
    }
}
