using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

public static class LocalVariables
{
    public static int MaxTypePropagationLoopCount = 5000;

    // CPP2IL_INT_ARITH=1 lets add/sub/mul type their result from an integer operand, like the bitwise ops
    // already do. Opt-in, and measured worse: those opcodes are also how pointers are walked, and turning
    // this on adds 351 markers. Kept only so the experiment can be repeated.
    private static readonly bool IntegerArithmetic = Environment.GetEnvironmentVariable("CPP2IL_INT_ARITH") == "1";

    // Types the locals nothing else could type, whose only definition is a constant. On by default
    // (CPP2IL_CONST_INT=0 disables); worth +28 strict-compilable methods on the two Assembly-CSharp DLLs.
    private static readonly bool ConstantLocalsAreIntegers = Environment.GetEnvironmentVariable("CPP2IL_CONST_INT") != "0";

    // Resolves a copy whose two ends carry contradictory types. On by default; CPP2IL_UNIFY_COPIES=0 disables.
    // Measured neutral: STRICT 11,363 -> 11,361, ilverify 762 -> 759. The contradictory copies it targets
    // are real, but almost none of them reach a slot that fails verification, so it buys nothing while it
    // does overwrite a type the inference had settled on. Off; CPP2IL_UNIFY_COPIES=1 to retry.
    private static readonly bool UnifyCopies = Environment.GetEnvironmentVariable("CPP2IL_UNIFY_COPIES") == "1";

    // Folds [L+d] where L=B+k back into [B+k+d] so field resolution can see a base it is able to type. On
    // by default (CPP2IL_FOLD_ADDR=0 disables): the single largest measured win, 3,634 -> 4,339 strict
    // methods on its own, and it drops 1,165 markers.
    private static readonly string? FoldAddressSetting = Environment.GetEnvironmentVariable("CPP2IL_FOLD_ADDR");
    private static readonly bool FoldAddressArithmetic = FoldAddressSetting != "0";

    // CPP2IL_FOLD_ADDR=2 also deletes the add once nothing reads it. Not the default: it edits the control
    // flow graph and measured exactly neutral on strict compilation, so there is nothing to buy the risk.
    private static readonly bool RemoveFoldedAdds = FoldAddressSetting == "2";

    // Locals no inference may write to for the duration of one pass. See SetTypeIfUnknown.
    [ThreadStatic] private static HashSet<LocalVariable>? _blockedFromTyping;

    private const long StaticFieldsOffset64 = 0xB8;
    private const long StaticFieldsOffset32 = 0x5C;

    public static void CreateAll(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        var instructions = cfg.Instructions;

        // Get all registers
        var registers = new List<Register>();
        foreach (var instruction in instructions)
            registers.AddRange(GetRegisters(instruction));

        // Remove duplicates
        registers = registers.Distinct().ToList();

        // Map those to locals
        var locals = new Dictionary<Register, LocalVariable>();
        for (var i = 0; i < registers.Count; i++)
        {
            var register = registers[i];
            locals.Add(register, new LocalVariable($"v{i}", register));
        }

        // Replace registers with locals
        foreach (var instruction in instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                var operand = instruction.Operands[i];

                if (operand is Register register)
                    instruction.SetOperand(i, locals[register]);

                if (operand is AddressOf { Target: Register addressed })
                    instruction.SetOperand(i, new AddressOf(locals[addressed]));

                if (operand is MemoryOperand memory)
                {
                    if (memory.Base != null)
                    {
                        var baseRegister = (Register)memory.Base;
                        memory.Base = locals[baseRegister];
                    }

                    if (memory.Index != null)
                    {
                        var index = (Register)memory.Index;
                        memory.Index = locals[index];
                    }

                    instruction.SetOperand(i, memory);
                }
            }
        }

        method.Locals = locals.Select(kv => kv.Value).ToList();

        // Return local names
        var retValIndex = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode != OpCode.Return || instruction.Operands.Count != 1) continue;

            var returnLocal = (LocalVariable)instruction.Sources[0];

            returnLocal.Name = $"returnVal{retValIndex + 1}";
            returnLocal.IsReturn = true;
            retValIndex++;
        }

        // Add parameter names
        var paramLocals = new List<LocalVariable>();

        var operandOffset = method.IsStatic ? 0 : 1; // 'this'

        // 'this' param
        if (!method.IsStatic && method.Locals.Count > 0 && method.ParameterOperands.Count > 0)
        {
            var thisOperand = (Register)method.ParameterOperands[0];
            var thisLocal = method.Locals.FirstOrDefault(l => l.Register.Number == thisOperand.Number && l.Register.Version == -1);

            if (thisLocal != null)
            {
                thisLocal.Name = "this";
                thisLocal.IsThis = true;
                paramLocals.Add(thisLocal);
            }
            else if (method.Locals.Any(l => l.Register.Number == thisOperand.Number))
            {
                // The receiver register is used, but not at its entry version, so the value we would
                // have called 'this' was overwritten before any read - worth reporting.
                method.AddWarning($"'this' local not found (operand: {thisOperand})");
            }
            // Otherwise the register is never referenced at all: the method simply does not use
            // 'this', so there is no local to name and nothing was lost. Reporting that as an
            // analysis warning put a spurious marker in every such body.
        }

        // Check if method has MethodInfo*
        var hasMethodInfo = (method.ParameterOperands.Count - operandOffset) > method.Parameters.Count;
        var methodInfoIndex = method.ParameterOperands.Count - 1;

        // Add normal parameter names
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            var operandIndex = i + operandOffset;
            if (hasMethodInfo && operandIndex == methodInfoIndex)
                break; // Skip MethodInfo*

            if (operandIndex >= method.ParameterOperands.Count)
                break;

            if (method.ParameterOperands[operandIndex] is not Register reg)
                continue;

            var local = method.Locals.FirstOrDefault(l => l.Register.Number == reg.Number && l.Register.Version == -1);
            if (local == null)
                continue;

            local.Name = method.Parameters[i].ParameterName;
            paramLocals.Add(local);
        }

        // Add MethodInfo*
        if (hasMethodInfo)
        {
            var methodInfoOperand = (Register)method.ParameterOperands[methodInfoIndex];
            var methodInfoLocal = method.Locals.FirstOrDefault(l => l.Register.Number == methodInfoOperand.Number && l.Register.Version == -1);

            if (methodInfoLocal != null)
            {
                methodInfoLocal.Name = "methodInfo";
                methodInfoLocal.IsMethodInfo = true;
                paramLocals.Add(methodInfoLocal);
            }
        }

        method.ParameterLocals = paramLocals;

        // the hidden return buffer takes the first argument register. we type it as the return
        // type so stores into it resolve to fields
        if (method.AppContext.Binary.PointerSizeBytes == 8
            && method.AppContext.InstructionSet.CallingConventionResolver?.HiddenReturnBufferRegister(method) is { } bufferRegister
            && method.Locals.FirstOrDefault(l => l.Register.Number == bufferRegister.Number && l.Register.Version == -1) is { } bufferLocal)
        {
            bufferLocal.Name = "returnBuffer";
            bufferLocal.Type = method.ReturnType;
        }
    }

    public static void RemoveUnused(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        cfg.BuildUseDefLists();

        var usedLocals = new HashSet<LocalVariable>();

        foreach (var block in cfg.Blocks)
        {
            foreach (var usedVar in block.Use.OfType<LocalVariable>())
                usedLocals.Add(usedVar);

            foreach (var definedVar in block.Def.OfType<LocalVariable>())
                usedLocals.Add(definedVar);
        }

        method.Locals.RemoveAll(x => !usedLocals.Contains(x));
    }

    private static List<Register> GetRegisters(Instruction instruction)
    {
        var registers = new List<Register>();

        foreach (var operand in instruction.Operands)
        {
            if (operand is AddressOf { Target: Register addressed })
            {
                if (!registers.Contains(addressed))
                    registers.Add(addressed);
            }

            if (operand is Register register)
            {
                if (!registers.Contains(register))
                    registers.Add(register);
            }

            if (operand is MemoryOperand memory)
            {
                if (memory.Base != null)
                {
                    var baseRegister = (Register)memory.Base;
                    if (!registers.Contains(baseRegister))
                        registers.Add(baseRegister);
                }

                if (memory.Index != null)
                {
                    var index = (Register)memory.Index;
                    if (!registers.Contains(index))
                        registers.Add(index);
                }
            }
        }

        return registers;
    }

    /// <summary>
    /// Resolves field accesses and propagates types together, to a fixpoint, while the method is
    /// still in SSA form (every local has a single, version-stable definition).
    ///
    /// The two are mutually enabling and so cannot be ordered as separate passes: a typed base lets
    /// <see cref="MetadataResolver.ResolveFieldOffsets"/> turn <c>[base + offset]</c> into a
    /// <see cref="FieldReference"/>, a resolved field load types its result with the field's type,
    /// and that result is in turn the base of the next access (directly, or after flowing through
    /// moves/phis). Both steps are monotonic - each only ever resolves an operand or fills a
    /// previously-unknown type - so the loop converges.
    /// </summary>
    public static void ResolveTypesAndFields(MethodAnalysisContext method)
    {
        // Seed types from fixed ground truth - the method's own signature, and type-metadata global
        // loads. Applied once up front and, being applied first, they win over anything inferred later.
        // Structural, type-free, and it changes which locals are dereferenced, so it has to precede both the
        // seeds and DereferencedLocals below.
        if (FoldAddressArithmetic)
            FoldConstantAddressArithmetic(method);

        PropagateFromReturn(method);
        PropagateFromParameters(method);
        SeedRuntimeClassTypes(method);
        SeedNewobjResults(method);
        SeedMethodInfoTypes(method);
        SeedComparisonResults(method);
        SeedLiterals(method);

        // Everywhere there's a CallVoid after a Newobj, we can resolve the constructor call.
        MetadataResolver.ResolveConstructorCalls(method);

        // Everything else is mutually enabling and so runs to a fixpoint: a typed receiver lets an
        // ambiguous call resolve, a resolved call types its return value and arguments, a typed base
        // lets a field offset resolve, a field load types its result, and any of those can be the
        // receiver/base of the next step. Every pass is monotonic - it only resolves an operand or
        // fills a previously-unknown type - so the loop converges.
        // Locals that are dereferenced anywhere hold addresses, so numeric propagation must not
        // claim them (see DereferencedLocals). Computed once: the set only ever shrinks in meaning as
        // memory operands become field references, so using the initial, wider set stays conservative.
        var addressed = DereferencedLocals(method);

        var changed = true;
        var loopCount = 0;

        while (changed)
        {
            if (MaxTypePropagationLoopCount != -1 && ++loopCount > MaxTypePropagationLoopCount)
                throw new DecompilerException($"Type and field resolution not settling! (looped {MaxTypePropagationLoopCount} times)");

            changed = false;
            changed |= MetadataResolver.ResolveCallsViaMethodInfo(method);
            changed |= MetadataResolver.ResolveAmbiguousCalls(method);
            changed |= MetadataResolver.ResolveVirtualCalls(method);
            changed |= MetadataResolver.ResolveMethodInfoCalls(method);
            changed |= PropagateFromCallParameters(method);
            changed |= MetadataResolver.ResolveFieldOffsets(method);
            changed |= RgctxResolver.Run(method);
            changed |= PropagateStaticFieldStorage(method);
            changed |= TypeAddressedLocals(method);
            changed |= PropagateTypesOnce(method, addressed);
        }

        // Last resort, and only for locals nothing else could type. Everything that knows better - a field
        // store, a call argument, a receiver, a phi from a typed value - has already run to a fixpoint and
        // lost, so what is left is a local whose only definition is a bare constant. That is a number.
        // Deliberately last so it can never beat a real type, restricted to locals that are never
        // dereferenced, and followed by pure type propagation only: re-running the metadata resolvers here
        // could let a wrong guess resolve a field offset or a call against System.Int32 and stick forever.
        if (ConstantLocalsAreIntegers)
        {
            // Nothing derived from a constant may end up on a dereferenced local, whichever rule carries it
            // there - the seed skips them, but a plain copy would not. Blocked for the whole fallback.
            _blockedFromTyping = addressed;
            try
            {
                if (SeedRemainingConstantLocals(method, addressed))
                {
                    var propagationLoops = 0;
                    while (PropagateTypesOnce(method, addressed))
                        if (MaxTypePropagationLoopCount != -1 && ++propagationLoops > MaxTypePropagationLoopCount)
                            throw new DecompilerException($"Constant type propagation not settling! (looped {MaxTypePropagationLoopCount} times)");
                }
            }
            finally
            {
                _blockedFromTyping = null;
            }
        }

        if (UnifyCopies)
            UnifyContradictoryCopies(method);

        ReportUntypedLocalDefinitions(method, addressed);
    }



    /// <summary>
    /// Settles the case a monotonic fixpoint cannot: a copy whose two ends were typed by different rules
    /// and disagree. <see cref="SetTypeIfUnknown"/> never overwrites, so whichever seed fired first wins and
    /// the contradiction simply stands - `ldloc V_5 (string[]); stloc V_7 (IntPtr)` and 332 verifier errors
    /// in one method alone.
    ///
    /// Only one direction is decided, because only one is ever clear: a reference type comes from metadata -
    /// a field's declared type, a parameter, a call's return - while a primitive or native int on the other
    /// end of a plain copy is what numeric propagation guessed. So the reference wins and the numeric guess
    /// is replaced. Where both ends are references, or both numeric, nothing here can tell which is right
    /// and both are left alone.
    ///
    /// Runs once after the fixpoint has settled, never inside it: overwriting a type is not monotonic, and
    /// doing it during the loop would stop the loop terminating.
    /// </summary>
    private static void UnifyContradictoryCopies(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode is not (OpCode.Move or OpCode.Phi))
                continue;

            if (instruction.Operands is not [LocalVariable { Type: { } destinationType } destination, ..])
                continue;

            for (var i = 1; i < instruction.Operands.Count; i++)
            {
                if (instruction.Operands[i] is not LocalVariable { Type: { } sourceType } source)
                    continue;

                if (IsNumericGuess(destinationType) && IsMetadataReference(sourceType))
                    destination.Type = sourceType;
                else if (IsNumericGuess(sourceType) && IsMetadataReference(destinationType))
                    source.Type = destinationType;
            }
        }
    }

    // A number or a raw pointer: what numeric propagation produces when nothing else claimed the local.
    // Matched by name rather than by signature, because the analysis layer has no AsmResolver type yet -
    // ToTypeSignature only works once the assembly has been populated.
    private static bool IsNumericGuess(TypeAnalysisContext type) => type.FullName switch
    {
        "System.SByte" or "System.Byte" or "System.Int16" or "System.UInt16" or "System.Int32"
            or "System.UInt32" or "System.Int64" or "System.UInt64" or "System.Char" or "System.Boolean"
            or "System.Single" or "System.Double" or "System.IntPtr" or "System.UIntPtr" => true,
        _ => false,
    };

    // A reference type named by real metadata. The synthetic il2cpp types are excluded: each of those IS a
    // pointer, so it is no more authoritative than the native int it would be replacing.
    private static bool IsMetadataReference(TypeAnalysisContext type) =>
        type is { IsValueType: false } and not (RuntimeClassTypeAnalysisContext or RgctxTableTypeAnalysisContext
            or MethodRgctxTableTypeAnalysisContext or StaticFieldStorageTypeAnalysisContext
            or RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext or ByRefTypeAnalysisContext);
    /// <summary>
    /// Folds a constant interior-pointer computation back into the load that uses it: <c>[L + d]</c> where
    /// <c>L = B + k</c> addresses exactly <c>[B + (k + d)]</c>. il2cpp computes the interior pointer in its
    /// own instruction whenever an offset is reused, and the load through it then has a base that no
    /// inference can ever type - the local holds an address, not an object - so the field lookup never runs
    /// and the load degrades into an "Unmanaged memory load" marker. Restoring the
    /// <c>[object + field offset]</c> shape is what lets <see cref="MetadataResolver.ResolveFieldOffsets"/>
    /// see it at all. Measured as the largest single cause: of the untyped locals that are dereferenced,
    /// 20,795 are defined by an Add - more than any other definition.
    ///
    /// Purely structural - it reads no types - so it runs before the fixpoint, which means the dereferenced
    /// set is computed from the already-folded form. Only a local with exactly one definition is folded:
    /// with two, which one reaches the load is a question this cannot answer. The add is left in place; it
    /// may have other users, and removing an instruction here would mean editing the control flow graph.
    /// </summary>
    private static void FoldConstantAddressArithmetic(MethodAnalysisContext method)
    {
        var instructions = method.ControlFlowGraph!.Instructions;

        // Built from every definition, not just the adds: a local written a second time by anything at all
        // is ambiguous and must be dropped, so both kinds of write have to be seen here.
        Dictionary<LocalVariable, (LocalVariable Base, long Offset)>? interior = null;
        var defined = new HashSet<LocalVariable>();

        foreach (var instruction in instructions)
        {
            if (instruction.Destination is not LocalVariable destination)
                continue;

            if (!defined.Add(destination))
            {
                interior?.Remove(destination);
                continue;
            }

            if (instruction.OpCode is not (OpCode.Add or OpCode.Subtract))
                continue;

            if (instruction.Operands is not [_, LocalVariable addressBase, Immediate constant])
                continue;

            interior ??= new Dictionary<LocalVariable, (LocalVariable, long)>();
            interior[destination] = (addressBase, instruction.OpCode == OpCode.Add ? constant.Value : -constant.Value);
        }

        if (interior == null)
            return;

        var folded = new HashSet<LocalVariable>();

        foreach (var instruction in instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                if (instruction.Operands[i] is not MemoryOperand { Index: null, Scale: 0, Base: LocalVariable local } memory)
                    continue;

                if (!TryWalkToAddressRoot(interior, local, out var root, out var offset))
                    continue;

                memory.Base = root;
                memory.Addend += offset;
                instruction.SetOperand(i, memory);
                folded.Add(local);
            }
        }

        // Every interior local is a candidate, not just the one the load named: folding a chain leaves the
        // intermediate links unread too, and an add nothing reads is dead whether we folded it or not.
        if (RemoveFoldedAdds && folded.Count > 0)
            RemoveDeadAddressArithmetic(method, new HashSet<LocalVariable>(interior.Keys));
    }

    // Walks a chain of interior pointers (L2 = L1 + 8, L1 = B + 16) down to the object it is measured from.
    // Bounded rather than cycle-checked: a real chain is one or two links, and a loop in the graph must not
    // turn into a hang or an addend that grows without limit.
    private static bool TryWalkToAddressRoot(
        Dictionary<LocalVariable, (LocalVariable Base, long Offset)> interior,
        LocalVariable local, out LocalVariable root, out long offset)
    {
        root = local;
        offset = 0;

        for (var steps = 0; interior.TryGetValue(root, out var step); steps++)
        {
            if (steps >= 8)
                return false;

            offset += step.Offset;
            root = step.Base;
        }

        return !ReferenceEquals(root, local);
    }

    // An add that existed only to compute an address we just folded into the load is now dead, and left in
    // place it decompiles to `someObject + 24`, which is not valid C# - the single largest compile-error
    // shape in the output (2,900 methods: "Operator '+' cannot be applied to operands of type '<id>' and
    // 'int'"). Removed only once nothing reads the local any more, and removed from the owning block:
    // ControlFlowGraph.Instructions rebuilds a flat copy on every access, so deleting from it does nothing.
    private static void RemoveDeadAddressArithmetic(MethodAnalysisContext method, HashSet<LocalVariable> folded)
    {
        var stillRead = new HashSet<LocalVariable>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            // Operand 0 is the destination for everything that has one, except a call, whose operand 0 is
            // the target and whose return value is operand 1.
            var destinationIndex = instruction.OpCode is OpCode.Call or OpCode.IndirectCall ? 1 : 0;

            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                // A local in destination position is written, not read. A memory operand there is a store
                // *through* an address, which does read the base that computes it.
                if (i == destinationIndex && instruction.Operands[i] is LocalVariable)
                    continue;

                MarkLocalsRead(instruction.Operands[i], stillRead);
            }
        }

        folded.ExceptWith(stillRead);

        if (folded.Count == 0)
            return;

        foreach (var block in method.ControlFlowGraph.Blocks)
            block.Instructions.RemoveAll(instruction =>
                instruction.OpCode is OpCode.Add or OpCode.Subtract
                && instruction.Operands.Count > 0
                && instruction.Operands[0] is LocalVariable destination
                && folded.Contains(destination));
    }

    // Records every local an operand reads, following the ones that nest: a memory operand reads its base
    // and its index, an address-of reads its target.
    private static void MarkLocalsRead(IOperand? operand, HashSet<LocalVariable> stillRead)
    {
        switch (operand)
        {
            case LocalVariable local:
                stillRead.Add(local);
                break;
            case MemoryOperand memory:
                MarkLocalsRead(memory.Base, stillRead);
                MarkLocalsRead(memory.Index, stillRead);
                break;
            case AddressOf address:
                MarkLocalsRead(address.Target, stillRead);
                break;
        }
    }
    /// <summary>
    /// Types every local that is still unknown and whose every definition is a constant load.
    /// A local written once as `v = 5` and never stored to a field, passed as an argument or used as a
    /// receiver has nothing left that could name its type, and a bare constant in that position is an
    /// integer. Multiple definitions are all checked: one non-constant write and the local is skipped,
    /// because that write is the one carrying the real type. Dereferenced locals are skipped too - those
    /// hold addresses (see DereferencedLocals) and a numeric type on them is permanent and wrong.
    /// </summary>
    private static bool SeedRemainingConstantLocals(MethodAnalysisContext method, HashSet<LocalVariable> addressed)
    {
        Dictionary<LocalVariable, Immediate?>? constantOnly = null;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.Destination is not LocalVariable { Type: null } destination)
                continue;

            var constant = instruction.OpCode == OpCode.Move && instruction.Operands[1] is Immediate immediate
                ? immediate
                : (Immediate?)null;

            constantOnly ??= new Dictionary<LocalVariable, Immediate?>();

            // A second definition that is not a constant load disqualifies the local for good.
            if (constantOnly.TryGetValue(destination, out var seen))
                constantOnly[destination] = seen == null ? null : constant;
            else
                constantOnly[destination] = constant;
        }

        if (constantOnly == null)
            return false;

        var changed = false;

        foreach (var pair in constantOnly)
        {
            if (pair.Value is not { } value || addressed.Contains(pair.Key))
                continue;

            // A constant that does not fit in an int was loaded as a 64-bit value, so keep it one.
            var type = value.Value is >= int.MinValue and <= int.MaxValue
                ? method.AppContext.SystemTypes.SystemInt32Type
                : method.AppContext.SystemTypes.SystemInt64Type;

            changed |= SetTypeIfUnknown(pair.Key, type);
        }

        return changed;
    }

    // Diagnostic-only (env CPP2IL_TYPEDIAG=1). Locals still untyped once the fixpoint settles are the single
    // largest source of unrecoverable output - 63.555 of the 69.548 A4 markers - so tally what defines them.
    // A dominant entry names the propagation rule that is missing, rather than leaving it to be guessed at.
    // The second table is the one that maps onto A4: only a local that is *dereferenced* can be the base of
    // the memory load that emits the marker, so that subset - not the whole population - is what to fix to
    // remove markers. The whole population is what to fix to remove compile errors on `object`.
    // Zero cost when off.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> UntypedDefinedBy = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> UntypedBaseDefinedBy = new();
    private static int _typeDiagHooked;

    private static void ReportUntypedLocalDefinitions(MethodAnalysisContext method, HashSet<LocalVariable> addressed)
    {
        if (Environment.GetEnvironmentVariable("CPP2IL_TYPEDIAG") != "1")
            return;

        if (System.Threading.Interlocked.Exchange(ref _typeDiagHooked, 1) == 0)
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                Console.WriteLine("==== LOCALE FARA TIP, dupa instructiunea care le defineste ====");
                foreach (var kv in UntypedDefinedBy.OrderByDescending(k => k.Value).Take(20))
                    Console.WriteLine($"   {kv.Value,9}  {kv.Key}");

                Console.WriteLine("==== dintre ele, DOAR cele dereferentiate (baza A4) ====");
                foreach (var kv in UntypedBaseDefinedBy.OrderByDescending(k => k.Value).Take(20))
                    Console.WriteLine($"   {kv.Value,9}  {kv.Key}");
            };

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.Destination is not LocalVariable { Type: null } destination)
                continue;

            // Name the source as well as the opcode: a Move from a MemoryOperand and a Move from an
            // Immediate are different missing rules even though both are a Move.
            var source = instruction.Operands.Count > 1 && instruction.Operands[1] != null
                ? instruction.Operands[1].GetType().Name
                : "-";

            var key = $"{instruction.OpCode} <- {source}";
            UntypedDefinedBy.AddOrUpdate(key, 1, (_, v) => v + 1);

            if (addressed.Contains(destination))
                UntypedBaseDefinedBy.AddOrUpdate(key, 1, (_, v) => v + 1);
        }
    }

    // A type-metadata global load (Move local, typeof(T)) puts the runtime class pointer for T into
    // the local - an Il2CppClass*, not an instance of T. That is known exactly from the instruction,
    // so it is seeded as ground truth (overriding any prior guess) before the inference fixpoint,
    // rather than letting a monotonic pass first mistype the local as T itself.
    private static void SeedRuntimeClassTypes(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count < 2)
                continue;

            if (instruction.Operands[0] is LocalVariable destination
                && instruction.Operands[1] is TypeAnalysisContext type and not (RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext))
                destination.Type = new RuntimeClassTypeAnalysisContext(type, type.DeclaringAssembly);
        }
    }

    private static void SeedNewobjResults(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Newobj || instruction.Operands.Count < 2)
                continue;

            if (instruction.Operands[0] is LocalVariable destination && InstantiatedType(instruction.Operands[1]) is { } type)
                destination.Type = type;
        }
    }

    private static TypeAnalysisContext? InstantiatedType(IOperand classOperand) =>
        classOperand switch
        {
            LocalVariable { Type: RuntimeClassTypeAnalysisContext { RepresentedType: var t } } => t,
            RuntimeClassTypeAnalysisContext { RepresentedType: var t } => t,
            TypeAnalysisContext type => type, //not sure this is actually valid but for completeness
            _ => null,
        };

    // A method/field-metadata global load (Move local, methodof(M) / fieldof(F)) puts a MethodInfo*
    // or FieldInfo* into the local. MetadataResolver already resolved the address to a context naming
    // the member; that same context is the local's type (a runtime handle, recoverable via its
    // RepresentedMethod/RepresentedField).
    private static void SeedMethodInfoTypes(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count < 2)
                continue;

            if (instruction.Operands[0] is LocalVariable destination
                && instruction.Operands[1] is RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext)
                destination.Type = (TypeAnalysisContext)instruction.Operands[1];
        }
    }

    // A comparison (CheckEqual, CheckLess, ...) writes a 0/1 result into its destination, so that local
    // is a System.Boolean regardless of what the compared operands are.
    private static void SeedComparisonResults(MethodAnalysisContext method)
    {
        var booleanType = method.AppContext.SystemTypes.SystemBooleanType;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode is < OpCode.CheckEqual or > OpCode.CheckLessOrEqual)
                continue;

            if (instruction.Destination is LocalVariable destination)
                destination.Type = booleanType;
        }
    }
    
    //Handles typing of locals for ref/out params. Returns whether anything new was typed
    public static bool TypeAddressedLocals(MethodAnalysisContext method)
    {
        var changed = false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (!instruction.IsCall || instruction.Operands[0] is not MethodAnalysisContext calledMethod)
                continue;

            var firstArg = instruction.OpCode == OpCode.CallVoid ? 1 : 2;

            // the receiver of a value type's instance method is a pointer to the value
            if (!calledMethod.IsStatic && firstArg < instruction.Operands.Count
                && instruction.Operands[firstArg] is AddressOf { Target: LocalVariable receiver }
                && calledMethod.DeclaringType is { IsValueType: true } declaringType)
                changed |= SetTypeIfUnknown(receiver, declaringType);

            var paramOffset = firstArg + (calledMethod.IsStatic ? 0 : 1);

            for (var i = paramOffset; i < instruction.Operands.Count; i++)
            {
                var parameterIndex = i - paramOffset;
                if (parameterIndex > calledMethod.Parameters.Count - 1) // Probably MethodInfo*
                    continue;

                if (instruction.Operands[i] is AddressOf { Target: LocalVariable referenced }
                    && calledMethod.Parameters[parameterIndex].ParameterType is ByRefTypeAnalysisContext { ElementType: { } referencedType })
                    changed |= SetTypeIfUnknown(referenced, referencedType);
            }
        }

        return changed;
    }

    // Fills in a local's type only when it is currently unknown, keeping propagation monotonic (a
    // type, once set, is never changed) so the fixpoint terminates. Returns whether it set anything.
    private static bool SetTypeIfUnknown(LocalVariable local, TypeAnalysisContext? type)
    {
        if (type == null || local.Type != null)
            return false;

        // Set only during the constant fallback: nothing inferred from a bare constant may reach a local
        // that is dereferenced somewhere. Those hold addresses, so a number there is not a missing type but
        // a wrong one - strictly worse, because the field lookup then runs against System.Int32 and fails
        // for good instead of staying recoverable. Copies (Move, Phi) are how it would leak, and they
        // deliberately do not consult `addressed` themselves, so the block lives here where every rule meets.
        if (_blockedFromTyping != null && _blockedFromTyping.Contains(local))
            return false;

        local.Type = type;
        return true;
    }

    private static bool PropagateStaticFieldStorage(MethodAnalysisContext method)
    {
        var staticFieldsOffset = method.AppContext.Binary.is32Bit ? StaticFieldsOffset32 : StaticFieldsOffset64;
        var changed = false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count < 2)
                continue;

            if (instruction.Operands[0] is not LocalVariable destination || destination.Type is StaticFieldStorageTypeAnalysisContext)
                continue;

            if (instruction.Operands[1] is not MemoryOperand { Index: null, Scale: 0 } memory || memory.Addend != staticFieldsOffset)
                continue;

            if (memory.Base is not LocalVariable { Type: RuntimeClassTypeAnalysisContext { RepresentedType: var owner } })
                continue;

            destination.Type = new StaticFieldStorageTypeAnalysisContext(owner, owner.DeclaringAssembly);
            changed = true;
        }

        return changed;
    }

    // A single propagation sweep over every move and phi. Returns whether it filled in any type.
    private static bool PropagateTypesOnce(MethodAnalysisContext method, HashSet<LocalVariable> addressed)
    {
        var changed = false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            switch (instruction.OpCode)
            {
                case OpCode.Move:
                    changed |= PropagateMove(instruction, method.AppContext.Binary.PointerSizeBytes);
                    break;
                case OpCode.Phi:
                    changed |= PropagatePhi(instruction);
                    break;
                case OpCode.Add or OpCode.Subtract or OpCode.Multiply:
                    changed |= PropagateArithmetic(instruction, method, addressed);
                    // Off by default: add/sub/mul on a pointer is address arithmetic, and a wrong numeric
                    // type is permanent, so this is opt-in until measured. The `addressed` guard already
                    // excludes every local that is dereferenced, which is most of the address arithmetic.
                    if (IntegerArithmetic)
                        changed |= PropagateIntegerResult(instruction, method, addressed);
                    break;
                case OpCode.Divide or OpCode.Modulo:
                    changed |= PropagateArithmetic(instruction, method, addressed) || PropagateIntegerResult(instruction, method, addressed);
                    break;
                case OpCode.And or OpCode.Or or OpCode.Xor or OpCode.Not or OpCode.Negate
                    or OpCode.ShiftLeft or OpCode.ShiftRight:
                    changed |= PropagateIntegerResult(instruction, method, addressed);
                    break;
            }
        }

        return changed;
    }

    /// <summary>
    /// Locals that are dereferenced somewhere in the method - used as the base of a memory operand.
    /// Such a local holds an address, so giving it a numeric type is always wrong, and because
    /// propagation is monotonic that wrong type would then be permanent: every field read through it
    /// would look for an offset in (say) System.Int32's layout and never resolve. Arithmetic on a
    /// pointer is address arithmetic, so those passes skip these locals and leave them untyped for a
    /// pass that can type them properly.
    /// </summary>
    private static HashSet<LocalVariable> DereferencedLocals(MethodAnalysisContext method)
    {
        var addressed = new HashSet<LocalVariable>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        foreach (var operand in instruction.Operands)
            if (operand is MemoryOperand { Base: LocalVariable baseLocal })
                addressed.Add(baseLocal);

        return addressed;
    }

    // A local assigned a literal is that literal.s type: a float/double (a lifted rodata constant load),
    // or a string literal, which is unambiguously System.String and needs no inference at all.
    private static void SeedLiterals(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands[0] is not LocalVariable destination)
                continue;

            destination.Type = instruction.Operands[1] switch
            {
                FloatLiteral => method.AppContext.SystemTypes.SystemSingleType,
                DoubleLiteral => method.AppContext.SystemTypes.SystemDoubleType,
                StringLiteral => method.AppContext.SystemTypes.SystemStringType,
                _ => destination.Type,
            };
        }
    }

    // Arithmetic on a float operand is float arithmetic, so the result is that float type.
    private static bool PropagateArithmetic(Instruction instruction, MethodAnalysisContext method, HashSet<LocalVariable> addressed)
    {
        if (instruction.Operands is not [LocalVariable { Type: null } destination, var left, var right])
            return false;

        if (addressed.Contains(destination))
            return false;

        if ((FloatOperandType(left, method) ?? FloatOperandType(right, method)) is not { } floatType)
            return false;

        return SetTypeIfUnknown(destination, floatType);
    }

    // An integer operand makes the result an integer. Excludes bool operands so flag logic stays boolean.
    private static bool PropagateIntegerResult(Instruction instruction, MethodAnalysisContext method, HashSet<LocalVariable> addressed)
    {
        if (instruction.Operands[0] is not LocalVariable { Type: null } destination)
            return false;

        if (addressed.Contains(destination))
            return false;

        var sawImmediate = false;

        for (var i = 1; i < instruction.Operands.Count; i++)
        {
            var operand = instruction.Operands[i];

            if (IntegerResultType(operand, method) is { } integerType)
                return SetTypeIfUnknown(destination, integerType);

            // An operand with a known type that is not an integer means this is not integer maths at all -
            // it is address arithmetic, float maths or a flag test - so the constant below must not claim it.
            if (operand is LocalVariable { Type: not null })
                return false;

            sawImmediate |= operand is Immediate;
        }

        // No operand carried a type, but one was a literal constant. Maths against a constant is integer
        // maths unless another operand said otherwise, and none did - the loop above would have returned.
        // This is what types the long tail of `x = y & 0xff` / `x = y >> 2` whose input never got a type.
        return sawImmediate && SetTypeIfUnknown(destination, method.AppContext.SystemTypes.SystemInt32Type);
    }

    private static TypeAnalysisContext? IntegerResultType(IOperand operand, MethodAnalysisContext method)
    {
        var type = operand switch
        {
            LocalVariable { Type: { } localType } => localType,
            FieldReference field => field.ResultType,
            _ => null,
        };

        return type?.FullName switch
        {
            "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16"
                or "System.Int32" or "System.UInt32" or "System.Char" => method.AppContext.SystemTypes.SystemInt32Type,
            "System.Int64" or "System.UInt64" => method.AppContext.SystemTypes.SystemInt64Type,
            _ => null,
        };
    }

    private static TypeAnalysisContext? FloatOperandType(IOperand operand, MethodAnalysisContext method) =>
        operand switch
        {
            FloatLiteral => method.AppContext.SystemTypes.SystemSingleType,
            DoubleLiteral => method.AppContext.SystemTypes.SystemDoubleType,
            LocalVariable { Type: { FullName: "System.Single" } single } => single,
            LocalVariable { Type: { FullName: "System.Double" } @double } => @double,
            _ => null,
        };

    private static bool PropagateMove(Instruction move, int pointerSize)
    {
        var destination = move.Operands[0];
        var source = move.Operands[1];

        // Move local, local: copy a known type in whichever direction is missing it.
        if (destination is LocalVariable destLocal && source is LocalVariable sourceLocal)
            return SetTypeIfUnknown(destLocal, sourceLocal.Type) || SetTypeIfUnknown(sourceLocal, destLocal.Type);

        // Move local, field: a field load types its result with the field's type. This is the edge
        // that lets the loaded value go on to be the base of a further field access.
        if (destination is LocalVariable loadDest && source is FieldReference loadField)
            return SetTypeIfUnknown(loadDest, loadField.Field.FieldType);

        // Move field, local: a field store types the stored value with the field's type.
        if (destination is FieldReference storeField && source is LocalVariable storeSource)
            return SetTypeIfUnknown(storeSource, storeField.Field.FieldType);

        // An element of T[] is a T, whether we loaded it (reference arrays) or only computed its address
        if (destination is LocalVariable { Type: null } elementDest
            && source is MemoryOperand { Base: LocalVariable { Type: SzArrayTypeAnalysisContext { ElementType: { } elementType } } } elementAccess
            && (elementAccess.Index != null || elementAccess.Addend >= 4L * pointerSize))
            return SetTypeIfUnknown(elementDest, elementType);

        // Move local, [byref]: dereferencing a managed pointer to a reference type yields that referent
        // (a struct byref accesses fields directly with no deref, so this only fires for class referents).
        if (destination is LocalVariable { Type: null } derefDest
            && source is MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable { Type: ByRefTypeAnalysisContext { ElementType: { IsValueType: false } referent } } })
            return SetTypeIfUnknown(derefDest, referent);

        // Move local, [obj]: offset 0 of a reference-typed value is its klass pointer.
        if (destination is LocalVariable { Type: null } klassDest
            && source is MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable { Type: { } baseType } }
            && baseType is not (RuntimeClassTypeAnalysisContext or StaticFieldStorageTypeAnalysisContext or RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext or ByRefTypeAnalysisContext)
            && !baseType.IsValueType)
            return SetTypeIfUnknown(klassDest, new RuntimeClassTypeAnalysisContext(baseType, baseType.DeclaringAssembly));

        return false;
    }

    // A phi is a copy from each predecessor's value, so types flow both ways across it - mirroring
    // the bidirectional Move copies it decays into once SSA is destroyed.
    private static bool PropagatePhi(Instruction phi)
    {
        if (phi.Operands[0] is not LocalVariable destination)
            return false;

        var changed = false;

        // Forward: an untyped phi result takes the type of any typed input.
        if (destination.Type == null)
        {
            for (var i = 1; i < phi.Operands.Count; i++)
            {
                if (phi.Operands[i] is LocalVariable { Type: { } inputType })
                {
                    changed = SetTypeIfUnknown(destination, inputType);
                    break;
                }
            }
        }

        // Backward: a typed phi result types each of its still-untyped inputs.
        if (destination.Type != null)
        {
            for (var i = 1; i < phi.Operands.Count; i++)
            {
                if (phi.Operands[i] is LocalVariable input)
                    changed |= SetTypeIfUnknown(input, destination.Type);
            }
        }

        return changed;
    }

    private static bool PropagateFromCallParameters(MethodAnalysisContext method)
    {
        var changed = false;

        // A lea and the call it's passed to are still separate here. The address only gets folded into the
        // call later, so an argument's address-of has to be found through the local carrying it.
        var addressesOf = new Dictionary<LocalVariable, LocalVariable>();
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode == OpCode.Move
                && instruction.Operands[0] is LocalVariable pointer
                && instruction.Operands[1] is AddressOf { Target: LocalVariable pointee })
                addressesOf[pointer] = pointee;
        }

        LocalVariable? Addressed(IOperand operand) => operand switch
        {
            AddressOf { Target: LocalVariable direct } => direct,
            LocalVariable local when addressesOf.TryGetValue(local, out var indirect) => indirect,
            _ => null
        };

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (!instruction.IsCall)
                continue;

            if (instruction.Operands[0] is not MethodAnalysisContext calledMethod)
                continue;

            // Return value: a constructor yields its declaring type, otherwise the declared return type.
            if (instruction.Destination is LocalVariable returnValue)
            {
                var producedType = calledMethod.Name is ".ctor" or ".cctor" ? calledMethod.DeclaringType : calledMethod.ReturnType;

                if (producedType != method.AppContext.SystemTypes.SystemVoidType)
                    changed |= SetTypeIfUnknown(returnValue, producedType);
            }


            // Call operands
            // 0. Target
            // 1. ReturnValue
            // 2. thisParam
            // ... parameters

            // CallVoid operands
            // 0. Target
            // 1. thisParam
            // ... parameters
            var thisParamIndex = instruction.OpCode == OpCode.CallVoid ? 1 : 2;

            // 'this' param
            if (!calledMethod.IsStatic
                && instruction.Operands[thisParamIndex] is LocalVariable thisParam)
            {
                changed |= SetTypeIfUnknown(thisParam, calledMethod.DeclaringType);
            }

            // Value type instance method, first arg is address of value, but we need to type the value
            if (!calledMethod.IsStatic
                && Addressed(instruction.Operands[thisParamIndex]) is { } addressedReceiver
                && calledMethod.DeclaringType is { IsValueType: true } valueType)
            {
                changed |= SetTypeIfUnknown(addressedReceiver, valueType);
            }

            // Remaining arguments map positionally onto the callee's declared parameters.
            var paramOffset = calledMethod.IsStatic ? 1 : 2;
            if (instruction.OpCode == OpCode.Call) // Skip the return value operand
                paramOffset += 1;

            for (var i = paramOffset; i < instruction.Operands.Count; i++)
            {
                var parameterIndex = i - paramOffset;
                if (parameterIndex > calledMethod.Parameters.Count - 1) // Probably MethodInfo*
                    continue;

                var parameterType = calledMethod.Parameters[parameterIndex].ParameterType;

                // A by-ref parameter says how the argument is passed, not what the argument holds. Where we
                // found the address-of, the local it names is the variable being passed, so it holds the
                // referent - that is the type worth learning.
                //
                // Where we did not, the operand is only the register il2cpp computed the pointer in, and
                // claiming T& for it is the one thing that must not happen: C# has no way to write a byref
                // local that is not bound to an existing variable, so every definition of it decompiles to
                // `ref T x = <value>;` - CS8172, and CS1510 on the same line, 546 and 416 methods. The
                // by-ref-ness belongs at the call site instead, where LoadOperand now puts it back.
                if (parameterType is ByRefTypeAnalysisContext { ElementType: { } referencedType })
                {
                    if (Addressed(instruction.Operands[i]) is { } referenced)
                        changed |= SetTypeIfUnknown(referenced, referencedType);
                    else if (!IlGenerator.ByRefAtCallSite && instruction.Operands[i] is LocalVariable pointerRegister)
                        changed |= SetTypeIfUnknown(pointerRegister, parameterType);

                    continue;
                }

                // The mirror case: a value type larger than a register is passed indirectly by the ABI even
                // when the parameter is declared by value, so an address-of here names a variable holding
                // exactly the parameter's own type.
                if (IlGenerator.ByRefAtCallSite && instruction.Operands[i] is AddressOf { Target: LocalVariable indirectlyPassed })
                {
                    if (parameterType.IsValueType)
                        changed |= SetTypeIfUnknown(indirectlyPassed, parameterType);

                    continue;
                }

                if (instruction.Operands[i] is LocalVariable local)
                    changed |= SetTypeIfUnknown(local, parameterType);
            }
        }

        return changed;
    }

    private static void PropagateFromParameters(MethodAnalysisContext method)
    {
        // 'this'
        if (!method.IsStatic)
        {
            var thisLocal = method.ParameterLocals.FirstOrDefault(p => p.IsThis);
            if (thisLocal != null)
                thisLocal.Type = method.DeclaringType is { GenericParameters.Count: > 0 } generic
                    ? new GenericInstanceTypeAnalysisContext(generic, generic.GenericParameters)
                    : method.DeclaringType;
        }

        if (method.ParameterLocals.FirstOrDefault(p => p.IsMethodInfo) is { } methodInfoLocal && method.DeclaringType is { } owner)
            methodInfoLocal.Type = new RuntimeMethodInfoAnalysisContext(method, owner.DeclaringAssembly);

        if (method.Parameters.Count == 0)
            return;

        // Normal params
        var paramIndex = 0;
        foreach (var local in method.ParameterLocals)
        {
            if (local.IsThis || local.IsMethodInfo)
                continue;

            if (paramIndex >= method.Parameters.Count)
                break;

            local.Type = method.Parameters[paramIndex].ParameterType;
            paramIndex++;
        }
    }

    private static void PropagateFromReturn(MethodAnalysisContext method)
    {
        var returns = method.ControlFlowGraph!.Instructions.Where(i => i.OpCode == OpCode.Return);

        foreach (var instruction in returns)
        {
            if (instruction.Operands.Count == 1 && instruction.Operands[0] is LocalVariable local)
                local.Type = method.ReturnType;
        }
    }
}
