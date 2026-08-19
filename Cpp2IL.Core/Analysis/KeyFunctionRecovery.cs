using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Il2CppApiFunctions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Maps calls to KeyFunctionAddresses to their underlying IL opcodes. E.g. il2cpp_codegen_object_new => newobj.
/// Eventually will include box/unbox/throw/etc
/// </summary>
// Diagnostic-only (env CPP2IL_KFDIAG=1): why key-function calls do/don't get rewritten.
internal static class KfDiag
{
    public static readonly bool Enabled = System.Environment.GetEnvironmentVariable("CPP2IL_KFDIAG") == "1";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long[]> Tally = new();
    private static int _hooked;
    // slots: 0=seen, 1=opcodeCall, 2=opcodeCallVoid, 3=opcodeOther, 4=notRewritten
    private static long[] Slot(string k) => Tally.GetOrAdd(k, _ => new long[5]);

    private static void Hook()
    {
        if (System.Threading.Interlocked.Exchange(ref _hooked, 1) != 0) return;
        System.AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            System.Console.WriteLine("==== KEY-FUNCTION DIAG (name: seen call/void/other notRewritten) ====");
            foreach (var kv in Tally.OrderByDescending(k => k.Value[4]))
                System.Console.WriteLine($"  {kv.Key,-45} seen={kv.Value[0],7} call={kv.Value[1],7} void={kv.Value[2],7} other={kv.Value[3],5} NOTrw={kv.Value[4],7}");
        };
    }

    public static void Seen(string name, Instruction ins)
    {
        Hook();
        var s = Slot(name);
        System.Threading.Interlocked.Increment(ref s[0]);
        var slot = ins.OpCode == OpCode.Call ? 1 : ins.OpCode == OpCode.CallVoid ? 2 : 3;
        System.Threading.Interlocked.Increment(ref s[slot]);
    }

    public static void NotRewritten(string name, Instruction ins) =>
        System.Threading.Interlocked.Increment(ref Slot(name)[4]);
}

public static class KeyFunctionRecovery
{
    //All of these have the same params in the same order so we treat them as equal.
    private static readonly HashSet<string> ObjectNewFunctions =
    [
        "il2cpp_object_new",
        "il2cpp_vm_object_new",
        "il2cpp_codegen_object_new",
    ];

    //These all take the exception to throw as their only real argument.
    private static readonly HashSet<string> RaiseExceptionFunctions =
    [
        nameof(BaseKeyFunctionAddresses.il2cpp_raise_exception),
        nameof(BaseKeyFunctionAddresses.il2cpp_vm_exception_raise),
        nameof(BaseKeyFunctionAddresses.il2cpp_codegen_raise_exception),
    ];

    //Both take the class to box as and a pointer to the value.
    private static readonly HashSet<string> BoxFunctions =
    [
        nameof(BaseKeyFunctionAddresses.il2cpp_value_box),
        nameof(BaseKeyFunctionAddresses.il2cpp_vm_object_box),
    ];

    //All take the array class and a length.
    private static readonly HashSet<string> ArrayNewFunctions =
    [
        nameof(BaseKeyFunctionAddresses.il2cpp_array_new_specific),
        nameof(BaseKeyFunctionAddresses.il2cpp_vm_array_new_specific),
        nameof(BaseKeyFunctionAddresses.SzArrayNew),
    ];

    public static void Run(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Blocks.SelectMany(block => block.Instructions))
        {
            if (instruction.Operands is not [StringLiteral { Value: var keyFunction }, ..])
                continue;

            if (KfDiag.Enabled) KfDiag.Seen(keyFunction, instruction);

            if (ObjectNewFunctions.Contains(keyFunction))
                RewriteObjectNew(instruction, method);
            else if (keyFunction == nameof(BaseKeyFunctionAddresses.il2cpp_codegen_write_barrier))
                RemoveWriteBarrier(instruction);
            else if (RaiseExceptionFunctions.Contains(keyFunction))
                RewriteRaiseException(instruction);
            else if (keyFunction == nameof(BaseKeyFunctionAddresses.il2cpp_vm_reflection_get_type_object))
                RewriteTypeObject(instruction);
            else if (keyFunction == nameof(BaseKeyFunctionAddresses.InternalCalls_Resolve))
                RewriteInternalCallResolve(instruction, method);

            if (KfDiag.Enabled && instruction.Operands is [StringLiteral, ..])
                KfDiag.NotRewritten(keyFunction, instruction);
        }
    }

    /// <summary>
    /// Key-function rewrites that need the argument's resolved managed type: box (the boxed value
    /// type), array allocation (the element type), and the 'as'/'is' cast (the target type). These
    /// run AFTER the type/field fixpoint, while still in SSA form, so the class-pointer argument is
    /// typed and a void call's return register is still tracked by ImplicitDefinition.
    /// </summary>
    public static void RunPostTyping(MethodAnalysisContext method)
    {
        // The class-pointer argument to box/array/cast is a runtime Il2CppClass* that type propagation
        // leaves untyped, so we trace it back through SSA copies to the metadata load that names the
        // managed type. Build the single-assignment definition map once for that walk.
        var defs = new System.Collections.Generic.Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.Destination is LocalVariable dst)
                defs[dst] = instruction;

        foreach (var instruction in method.ControlFlowGraph.Blocks.SelectMany(block => block.Instructions))
        {
            if (instruction.Operands is not [StringLiteral { Value: var keyFunction }, ..])
                continue;

            if (BoxFunctions.Contains(keyFunction))
                RewriteBox(instruction, method, defs);
            else if (ArrayNewFunctions.Contains(keyFunction))
                RewriteArrayNew(instruction, method, defs);
            else if (keyFunction == nameof(BaseKeyFunctionAddresses.il2cpp_vm_object_is_inst))
                RewriteIsInst(instruction, method, defs);
        }
    }

    // Resolves a class-pointer operand to the managed type it names, following SSA move-copies back to
    // the metadata load ResolveMetadataUsages already turned into a type operand.
    private static TypeAnalysisContext? ResolveClassType(IOperand? op, System.Collections.Generic.Dictionary<LocalVariable, Instruction> defs, int depth = 0)
    {
        switch (op)
        {
            case RuntimeClassTypeAnalysisContext { RepresentedType: { } t }:
                return t;
            case RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext or StaticFieldStorageTypeAnalysisContext:
                return null;
            case TypeAnalysisContext t:
                return t;
            case LocalVariable lv:
                if (lv.Type is RuntimeClassTypeAnalysisContext { RepresentedType: { } lt })
                    return lt;
                if (depth < 8 && defs.TryGetValue(lv, out var def) && def.OpCode == OpCode.Move && def.Operands.Count >= 2)
                    return ResolveClassType(def.Operands[1], defs, depth + 1);
                return null;
            default:
                return null;
        }
    }

    // il2cpp_vm_object_is_inst(obj, targetClass) is the runtime cast used by C# 'as'/'is' and the
    // reference-type path of '(T)obj'. It returns the object typed as the target (or null). We model
    // it as a move and, crucially, type the result as the target type, so field accesses on the cast
    // result resolve against T's layout instead of the pre-cast (base) type. Without this the call
    // is emitted as an "unknown call target" marker AND the result stays under-typed, which is the
    // single biggest source of unresolved [base + offset] loads.
    private static void RewriteIsInst(Instruction instruction, MethodAnalysisContext method, System.Collections.Generic.Dictionary<LocalVariable, Instruction> defs)
    {
        // args: the object being cast, then the target class pointer.
        if (CallShape(instruction, method) is not (LocalVariable result, [var obj, var target, ..]))
            return;

        if (ResolveClassType(target, defs) is not { } targetType)
            return;

        // Type the result to the cast target so field accesses on it resolve against T's layout.
        result.Type = targetType;

        instruction.OpCode = OpCode.Move;
        instruction.SetOperands(result, obj);
        instruction.ImplicitDefinition = null;
    }

    // The cast target argument can arrive as the type directly, as a runtime class pointer, or as a
    // local carrying either - unwrap all three to the represented managed type.
    private static TypeAnalysisContext? CastTargetType(IOperand operand) => operand switch
    {
        RuntimeClassTypeAnalysisContext { RepresentedType: { } t } => t,
        LocalVariable { Type: RuntimeClassTypeAnalysisContext { RepresentedType: { } t } } => t,
        RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext or StaticFieldStorageTypeAnalysisContext => null,
        TypeAnalysisContext type => type,
        _ => null,
    };

    private static void RemoveWriteBarrier(Instruction instruction)
    {
        instruction.OpCode = OpCode.Nop;
        instruction.SetOperands();
    }
    
    private static void RewriteRaiseException(Instruction instruction)
    {
        // A void call has no return value operand, so the exception is one slot earlier
        var exceptionIndex = instruction.OpCode == OpCode.CallVoid ? 1 : 2;

        if (!instruction.IsCall || instruction.Operands.Count <= exceptionIndex)
            return;

        var exception = instruction.Operands[exceptionIndex];

        instruction.OpCode = OpCode.Throw;
        instruction.SetOperands(exception);
    }

    // Splits a key-function call into (result, args), transparently handling the CallVoid shape whose
    // return value is carried by ImplicitDefinition rather than a result operand.
    //   Call     : [name, result, arg0, arg1, ...]
    //   CallVoid : [name, arg0, arg1, ...]  (+ ImplicitDefinition = the return-register local)
    private static (IOperand? result, System.Collections.Generic.List<IOperand> args)? CallShape(Instruction instruction, MethodAnalysisContext method)
    {
        if (instruction.OpCode == OpCode.Call && instruction.Operands.Count >= 2)
            return (instruction.Operands[1], instruction.Operands.Skip(2).ToList());
        if (instruction.OpCode == OpCode.CallVoid && instruction.Operands.Count >= 1)
            return (ImplicitResultLocal(instruction, method), instruction.Operands.Skip(1).ToList());
        return null;
    }

    private static void RewriteBox(Instruction instruction, MethodAnalysisContext method, System.Collections.Generic.Dictionary<LocalVariable, Instruction> defs)
    {
        // args: the boxed value type (as a type or a runtime class pointer), then a pointer to the value.
        if (CallShape(instruction, method) is not ({ } result, [var typeArg, var value, ..]))
            return;

        if (ResolveClassType(typeArg, defs) is not { } boxedType)
            return;

        instruction.OpCode = OpCode.Box;
        instruction.SetOperands(result, boxedType, value);
        instruction.ImplicitDefinition = null;
    }

    private static void RewriteArrayNew(Instruction instruction, MethodAnalysisContext method, System.Collections.Generic.Dictionary<LocalVariable, Instruction> defs)
    {
        // args: the (single-dimensional) array type, then the element count.
        if (CallShape(instruction, method) is not ({ } result, [var arrayOperand, var length, ..]))
            return;

        if (ResolveClassType(arrayOperand, defs) is not SzArrayTypeAnalysisContext arrayType)
            return;

        // The fixpoint types newobj results but not newarr, so type the array local here (correctly,
        // from the allocation) to help downstream element accesses resolve.
        if (result is LocalVariable arrayLocal)
            arrayLocal.Type = arrayType;

        instruction.OpCode = OpCode.NewArr;
        instruction.SetOperands(result, arrayType, length);
        instruction.ImplicitDefinition = null;
    }

    // The array-allocation argument names the array type (directly, via a runtime class pointer, or a
    // local carrying either). IlGenerator's newarr needs the szarray type itself, so unwrap to it.
    private static SzArrayTypeAnalysisContext? ArrayType(IOperand op) => op switch
    {
        SzArrayTypeAnalysisContext sz => sz,
        RuntimeClassTypeAnalysisContext { RepresentedType: SzArrayTypeAnalysisContext sz } => sz,
        LocalVariable { Type: SzArrayTypeAnalysisContext sz } => sz,
        LocalVariable { Type: RuntimeClassTypeAnalysisContext { RepresentedType: SzArrayTypeAnalysisContext sz } } => sz,
        _ => null,
    };

    private static void RewriteTypeObject(Instruction instruction)
    {
        if (instruction.OpCode != OpCode.Call || instruction.Operands is not [_, var result, TypeAnalysisContext type, ..])
            return;

        instruction.OpCode = OpCode.Move;
        instruction.SetOperands(result, type);
    }

    private static void RewriteInternalCallResolve(Instruction instruction, MethodAnalysisContext method)
    {
        if (instruction.OpCode != OpCode.Call || instruction.Operands is not [_, var result, Immediate nameAddress, ..])
            return;

        if (ThrowHelperRecovery.ReadCStringAtVirtualAddress(method.AppContext, nameAddress.UnsignedValue, 256) is not { } name)
            return;

        if (ResolveInternalCallName(method.AppContext, name) is not { DeclaringType.DeclaringAssembly: { } assembly } resolved)
            return;

        var pointer = new RuntimeMethodInfoAnalysisContext(resolved, assembly);

        instruction.OpCode = OpCode.Move;
        instruction.SetOperands(result, pointer);

        RewriteCachedPointerLoads(method, result, pointer);
    }

    private static void RewriteCachedPointerLoads(MethodAnalysisContext method, IOperand result, RuntimeMethodInfoAnalysisContext pointer)
    {
        var cache = method.ControlFlowGraph!.Instructions
            .Where(i => i is { OpCode: OpCode.Move, Operands: [MemoryOperand { IsConstant: true }, _] })
            .Where(i => ReferenceEquals(i.Operands[1], result))
            .Select(i => ((MemoryOperand)i.Operands[0]).Addend)
            .Distinct()
            .ToList();

        if (cache.Count != 1)
            return;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable destination, MemoryOperand { IsConstant: true } load] }
                && load.Addend == cache[0])
                instruction.SetOperands(destination, pointer);
        }
    }

    internal static MethodAnalysisContext? ResolveInternalCallName(ApplicationAnalysisContext appContext, string name)
    {
        var separator = name.IndexOf("::", StringComparison.Ordinal);

        if (separator < 0)
            return null;

        var typeName = name[..separator];
        var signature = name[(separator + 2)..];
        var parenthesis = signature.IndexOf('(');
        var methodName = parenthesis < 0 ? signature : signature[..parenthesis];

        if (appContext.LibCpp2IlContext.ReflectionCache.GetTypeByFullName(typeName) is not { } typeDefinition
            || appContext.ResolveContextForType(typeDefinition) is not { } type)
            return null;

        var candidates = type.Methods.Where(m => m.Name == methodName).ToList();

        if (candidates.Count <= 1)
            return candidates.FirstOrDefault();

        // Overloaded, so fall back on the parameter list in the name
        var parameters = parenthesis < 0 ? "" : signature[(parenthesis + 1)..].TrimEnd(')');
        var count = parameters.Length == 0 ? 0 : parameters.Split(',').Length;

        var byParameterCount = candidates.Where(m => m.Parameters.Count == count).ToList();

        return byParameterCount.Count == 1 ? byParameterCount[0] : null;
    }

    private static void RewriteObjectNew(Instruction instruction, MethodAnalysisContext method)
    {
        // il2cpp_codegen_object_new's address usually resolves to a void managed thunk, so the lifter
        // emits it as a CallVoid whose return (the new object, in rax) is only carried by the call's
        // ImplicitDefinition rather than a result operand. Handle both shapes:
        //   Call     : [name, result, klass, ...]
        //   CallVoid : [name, klass, ...]  with the new object in ImplicitDefinition
        IOperand? result, klass;
        if (instruction.OpCode == OpCode.Call && instruction.Operands.Count >= 3)
        {
            result = instruction.Operands[1];
            klass = instruction.Operands[2];
        }
        else if (instruction.OpCode == OpCode.CallVoid && instruction.Operands.Count >= 2
                 && ImplicitResultLocal(instruction, method) is { } implicitResult)
        {
            result = implicitResult;
            klass = instruction.Operands[1];
        }
        else
            return;

        instruction.OpCode = OpCode.Newobj;
        instruction.SetOperands(result, klass);
        instruction.ImplicitDefinition = null; // the result is now an explicit operand
    }

    // The local carrying a void-call's return value: the call clobbers the return register, and SSA
    // renamed that clobber to a fresh version that the downstream reads use. Find the local for it.
    internal static LocalVariable? ImplicitResultLocal(Instruction instruction, MethodAnalysisContext method)
    {
        if (instruction.ImplicitDefinition is not { } clobbered)
            return null;

        return method.Locals.FirstOrDefault(l => l.Register.Number == clobbered.Number && l.Register.Version == clobbered.Version);
    }
}
