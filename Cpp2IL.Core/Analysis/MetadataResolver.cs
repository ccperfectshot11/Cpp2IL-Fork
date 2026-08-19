using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.Il2CppApiFunctions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL;

namespace Cpp2IL.Core.Analysis;

// Diagnostic-only (env CPP2IL_FIELDDIAG=1): tallies why a typed-base [local+offset] memory operand
// did or did not resolve to a field, so the biggest field-lookup gap can be found. Zero cost when off.
internal static class FieldDiag
{
    public static readonly bool Enabled = System.Environment.GetEnvironmentVariable("CPP2IL_FIELDDIAG") == "1";
    private static long _candidates, _resolved, _failValueTypeBase, _failBeyondLayout, _failNoExactMatch, _failGenericVt;
    private static int _hooked;

    private static void EnsureDump()
    {
        if (System.Threading.Interlocked.Exchange(ref _hooked, 1) == 0)
            System.AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                System.Console.WriteLine("==== FIELD-OFFSET DIAG (typed-base [local+offset]) ====");
                System.Console.WriteLine($"  candidates            : {_candidates}");
                System.Console.WriteLine($"  resolved -> ldfld     : {_resolved}");
                System.Console.WriteLine($"  FAIL value-type base  : {_failValueTypeBase}");
                System.Console.WriteLine($"  FAIL beyond layout    : {_failBeyondLayout}");
                System.Console.WriteLine($"  FAIL no exact match   : {_failNoExactMatch}");
                System.Console.WriteLine($"  FAIL generic vt skip  : {_failGenericVt}");
                System.Console.WriteLine("  -- delta to nearest lower field (0x1000 = >0x200 / beyond layout) --");
                foreach (var kv in DeltaToNearestField.OrderByDescending(k => k.Value).Take(20))
                    System.Console.WriteLine($"     delta 0x{kv.Key:X4} : {kv.Value}");
            };
    }

    public static void Candidate() { if (!Enabled) return; EnsureDump(); System.Threading.Interlocked.Increment(ref _candidates); }
    public static void Resolved() { if (!Enabled) return; System.Threading.Interlocked.Increment(ref _resolved); }
    public static void GenericVtSkip() { if (!Enabled) return; System.Threading.Interlocked.Increment(ref _failGenericVt); }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> DeltaToNearestField = new();

    public static void Failure(TypeAnalysisContext owner, long addend)
    {
        if (!Enabled) return;
        if (owner.IsValueType) { System.Threading.Interlocked.Increment(ref _failValueTypeBase); return; }

        long maxOffset = -1;
        long nearestBelow = -1;
        for (var t = owner; t != null; t = t.BaseType)
            foreach (var f in t.Fields)
                if (!f.IsStatic && f.BackingData is { } bd)
                {
                    if (bd.FieldOffset > maxOffset) maxOffset = bd.FieldOffset;
                    if (bd.FieldOffset <= addend && bd.FieldOffset > nearestBelow) nearestBelow = bd.FieldOffset;
                }

        // Delta from the accessed offset down to the closest real field. 0 would be an exact hit (so
        // it never reaches here); a constant delta of 0x10 is the object header; small varying positive
        // deltas are unpadded inherited fields; a huge delta means the offset is past the whole layout.
        var delta = nearestBelow < 0 ? -1 : addend - nearestBelow;
        var bucket = delta < 0 ? -1 : delta > 0x200 ? 0x1000 : delta;
        DeltaToNearestField.AddOrUpdate(bucket, 1, (_, v) => v + 1);

        if (addend > maxOffset) System.Threading.Interlocked.Increment(ref _failBeyondLayout);
        else System.Threading.Interlocked.Increment(ref _failNoExactMatch);
    }
}

public static class MetadataResolver
{
    public static void ResolveAll(MethodAnalysisContext method)
    {
        ResolveStringLiteralAccessors(method);
        ResolveCalls(method);
        ResolveGetter(method);
        ResolveMetadataUsages(method);
    }

    private static void ResolveStringLiteralAccessors(MethodAnalysisContext method)
    {
        var libContext = method.AppContext.LibCpp2IlContext;

        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (instruction.OpCode != OpCode.Call || instruction.Operands[1] is not LocalVariable result)
                continue;

            for (var i = 2; i < instruction.Operands.Count; i++)
            {
                if (LiteralSlotAddress(instruction.Operands[i], definitions) is not { } address
                    || libContext.GetLiteralByAddress(address) is not { } literal)
                    continue;

                instruction.OpCode = OpCode.Move;
                instruction.SetOperands(result, new StringLiteral(literal));
                break;
            }
        }
    }

    private static ulong? LiteralSlotAddress(IOperand operand, Dictionary<LocalVariable, Instruction> definitions) =>
        operand switch
        {
            Immediate immediate => immediate.UnsignedValue,
            LocalVariable local when definitions.TryGetValue(local, out var definition)
                && definition is { OpCode: OpCode.Move, Operands: [_, Immediate immediate] } => immediate.UnsignedValue,
            _ => null,
        };

    /// <summary>
    /// Resolves <c>Move local, [absoluteAddress]</c> loads of IL2CPP metadata-usage globals into a
    /// strongly-typed operand: a string literal, a <see cref="TypeAnalysisContext"/> (an Il2CppType*/
    /// Il2CppClass* usage) or, for a MethodInfo* usage, a <see cref="RuntimeMethodInfoAnalysisContext"/>
    /// naming the method it refers to (also used to type the local - see <see cref="LocalVariables"/>),
    /// or likewise a <see cref="RuntimeFieldInfoAnalysisContext"/> for a FieldInfo* usage.
    /// </summary>
    private static void ResolveMetadataUsages(MethodAnalysisContext method)
    {
        var libContext = method.AppContext.LibCpp2IlContext;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Move)
                continue;

            if (instruction.Operands[0] is not LocalVariable)
                continue;

            var address = instruction.Operands[1] switch
            {
                MemoryOperand { Base: null, Index: null, Scale: 0 } memory => (ulong)memory.Addend,
                Immediate immediate => immediate.UnsignedValue,
                _ => 0ul,
            };

            if (address == 0)
                continue;

            // String literal.
            var stringLiteral = libContext.GetLiteralByAddress(address);
            if (stringLiteral != null)
            {
                instruction.SetOperand(1, new StringLiteral(stringLiteral));
                continue;
            }

            // Type metadata usage (Il2CppType* / Il2CppClass*).
            if (method.DeclaringType is { } declaringType)
            {
                var typeGlobal = libContext.GetTypeGlobalByAddress(address);
                if (typeGlobal != null)
                {
                    instruction.SetOperand(1, declaringType.AppContext.ResolveIl2CppType(typeGlobal));
                    continue;
                }
            }

            // Method metadata usage (MethodInfo*). On metadata v27+ GetMethodGlobalByAddress can return
            // any global, so confirm it is actually a method before resolving - the resolver's switch
            // throws on other usage kinds.
            var methodUsage = libContext.GetMethodGlobalByAddress(address);
            if (methodUsage?.Type is MetadataUsageType.MethodDef or MetadataUsageType.MethodRef
                && method.AppContext.ResolveContextForMethod(methodUsage) is { DeclaringType: { } methodDeclaringType } methodContext)
            {
                instruction.SetOperand(1, new RuntimeMethodInfoAnalysisContext(methodContext, methodDeclaringType.DeclaringAssembly));
                continue;
            }

            // Field metadata usage (FieldInfo*), e.g. the RuntimeFieldHandle passed to InitializeArray.
            if (libContext.GetRawFieldGlobalByAddress(address) is { Type: MetadataUsageType.FieldInfo } fieldUsage
                && method.AppContext.ResolveContextForField(fieldUsage.AsField()) is { DeclaringType.DeclaringAssembly: { } fieldAssembly } fieldContext)
                instruction.SetOperand(1, new RuntimeFieldInfoAnalysisContext(fieldContext, fieldAssembly));
        }
    }

    /// <summary>
    /// Replaces every <c>[base + addend]</c> memory operand whose base is a typed local with a
    /// <see cref="FieldReference"/> to the field at that offset. Returns whether any operand was
    /// resolved this pass, so the type/field fixpoint can detect convergence: as more bases become
    /// typed (a field load types its result, which is the base of the next load), more offsets
    /// resolve, so this is re-run until it stops finding new fields.
    /// </summary>
    public static bool ResolveFieldOffsets(MethodAnalysisContext method)
    {
        var changed = false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                var operand = instruction.Operands[i];

                if (operand is not MemoryOperand memory)
                    continue;

                // Has to be [base (local) + addend (field offset)]
                if (memory.Index != null || memory.Scale != 0)
                    continue;

                if (memory.Base is not LocalVariable local || local?.Type == null)
                    continue;

                // check if static field access
                var staticOwner = (local.Type as StaticFieldStorageTypeAnalysisContext)?.OwnerType;
                var owner = staticOwner ?? local.Type;

                // A managed pointer to a struct addresses the UNBOXED value, whose offsets start at 0,
                // while il2cpp metadata measures field offsets from the boxed form - confirmed in the
                // runtime source (vm/Object.cpp): Unbox(obj) == obj + sizeof(Il2CppObject), and boxing
                // writes at field.offset - sizeof(Il2CppObject). So for a byref-to-struct base the
                // search offset needs the header added back.
                //
                // Only an explicit byref qualifies. A local typed as the value type itself is
                // ambiguous - it may hold a BOXED instance, which is addressed with the raw metadata
                // offset - and shifting those regresses badly, so they keep the existing behaviour.
                // Byref bases, meanwhile, resolve against the byref type today and so never match a
                // field at all, which makes this pure gain for them.
                var byRefStruct = staticOwner == null
                    && owner is ByRefTypeAnalysisContext { ElementType: { IsValueType: true } referentStruct }
                    ? referentStruct
                    : null;

                if (byRefStruct != null)
                    owner = byRefStruct;

                var searchAddend = byRefStruct != null ? memory.Addend + ValueTypeHeaderSize : memory.Addend;

                var genericOwner = owner as GenericInstanceTypeAnalysisContext;
                FieldDiag.Candidate();

                FieldAnalysisContext? field;
                if (genericOwner != null && staticOwner == null)
                {
                    // metadata has all-0 offsets for generic definitions, so recompute layout
                    // TODO support user-defined value types
                    if (genericOwner.GenericArguments.Any(a => a.IsValueType))
                    {
                        FieldDiag.GenericVtSkip();
                        continue;
                    }

                    field = GenericInstanceFieldLayout.FindFieldAtOffset(genericOwner.GenericType, memory.Addend);
                }
                else if (staticOwner == null && owner.GenericParameters.Count > 0)
                {
                    field = GenericInstanceFieldLayout.FindFieldAtOffset(owner, searchAddend);
                }
                else
                {
                    // an inherited field exists on the base type but sits at the same offset in the
                    // derived layout, so the whole chain is searched
                    field = null;
                    for (var candidateOwner = genericOwner?.GenericType ?? owner; candidateOwner != null && field == null; candidateOwner = candidateOwner.BaseType)
                        field = candidateOwner.Fields.FirstOrDefault(f => f.IsStatic == (staticOwner != null)
                            && (f.Attributes & FieldAttributes.Literal) == 0 // consts have no storage but their metadata offset is 0, which would match
                            && f.BackingData?.FieldOffset == searchAddend);
                }

                FieldAnalysisContext[]? innerPath = null;

                if (field == null)
                {
                    // The offset can land inside a value-type field, which means the source was a nested
                    // access such as a.b.c. Walk into the containing field and keep going until the
                    // remaining offset lands exactly on a field.
                    if (ResolveNestedField(owner, searchAddend, staticOwner != null) is not { } nested)
                    {
                        FieldDiag.Failure(owner, searchAddend);
                        continue;
                    }

                    field = nested.Outer;
                    innerPath = nested.Path;
                }

                // make sure we have a full GIT for field access. open type is bad.
                if (genericOwner != null)
                    field = new ConcreteGenericFieldAnalysisContext(field, genericOwner);

                instruction.SetOperand(i, new FieldReference(field, local, (int)memory.Addend, innerPath));
                changed = true;
                FieldDiag.Resolved();
            }
        }

        return changed;
    }

    private static void ResolveCalls(MethodAnalysisContext method)
    {
        foreach (var block in method.ControlFlowGraph!.Blocks)
        {
            if (block.BlockType != BlockType.Call && block.BlockType != BlockType.TailCall)
                continue;

            var callInstruction = block.Instructions[^1];
            if (callInstruction.Operands[0] is not Immediate dest)
                continue;

            var target = dest.UnsignedValue;

            var keyFunctionAddresses = method.AppContext.GetOrCreateKeyFunctionAddresses();

            if (keyFunctionAddresses.IsKeyFunctionAddress(target))
            {
                HandleKeyFunction(method.AppContext, callInstruction, target, keyFunctionAddresses);

                if (target == keyFunctionAddresses.il2cpp_codegen_initialize_runtime_metadata_inline
                    && callInstruction is { OpCode: OpCode.Call, Operands: [_, var initResult, var handle, ..] })
                {
                    callInstruction.OpCode = OpCode.Move;
                    callInstruction.SetOperands(initResult, handle);
                }

                continue;
            }

            //Non-key function call. Try to find a single match
            if (!method.AppContext.MethodsByAddress.TryGetValue(target, out var targetMethods))
            {
                // Not a managed method at all. It may be one of the runtime helpers built around an exception
                // type, which either throw it themselves or build it and hand it back for the caller to raise.
                if (ThrowHelperRecovery.GetThrownException(method.AppContext, target) is { } thrown)
                {
                    if (callInstruction.Destination is LocalVariable produced && method.ControlFlowGraph!.Instructions.Any(i => i.Sources.Any(s => ReferenceEquals(s, produced))))
                    {
                        callInstruction.OpCode = OpCode.Newobj;
                        callInstruction.SetOperands(produced, thrown);
                    }
                    else
                    {
                        callInstruction.OpCode = OpCode.Throw;
                        callInstruction.SetOperands(thrown);
                    }

                    continue;
                }

                // Otherwise it may be one of the raisers, which throw the exception they are given
                var raisedIndex = callInstruction.OpCode == OpCode.CallVoid ? 1 : 2;

                if (callInstruction.Operands.Count > raisedIndex && ThrowHelperRecovery.IsExceptionRaiser(method.AppContext, target))
                {
                    var raised = callInstruction.Operands[raisedIndex];

                    callInstruction.OpCode = OpCode.Throw;
                    callInstruction.SetOperands(raised);
                }

                continue;
            }

            // Duplicated/Shared method bodies are resolved later in ResolveCallsViaMethodInfo/ResolveAmbiguousCalls.
            if (targetMethods is not [{ } singleTargetMethod])
                continue;

            callInstruction.SetOperand(0, singleTargetMethod);
            singleTargetMethod.AppContext.InstructionSet.CallingConventionResolver?.RemapRawArguments(callInstruction, singleTargetMethod);
        }

        method.ControlFlowGraph.MergeCallBlocks();
    }

    /// <summary>
    /// Resolves calls whose address maps to more than one method by matching the receiver's known
    /// type against the candidates' declaring types. Runs inside the type/field fixpoint and so
    /// re-fires as receivers become typed - a resolved call types its return value, which can type
    /// the receiver of a further call. Returns whether any call was resolved this pass.
    ///
    /// Conservative by design: it commits only when exactly one non-static candidate's declaring
    /// type matches the receiver's type. Anything still untyped or ambiguous is left for a later
    /// pass, or left unresolved - it never guesses.
    /// </summary>
    public static bool ResolveAmbiguousCalls(MethodAnalysisContext method)
    {
        var changed = false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (!instruction.IsCall)
                continue;

            // A resolved call's target is a method/key-function name; only unresolved ones are still numeric.
            if (instruction.Operands[0] is not Immediate target)
                continue;

            if (!method.AppContext.MethodsByAddress.TryGetValue(target.UnsignedValue, out var candidates) || candidates.Count < 2)
                continue;

            // e.g. string.Equals and string.op_Equality, identical params, instance type, and bodies are shared
            // we can't differentiate which is being called but it doesn't matter
            if (AreInterchangeable(candidates))
            {
                var preferred = PreferredOf(candidates);
                instruction.SetOperand(0, preferred);
                preferred.AppContext.InstructionSet.CallingConventionResolver?.RemapRawArguments(instruction, preferred);
                changed = true;
                continue;
            }

            if (GetReceiver(instruction) is not { Type: { } receiverType } receiver)
                continue;

            // Prefer picking base ctor if we are a ctor
            var callerIsCtor = method.Name == ".ctor" && receiver.IsThis;

            // Handle methods with shared bodies
            var match = default(MethodAnalysisContext);

            for (var type = receiverType; type != null && match == null; type = type.BaseType)
            {
                var matches = candidates.Where(c => !c.IsStatic && IsSameType(c.DeclaringType, type)).ToList();

                if (matches.Count > 1 && callerIsCtor)
                    matches = matches.Where(c => c.Name == ".ctor").ToList();

                if (matches.Count > 1)
                    break;

                match = matches.SingleOrDefault();
            }

            if (match == null)
                continue;

            instruction.SetOperand(0, match);
            match.AppContext.InstructionSet.CallingConventionResolver?.RemapRawArguments(instruction, match);
            changed = true;
        }

        return changed;
    }

    private static bool AreInterchangeable(List<MethodAnalysisContext> candidates)
    {
        var first = candidates[0];

        return candidates.All(c => c.IsStatic == first.IsStatic
            && ReferenceEquals(c.DeclaringType, first.DeclaringType)
            && ReferenceEquals(c.ReturnType, first.ReturnType)
            && c.Parameters.Count == first.Parameters.Count
            && SameParameterTypes(c, first));
    }

    private static bool SameParameterTypes(MethodAnalysisContext a, MethodAnalysisContext b)
    {
        for (var i = 0; i < a.Parameters.Count; i++)
        {
            if (!ReferenceEquals(a.Parameters[i].ParameterType, b.Parameters[i].ParameterType))
                return false;
        }

        return true;
    }

    // Prefer operators if possible
    private static MethodAnalysisContext PreferredOf(List<MethodAnalysisContext> candidates) =>
        candidates.FirstOrDefault(c => c.Name.StartsWith("op_")) ?? candidates[0];

    // The receiver ('this') of a call is the first integer-slot argument: operand 1 for CallVoid
    // (after the target), operand 2 for Call (after the target and the return value).
    // A value type receiver is passed byref, so it arrives as an AddressOf over the local.
    private static LocalVariable? GetReceiver(Instruction call)
    {
        var index = call.OpCode == OpCode.CallVoid ? 1 : 2;

        return index < call.Operands.Count
            ? call.Operands[index] switch
            {
                LocalVariable local => local,
                AddressOf { Target: LocalVariable addressed } => addressed,
                _ => null
            }
            : null;
    }

    // Concrete generic method contexts build their declaring type fresh rather than via the
    // GetOrCreate cache, so generic instances also need comparing structurally.
    // TODO Fix this, concrete generic methods should use GetOrCreate
    private static bool IsSameType(TypeAnalysisContext? a, TypeAnalysisContext? b)
    {
        if (ReferenceEquals(a, b))
            return true;

        if (a is not GenericInstanceTypeAnalysisContext leftInstance
            || b is not GenericInstanceTypeAnalysisContext rightInstance
            || !ReferenceEquals(leftInstance.GenericType, rightInstance.GenericType)
            || leftInstance.GenericArguments.Count != rightInstance.GenericArguments.Count)
            return false;

        for (var i = 0; i < leftInstance.GenericArguments.Count; i++)
        {
            if (!IsSameType(leftInstance.GenericArguments[i], rightInstance.GenericArguments[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves any Call (theoretically should always be a CallVoid) target directly after a Newobj to a constructor call.
    /// </summary>
    public static bool ResolveConstructorCalls(MethodAnalysisContext method)
    {
        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.Destination is LocalVariable definition)
                definitions[definition] = instruction;

        var changed = false;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (!instruction.IsCall || instruction.Operands[0] is not Immediate callTarget)
                continue;

            if (!method.AppContext.MethodsByAddress.TryGetValue(callTarget.UnsignedValue, out var candidates))
                continue;

            if (GetReceiver(instruction) is not { } receiver || AllocatedType(receiver, definitions) is not { } allocatedType)
                continue;

            var constructor = candidates.FirstOrDefault(c => !c.IsStatic && c.Name == ".ctor" && ReferenceEquals(c.DeclaringType, allocatedType))
                              ?? FindConstructorForSharedBody(allocatedType, candidates);
            if (constructor == null)
                continue;

            instruction.SetOperand(0, constructor);
            constructor.AppContext.InstructionSet.CallingConventionResolver?.RemapRawArguments(instruction, constructor);
            changed = true;
        }

        return changed;
    }

    private static MethodAnalysisContext? FindConstructorForSharedBody(TypeAnalysisContext allocatedType, List<MethodAnalysisContext> candidates)
    {
        var candidateParamCounts = new HashSet<int>(candidates
            .Where(c => c is { IsStatic: false, Name: ".ctor" })
            .Select(c => c.Parameters.Count));

        if (candidateParamCounts.Count == 0)
            return null;

        var definition = allocatedType is GenericInstanceTypeAnalysisContext genericInstance ? genericInstance.GenericType : allocatedType;
        var matches = definition.Methods
            .Where(m => m is { IsStatic: false, Name: ".ctor" } && candidateParamCounts.Contains(m.Parameters.Count))
            .ToList();

        if (matches is not [{ } match])
            return null;

        return allocatedType is GenericInstanceTypeAnalysisContext instance
            ? new ConcreteGenericMethodAnalysisContext(match, instance.GenericArguments, [])
            : match;
    }

    // Follow SSA copies from a local back to the Newobj that produced the value
    private static TypeAnalysisContext? AllocatedType(LocalVariable local, Dictionary<LocalVariable, Instruction> definitions)
    {
        var visited = new HashSet<LocalVariable>();

        while (visited.Add(local) && definitions.TryGetValue(local, out var definition))
        {
            switch (definition.OpCode)
            {
                case OpCode.Newobj:
                    return (definition.Operands[0] as LocalVariable)?.Type;
                case OpCode.Move when definition.Operands[1] is LocalVariable source:
                    local = source;
                    continue;
            }

            break;
        }

        return null;
    }

    /// <summary>
    /// Resolves calls whose address maps to more than one method by reading the runtime
    /// <c>MethodInfo*</c> the caller passes in, if there is one.
    /// </summary>
    public static bool ResolveCallsViaMethodInfo(MethodAnalysisContext method)
    {
        var changed = false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (!instruction.IsCall)
                continue;

            if (instruction.Operands[0] is not Immediate target)
                //Already resolved
                continue;

            if (GetMethodInfoArgument(instruction) is not { RepresentedMethod: { } representedMethod })
                //No MethodInfo to work with
                continue;

            if (!method.AppContext.MethodsByAddress.TryGetValue(target.UnsignedValue, out var candidates))
            {
                // Some shared generic bodies aren't in the address map at all (todo investigate?).
                // Il2cpp still passes the concrete MethodInfo as the hidden final parameter, so we can use a methodof there if we have one.
                // However, make sure it isn't our OWN hidden MethodInfo arg, because that would turn all unknown calls into recursion
                if (ReferenceEquals(representedMethod, method))
                    continue;

                var firstArg = instruction.OpCode == OpCode.CallVoid ? 1 : 2;
                var hiddenParamIndex = firstArg
                    + (representedMethod.AppContext.InstructionSet.CallingConventionResolver?.ReturnsViaHiddenBuffer(representedMethod) == true ? 1 : 0)
                    + (representedMethod.IsStatic ? 0 : 1) + representedMethod.Parameters.Count;

                if (hiddenParamIndex >= instruction.Operands.Count
                    || AsMethodInfo(instruction.Operands[hiddenParamIndex]) == null)
                    continue;

                instruction.SetOperand(0, representedMethod);
                representedMethod.AppContext.InstructionSet.CallingConventionResolver?.RemapRawArguments(instruction, representedMethod);
                changed = true;
                continue;
            }

            if (candidates.Count < 2)
                continue;

            //Try to actually match on the method name so we don't just replace a call with something else.
            var representedBase = BaseMethodOf(representedMethod);
            if (!candidates.Any(candidate => ReferenceEquals(BaseMethodOf(candidate), representedBase)))
                continue;

            instruction.SetOperand(0, representedMethod);
            representedMethod.AppContext.InstructionSet.CallingConventionResolver?.RemapRawArguments(instruction, representedMethod);
            changed = true;
        }

        return changed;
    }

    // Offset of Il2CppClass::vtable, VirtualInvokeData entries of {methodPtr, MethodInfo*}.
    // TODO this is almost certainly not correct on every version
    private const long VTableOffset64 = 0x138;
    private const long VTableOffset32 = 0xC0;
    
    // Resolves virtual dispatch through <c>[klass + vtableOffset + slot * sizeof(VirtualInvokeData)]</c>
    // as long as the klass local's represented type is known.
    public static bool ResolveVirtualCalls(MethodAnalysisContext method)
    {
        var pointerSize = method.AppContext.Binary.PointerSizeBytes;
        var vtableOffset = pointerSize == 8 ? VTableOffset64 : VTableOffset32;
        var invokeDataSize = 2L * pointerSize;
        var changed = false;

        var loads = new Dictionary<LocalVariable, MemoryOperand>();
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode == OpCode.Move
                && instruction.Operands[0] is LocalVariable destination
                && instruction.Operands[1] is MemoryOperand { Index: null, Scale: 0 } load)
                loads[destination] = load;
        }

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (instruction.OpCode != OpCode.IndirectCall)
                continue;

            if (SlotLoad(instruction.Operands[0]) is not { } target
                || target.Base is not LocalVariable { Type: RuntimeClassTypeAnalysisContext { RepresentedType: { } receiverType } } klassLocal)
                continue;

            var offset = target.Addend - vtableOffset;
            if (offset < 0 || offset % invokeDataSize != 0)
                continue;

            var slot = (int)(offset / invokeDataSize);
            if (ResolveVTableSlot(method.AppContext, receiverType, slot) is not { } resolved)
                continue;

            var assembly = resolved.DeclaringType?.DeclaringAssembly ?? method.DeclaringType?.DeclaringAssembly;

            instruction.OpCode = OpCode.Call; // same operand layout as IndirectCall, and we've resolved it now
            instruction.SetOperand(0, resolved);
            resolved.AppContext.InstructionSet.CallingConventionResolver?.RemapRawArguments(instruction, resolved);

            // the MethodInfo field is also the same method, name it, for cleanliness and so it can
            // serve as a hidden final parameter if needed
            for (var i = 1; i < instruction.Operands.Count && assembly != null; i++)
            {
                if (SlotLoad(instruction.Operands[i]) is { } methodInfoLoad
                    && ReferenceEquals(methodInfoLoad.Base, klassLocal)
                    && methodInfoLoad.Addend == target.Addend + pointerSize)
                    instruction.SetOperand(i, new RuntimeMethodInfoAnalysisContext(resolved, assembly));
            }

            changed = true;
        }

        return changed;

        MemoryOperand? SlotLoad(IOperand operand) => operand switch
        {
            MemoryOperand { Index: null, Scale: 0 } inlined => inlined,
            LocalVariable local when loads.TryGetValue(local, out var load) => load,
            _ => null
        };
    }

    /// <summary>
    /// Resolves a call made through a MethodInfo*. The runtime struct begins with the method's native
    /// entry point (MethodInfo::methodPointer at offset 0 - see the il2cpp runtime source), so calling
    /// through [methodInfo] is just a call to the method that MethodInfo names, which metadata-usage
    /// resolution has already attached to the local as its type. Runs inside the type/field fixpoint,
    /// so it re-fires as more locals become MethodInfo-typed.
    /// </summary>
    public static bool ResolveMethodInfoCalls(MethodAnalysisContext method)
    {
        var loads = new Dictionary<LocalVariable, MemoryOperand>();
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable destination, MemoryOperand { Index: null, Scale: 0 } load] })
                loads[destination] = load;
        }

        var changed = false;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (instruction.OpCode != OpCode.IndirectCall)
                continue;

            if (CalledThroughMethodInfo(instruction.Operands[0], 0) is not { } resolved)
                continue;

            // IndirectCall already has the Call operand layout, so only the target changes.
            instruction.OpCode = OpCode.Call;
            instruction.SetOperand(0, resolved);
            resolved.AppContext.InstructionSet.CallingConventionResolver?.RemapRawArguments(instruction, resolved);
            changed = true;
        }

        return changed;

        MethodAnalysisContext? CalledThroughMethodInfo(IOperand operand, int depth)
        {
            if (depth > 4)
                return null;

            return operand switch
            {
                MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable { Type: RuntimeMethodInfoAnalysisContext { RepresentedMethod: { } represented } } } => represented,
                LocalVariable { Type: RuntimeMethodInfoAnalysisContext { RepresentedMethod: { } direct } } => direct,
                LocalVariable local when loads.TryGetValue(local, out var load) => CalledThroughMethodInfo(load, depth + 1),
                _ => null,
            };
        }
    }

    private static MethodAnalysisContext? ResolveVTableSlot(ApplicationAnalysisContext appContext, TypeAnalysisContext type, int slot)
    {
        var definition = (type as GenericInstanceTypeAnalysisContext)?.GenericType.Definition ?? type.Definition;

        if (definition == null || slot >= definition.VtableCount)
            return null;

        if (appContext.ResolveContextForMethod(definition.VTable[slot]) is { } implementation)
            return implementation;

        // an abstract method has no implementation, try to resolve it
        for (var declarer = type; declarer != null; declarer = declarer.BaseType)
        {
            if (declarer.Methods.FirstOrDefault(m => m.Definition?.slot == slot) is { } declaration)
                return declaration;
        }

        return null;
    }

    private static MethodAnalysisContext BaseMethodOf(MethodAnalysisContext method) =>
        method is ConcreteGenericMethodAnalysisContext { BaseMethodContext: { } baseMethod } ? baseMethod : method;

    private static RuntimeMethodInfoAnalysisContext? GetMethodInfoArgument(Instruction call)
    {
        var firstArg = call.OpCode == OpCode.CallVoid ? 1 : 2;

        for (var i = call.Operands.Count - 1; i >= firstArg; i--)
        {
            if (AsMethodInfo(call.Operands[i]) is { } methodInfo)
                return methodInfo;
        }

        return null;
    }

    private static RuntimeMethodInfoAnalysisContext? AsMethodInfo(IOperand operand) =>
        operand switch
        {
            RuntimeMethodInfoAnalysisContext methodInfo => methodInfo,
            LocalVariable { Type: RuntimeMethodInfoAnalysisContext methodInfoLocal } => methodInfoLocal,
            _ => null
        };

    private static void HandleKeyFunction(ApplicationAnalysisContext appContext, Instruction instruction, ulong target, BaseKeyFunctionAddresses kFA)
    {
        var method = "";
        if (target == kFA.il2cpp_codegen_initialize_method || target == kFA.il2cpp_codegen_initialize_runtime_metadata)
        {
            if (appContext.MetadataVersion < 27)
            {
                method = nameof(kFA.il2cpp_codegen_initialize_method);
            }
            else
            {
                method = nameof(kFA.il2cpp_codegen_initialize_runtime_metadata);
            }
        }
        else
        {
            var pairs = kFA.Pairs.ToList();
            var key = pairs.FirstOrDefault(pair => pair.Value == target).Key;
            if (key == null)
                return;
            method = key;
        }

        if (method != "")
        {
            instruction.SetOperand(0, new StringLiteral(method));
        }
    }

    // Because of il2cpp fields (like cctor_finished_or_no_cctor) [local @ reg+offset] sometimes can't be resolved, but this works for now
    private static void ResolveGetter(MethodAnalysisContext method)
    {
        if (!method.Name.StartsWith("get_"))
            return;

        // Default get: Return [this @ reg+offset]
        var instructions = method.ControlFlowGraph!.Instructions;
        if (instructions.Count == 1)
        {
            var instr = instructions[0];

            if (instr.OpCode != OpCode.Return
                || instr.Operands.Count < 1
                || instr.Operands[0] is not MemoryOperand memory
                || memory.Index != null || memory.Scale != 0
                || memory.Base is not LocalVariable local)
                return;

            var fieldName = $"<{method.Name[4..]}>k__BackingField";

            var field = method.DeclaringType!.Fields.Find(f => f.Name == fieldName);
            if (field == null)
                return;

            instr.SetOperand(0, new FieldReference(field, local, (int)memory.Addend));
        }
    }


    /// <summary>
    /// IL2CPP field offsets on a value type are measured from the start of its boxed form, so the
    /// object header has to be added back when descending into one embedded in another type.
    /// </summary>
    private const long ValueTypeHeaderSize = 0x10;

    private const int MaxNestingDepth = 8;

    /// <summary>
    /// Resolves an offset that does not name a field directly but falls inside a value-type field,
    /// which is how a nested access such as <c>a.b.c</c> appears once compiled. Returns the outermost
    /// field and the remaining ones, or null if any step is ambiguous - a wrong guess here would
    /// silently produce a read of the wrong field, so only exact matches are accepted.
    /// </summary>
    private static (FieldAnalysisContext Outer, FieldAnalysisContext[] Path)? ResolveNestedField(TypeAnalysisContext owner, long addend, bool isStatic)
    {
        var container = FindContainingValueTypeField(owner, addend, isStatic);
        if (container == null)
            return null;

        var outer = container.Value.Field;
        var path = new List<FieldAnalysisContext>();
        var remaining = addend - container.Value.Offset + ValueTypeHeaderSize;
        var current = outer.FieldType;

        for (var depth = 0; depth < MaxNestingDepth; depth++)
        {
            var exact = FindFieldAtExactOffset(current, remaining);
            if (exact != null)
            {
                path.Add(exact);
                return (outer, path.ToArray());
            }

            var inner = FindContainingValueTypeField(current, remaining, isStatic: false);
            if (inner == null)
                return null;

            path.Add(inner.Value.Field);
            remaining = remaining - inner.Value.Offset + ValueTypeHeaderSize;
            current = inner.Value.Field.FieldType;
        }

        return null;
    }

    private static FieldAnalysisContext? FindFieldAtExactOffset(TypeAnalysisContext type, long offset)
    {
        for (var candidate = type; candidate != null; candidate = candidate.BaseType)
        {
            foreach (var field in candidate.Fields)
            {
                if (field.IsStatic || (field.Attributes & FieldAttributes.Literal) != 0)
                    continue;

                if (field.BackingData?.FieldOffset == offset)
                    return field;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the value-type field the offset falls within: the one with the greatest offset not past it.
    /// </summary>
    private static (FieldAnalysisContext Field, long Offset)? FindContainingValueTypeField(TypeAnalysisContext type, long offset, bool isStatic)
    {
        FieldAnalysisContext? best = null;
        long bestOffset = -1;

        for (var candidate = type; candidate != null; candidate = candidate.BaseType)
        {
            foreach (var field in candidate.Fields)
            {
                if (field.IsStatic != isStatic || (field.Attributes & FieldAttributes.Literal) != 0)
                    continue;

                if (field.FieldType is not { IsValueType: true } fieldType || fieldType.IsEnumType)
                    continue;

                var fieldOffset = field.BackingData?.FieldOffset ?? -1;
                if (fieldOffset < 0 || fieldOffset >= offset)
                    continue;

                if (fieldOffset > bestOffset)
                {
                    best = field;
                    bestOffset = fieldOffset;
                }
            }
        }

        return best == null ? null : (best, bestOffset);
    }
}
