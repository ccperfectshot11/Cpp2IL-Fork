using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Reconciles the type of a value on the evaluation stack with the type of the slot it is about to reach.
///
/// The lifter works on registers, and a register has a width but no type: the same 64 bits are a long here,
/// a struct there, and an object reference two instructions later. IL is the opposite - every store, every
/// return, every argument and both operands of every arithmetic opcode have to agree already, and there is
/// no implicit conversion anywhere. Emission has no notion of what is currently on the stack, so it emits
/// the load one site asks for and the store another asks for and the pair regularly disagrees. That
/// disagreement is not cosmetic: the runtime refuses the whole body, so the method cannot run at all. It is
/// what the verifier reports as an unexpected type on the stack - 939 of the 1,323 errors on
/// PhotonDeterministic, across 373 methods.
///
/// The repair has to see the stack, so it runs after emission rather than during it: the IL generated for
/// one ISIL instruction is walked with an abstract stack, and where a value meets a slot it does not fit,
/// the conversion the native code performed implicitly is written out. Per ISIL instruction rather than per
/// body because that unit starts and ends empty by construction - loads, an opcode, a store - so the walk
/// is exact with no control flow reasoning at all, and a group that stops adding up is simply left alone.
///
/// Only conversions that preserve what the native code did are emitted. Where the two types cannot both be
/// true - a string[] moved into a slot the inference decided was a native int - nothing is written: one of
/// the two types is wrong, this pass cannot tell which, and inventing a conversion would trade an honest
/// verifier error for a body that runs and lies.
/// </summary>
public static class StackCoercion
{
    // On by default (CPP2IL_STACK_TYPES=0 disables).
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("CPP2IL_STACK_TYPES") != "0";

    // Settles native-int arithmetic on int64, the only width C# can spell those operators on. Shares its
    // switch with the emitter's half of the same repair (CPP2IL_NINT_ARITH=0 disables both).
    private static readonly bool NativeArithmeticAsInt64 = Environment.GetEnvironmentVariable("CPP2IL_NINT_ARITH") != "0";

    // Unboxes the operands of an arithmetic opcode fed by two untyped locals (CPP2IL_OBJ_ARITH=0 disables).
    private static readonly bool UntypedArithmetic = Environment.GetEnvironmentVariable("CPP2IL_OBJ_ARITH") != "0";

    // Reads a union struct through whichever member is the register at the width the opcode wants. Shares its
    // switch with the emitter's half of the same repair (CPP2IL_UNION_STRUCTS=1 enables both; off by
    // default while the field read can land on an unassigned local - see IlGenerator).
    private static readonly bool UnwrapUnionStructs = Environment.GetEnvironmentVariable("CPP2IL_UNION_STRUCTS") == "1";

    // A round only reaches the sites the previous one boxed a result into, so the chains are short and a
    // body that keeps finding work is looping over something unexpected rather than converging.
    private const int UntypedArithmeticRounds = 4;

    /// <summary>
    /// What the evaluation stack actually distinguishes. Narrower integers do not appear: bool, char and
    /// everything below int32 all arrive as int32, which is why storing an int32 into a byte field needs no
    /// conversion while storing one into an int64 field does.
    /// </summary>
    private enum Kind { Unknown, Int32, Int64, Native, Float, Ref, ByRef, Struct }

    private readonly struct Value(TypeSignature? type, Kind kind, int producedBy)
    {
        public readonly TypeSignature? Type = type;
        public readonly Kind Kind = kind;

        // Index just past the instruction that finished producing this value, which is the only place a
        // conversion for it can go: by the time the consuming opcode is reached, the operands after it are
        // already stacked on top and nothing can be inserted underneath them.
        public readonly int ProducedBy = producedBy;
    }

    private sealed class Edit(int at, int order, List<CilInstruction> bridge)
    {
        public readonly int At = at;

        // Two conversions can share an insertion point - unwrapping an operand and then wrapping the result
        // of the opcode that consumed it. Inserting the later one first is what leaves them in the order
        // they were decided on.
        public readonly int Order = order;
        public readonly List<CilInstruction> Bridge = bridge;
    }

    public static void Reconcile(CilMethodBody body, int start)
    {
        if (!Enabled || start >= body.Instructions.Count)
            return;

        var instructions = body.Instructions;
        var stack = new List<Value>();
        var edits = new List<Edit>();

        for (var i = start; i < instructions.Count; i++)
        {
            if (!Step(body, instructions, i, stack, edits))
                break;
        }

        // Descending, so an insertion never moves an index a later edit still points at.
        edits.Sort((a, b) => a.At != b.At ? b.At.CompareTo(a.At) : b.Order.CompareTo(a.Order));

        foreach (var edit in edits)
            instructions.InsertRange(edit.At, edit.Bridge);
    }

    /// <summary>
    /// Makes every read of an untyped local name the type that was actually boxed into it.
    ///
    /// A local the inference never typed is declared object, so a primitive is boxed on the way in and
    /// unboxed on the way out. The two never had to agree: the box names what the arithmetic produced and
    /// the unbox names what the use site expects, and inference settling on int32 for a value that is
    /// really a long makes them differ constantly. unbox.any demands an exact match, so the pair verifies
    /// and then throws InvalidCastException the first time the method runs - invisible to the verifier and
    /// fatal to the method, which is the whole point of recovering it.
    ///
    /// The box is the one of the two that cannot be wrong: it names the type of a value that was really on
    /// the stack. So the read is retargeted at it and converted from there to what the use site asked for,
    /// which leaves both halves true. A local boxed as two different types on different paths is left
    /// alone - there is no single answer, and picking one would break the paths it does not describe.
    /// </summary>
    public static void AgreeWithBox(CilMethodBody body)
    {
        if (!Enabled)
            return;

        var instructions = body.Instructions;
        var boxedAs = BoxedInto(body);

        for (var i = instructions.Count - 2; i >= 0; i--)
        {
            if (!instructions[i].IsLdloc() || instructions[i + 1].OpCode.Code != CilCode.Unbox_Any)
                continue;

            if (UntypedLocal(body, instructions[i]) is not { } local
                || !boxedAs.TryGetValue(local, out var stored) || stored == null)
                continue;

            var expected = OperandType(instructions[i + 1], body);

            if (expected == null || expected.FullName == stored.FullName)
                continue;

            if (Bridge(body, new Value(stored, KindOf(stored), i + 2), expected) is not { } bridge)
                continue;

            instructions[i + 1].Operand = stored.ToTypeDefOrRef();
            instructions.InsertRange(i + 2, bridge);
        }
    }

    private static CilLocalVariable? UntypedLocal(CilMethodBody body, CilInstruction instruction)
        => instruction.GetLocalVariable(body.LocalVariables) is { VariableType.ElementType: ElementType.Object } local
            ? local
            : null;

    /// <summary>
    /// What each untyped local was boxed as, by the one statement of type that cannot be wrong: the box
    /// names a value that was really on the stack. A local boxed as two different types on different paths
    /// maps to null - there is no single answer for it.
    /// </summary>
    private static Dictionary<CilLocalVariable, TypeSignature?> BoxedInto(CilMethodBody body)
    {
        var instructions = body.Instructions;
        var boxedAs = new Dictionary<CilLocalVariable, TypeSignature?>();

        for (var i = 0; i + 1 < instructions.Count; i++)
        {
            if (instructions[i].OpCode.Code != CilCode.Box || !instructions[i + 1].IsStloc())
                continue;

            if (UntypedLocal(body, instructions[i + 1]) is not { } local)
                continue;

            var stored = OperandType(instructions[i], body);

            if (boxedAs.TryGetValue(local, out var already))
                boxedAs[local] = already?.FullName == stored?.FullName ? already : null;
            else
                boxedAs[local] = stored;
        }

        return boxedAs;
    }

    /// <summary>
    /// Unboxes the operands of an arithmetic opcode that reached it as untyped locals.
    ///
    /// il2cpp kept a number in a register; the inference never typed the local that register became, so it
    /// is declared object and the value is boxed on the way in. Where the use site is typed, emission
    /// unboxes on the way out - but at an arithmetic opcode fed by two such locals there is no typed side
    /// to ask, so both arrive as raw object references. add on two object references is not IL at all, and
    /// what it decompiles to is `obj + obj`, which is not C# either: 784 sites across the two
    /// Assembly-CSharp assemblies, add and mul the most of them.
    ///
    /// The box is what the local really holds, exactly as in <see cref="AgreeWithBox"/>, and one side
    /// naming a primitive is enough - the existing rule for an object meeting a number already says the
    /// other side names what to unbox to. Nothing is invented where neither side was ever boxed.
    ///
    /// Running in front of <see cref="AgreeWithBox"/> is what makes the pair emitted here safe: unbox.any
    /// demands the exact boxed type, and where the two operands were boxed as different widths the type
    /// asked for here is only the arithmetic's common one. AgreeWithBox then retargets each unbox at its
    /// own box and converts from there, so the widening happens after the value is off the heap.
    /// </summary>
    public static void UnboxArithmeticOperands(CilMethodBody body)
    {
        if (!Enabled || !UntypedArithmetic)
            return;

        // Each rewrite boxes its own result into the local the result is stored in, which is a new true
        // statement about that local - and the operand a later site could not type is regularly exactly
        // that local. So the pass is repeated while it keeps finding work, rather than once.
        for (var round = 0; round < UntypedArithmeticRounds && UnboxArithmeticOperandsOnce(body); round++)
        {
        }
    }

    private static bool UnboxArithmeticOperandsOnce(CilMethodBody body)
    {
        var instructions = body.Instructions;
        var boxedAs = BoxedInto(body);

        if (boxedAs.Count == 0)
            return false;

        var factory = Module(body).CorLibTypeFactory;
        var edits = new List<Edit>();

        for (var i = 2; i < instructions.Count; i++)
        {
            var code = instructions[i].OpCode.Code;

            if (!IsUntypedArithmetic(code, out var comparison, out var integerOnly))
                continue;

            if (!instructions[i - 2].IsLdloc() || !instructions[i - 1].IsLdloc()
                || UntypedLocal(body, instructions[i - 2]) is not { } left
                || UntypedLocal(body, instructions[i - 1]) is not { } right)
                continue;

            boxedAs.TryGetValue(left, out var leftBoxed);
            boxedAs.TryGetValue(right, out var rightBoxed);

            if (CommonBoxedType(leftBoxed, rightBoxed, factory, integerOnly) is not { } target)
                continue;

            // A comparison answers with a bool whatever went into it, so only the operands move. Arithmetic
            // now produces a number where an object reference used to be stored, and the slot it is stored
            // into is still declared object - so it is boxed back, which is also what makes the local
            // readable as the number it holds on the next round.
            var box = new List<CilInstruction>();

            if (!comparison)
            {
                if (i + 1 >= instructions.Count || !instructions[i + 1].IsStloc()
                    || UntypedLocal(body, instructions[i + 1]) == null)
                    continue;

                box.Add(new CilInstruction(CilOpCodes.Box, target.ToTypeDefOrRef()));
            }

            var unbox = target.ToTypeDefOrRef();

            edits.Add(new Edit(i - 1, edits.Count, [new CilInstruction(CilOpCodes.Unbox_Any, unbox)]));
            edits.Add(new Edit(i, edits.Count, [new CilInstruction(CilOpCodes.Unbox_Any, unbox)]));

            if (box.Count > 0)
                edits.Add(new Edit(i + 1, edits.Count, box));
        }

        if (edits.Count == 0)
            return false;

        edits.Sort((a, b) => a.At != b.At ? b.At.CompareTo(a.At) : b.Order.CompareTo(a.Order));

        foreach (var edit in edits)
            instructions.InsertRange(edit.At, edit.Bridge);

        return true;
    }

    /// <summary>
    /// The opcodes C# has no form of at all for object, which is what makes an untyped operand here a
    /// defect rather than a reference comparison. ceq is deliberately absent: `a == b` on two references
    /// is legal C# and legal IL, so nothing there says the values are not references.
    /// </summary>
    private static bool IsUntypedArithmetic(CilCode code, out bool comparison, out bool integerOnly)
    {
        comparison = code is CilCode.Cgt or CilCode.Cgt_Un or CilCode.Clt or CilCode.Clt_Un;
        integerOnly = code is CilCode.And or CilCode.Or or CilCode.Xor or CilCode.Div_Un or CilCode.Rem_Un;

        return code is CilCode.Add or CilCode.Sub or CilCode.Mul or CilCode.Div or CilCode.Div_Un
            or CilCode.Rem or CilCode.Rem_Un or CilCode.And or CilCode.Or or CilCode.Xor
            or CilCode.Cgt or CilCode.Cgt_Un or CilCode.Clt or CilCode.Clt_Un;
    }

    /// <summary>
    /// The one type both operands are brought to, from whichever of them a box named. Reconciling two
    /// different widths is the same decision <see cref="CommonOperandType"/> already makes, so it makes it
    /// here too, with an unboxed side standing in as the reference it currently is.
    /// </summary>
    private static TypeSignature? CommonBoxedType(TypeSignature? left, TypeSignature? right,
        CorLibTypeFactory factory, bool integerOnly)
    {
        if (left == null && right == null)
            return null;

        var known = left ?? right!;
        var target = left != null && right != null
            ? CommonOperandType(new Value(left, KindOf(left), 0), new Value(right, KindOf(right), 0),
                  factory, integerOnly, NativeArithmeticAsInt64) ?? known
            : known;

        // Same reason as everywhere else: two native ints verify, but C# cannot spell an operator on
        // IntPtr, so the value the recovered method has to read as is int64. Unlike the emitter's half,
        // that holds for the comparisons here too - `IntPtr < IntPtr` is a C# error just as loudly as
        // `IntPtr + IntPtr`, and a pass whose whole purpose is producing code that compiles has no reason
        // to leave the one shape it can spell for the one it cannot.
        if (KindOf(target) == Kind.Native && NativeArithmeticAsInt64)
            target = factory.Int64;

        return KindOf(target) switch
        {
            Kind.Int32 or Kind.Int64 or Kind.Native => target,
            Kind.Float => integerOnly ? null : target,
            _ => null,
        };
    }

    /// <summary>
    /// Models one instruction's effect on the abstract stack, coercing the values it consumes on the way.
    /// Returns false where the model can no longer be trusted, which abandons the rest of the group instead
    /// of guessing at it.
    /// </summary>
    private static bool Step(CilMethodBody body, CilInstructionCollection instructions, int index,
        List<Value> stack, List<Edit> edits)
    {
        var instruction = instructions[index];
        var code = instruction.OpCode.Code;
        var module = Module(body);
        var factory = module.CorLibTypeFactory;
        var next = index + 1;

        switch (code)
        {
            case CilCode.Nop or CilCode.Br or CilCode.Br_S:
                return true;

            case CilCode.Ldc_I4 or CilCode.Ldc_I4_S or CilCode.Ldc_I4_0 or CilCode.Ldc_I4_1 or CilCode.Ldc_I4_2
                or CilCode.Ldc_I4_3 or CilCode.Ldc_I4_4 or CilCode.Ldc_I4_5 or CilCode.Ldc_I4_6
                or CilCode.Ldc_I4_7 or CilCode.Ldc_I4_8 or CilCode.Ldc_I4_M1:
                return Push(stack, factory.Int32, next);

            case CilCode.Ldc_I8:
                return Push(stack, factory.Int64, next);

            case CilCode.Ldc_R4:
                return Push(stack, factory.Single, next);

            case CilCode.Ldc_R8:
                return Push(stack, factory.Double, next);

            case CilCode.Ldstr:
                return Push(stack, factory.String, next);

            // A null literal is a reference of no particular type and fits every reference slot, so it
            // carries the kind without a type rather than an unknown.
            case CilCode.Ldnull:
                stack.Add(new Value(null, Kind.Ref, next));
                return true;

            case CilCode.Ldloc or CilCode.Ldloc_S or CilCode.Ldloc_0 or CilCode.Ldloc_1
                or CilCode.Ldloc_2 or CilCode.Ldloc_3:
                return Push(stack, instruction.GetLocalVariable(body.LocalVariables)?.VariableType, next);

            case CilCode.Ldloca or CilCode.Ldloca_S:
                return Push(stack, instruction.GetLocalVariable(body.LocalVariables)?.VariableType?.MakeByReferenceType(), next);

            case CilCode.Ldarg or CilCode.Ldarg_S or CilCode.Ldarg_0 or CilCode.Ldarg_1
                or CilCode.Ldarg_2 or CilCode.Ldarg_3:
                return Push(stack, instruction.GetParameter(body.Owner!.Parameters)?.ParameterType, next);

            case CilCode.Ldarga or CilCode.Ldarga_S:
                return Push(stack, instruction.GetParameter(body.Owner!.Parameters)?.ParameterType?.MakeByReferenceType(), next);

            case CilCode.Ldsfld:
                return Push(stack, FieldType(instruction), next);

            case CilCode.Ldsflda:
                return Push(stack, FieldType(instruction)?.MakeByReferenceType(), next);

            case CilCode.Ldfld:
                return Pop(stack, 1) && Push(stack, FieldType(instruction), next);

            case CilCode.Ldflda:
                return Pop(stack, 1) && Push(stack, FieldType(instruction)?.MakeByReferenceType(), next);

            case CilCode.Ldlen:
                return Pop(stack, 1) && Push(stack, factory.IntPtr, next);

            case CilCode.Ldelem:
                return Pop(stack, 2) && Push(stack, OperandType(instruction, body), next);

            case CilCode.Ldelema:
                return Pop(stack, 2) && Push(stack, OperandType(instruction, body)?.MakeByReferenceType(), next);

            case CilCode.Ldobj:
                return Pop(stack, 1) && Push(stack, OperandType(instruction, body), next);

            case CilCode.Ldind_Ref:
                return Pop(stack, 1) && Push(stack, factory.Object, next);

            case CilCode.Ldftn or CilCode.Ldvirtftn:
                return Push(stack, factory.IntPtr, next);

            case CilCode.Ldtoken:
                return Push(stack, null, next);

            case CilCode.Newarr:
                return Pop(stack, 1) && Push(stack, OperandType(instruction, body)?.MakeSzArrayType(), next);

            case CilCode.Initobj or CilCode.Pop or CilCode.Brtrue or CilCode.Brtrue_S
                or CilCode.Brfalse or CilCode.Brfalse_S:
                return Pop(stack, 1);

            // The boxed type is written into the instruction, so a value of any other type here is exactly
            // the mismatch to repair: a long boxed as int32 is refused just as loudly as a long stored into
            // an int32 slot.
            case CilCode.Box:
                CoerceTop(body, stack, edits, OperandType(instruction, body));
                return Pop(stack, 1) && Push(stack, factory.Object, next);

            case CilCode.Unbox_Any or CilCode.Castclass or CilCode.Isinst:
                return Pop(stack, 1) && Push(stack, OperandType(instruction, body), next);

            case CilCode.Unbox:
                return Pop(stack, 1) && Push(stack, OperandType(instruction, body)?.MakeByReferenceType(), next);

            case CilCode.Stloc or CilCode.Stloc_S or CilCode.Stloc_0 or CilCode.Stloc_1
                or CilCode.Stloc_2 or CilCode.Stloc_3:
                CoerceTop(body, stack, edits, instruction.GetLocalVariable(body.LocalVariables)?.VariableType);
                return Pop(stack, 1);

            case CilCode.Starg or CilCode.Starg_S:
                CoerceTop(body, stack, edits, instruction.GetParameter(body.Owner!.Parameters)?.ParameterType);
                return Pop(stack, 1);

            case CilCode.Stsfld:
                CoerceTop(body, stack, edits, FieldType(instruction));
                return Pop(stack, 1);

            case CilCode.Stfld:
                CoerceTop(body, stack, edits, FieldType(instruction));
                return Pop(stack, 2);

            case CilCode.Stelem:
                CoerceTop(body, stack, edits, OperandType(instruction, body));
                return Pop(stack, 3);

            case CilCode.Stobj:
                CoerceTop(body, stack, edits, OperandType(instruction, body));
                return Pop(stack, 2);

            case CilCode.Ret:
                if (stack.Count > 0)
                    CoerceTop(body, stack, edits, body.Owner!.Signature?.ReturnType);

                stack.Clear();
                return true;

            case CilCode.Add or CilCode.Sub or CilCode.Mul or CilCode.Div or CilCode.Div_Un
                or CilCode.Rem or CilCode.Rem_Un or CilCode.And or CilCode.Or or CilCode.Xor
                or CilCode.Ceq or CilCode.Cgt or CilCode.Cgt_Un or CilCode.Clt or CilCode.Clt_Un:
                return Binary(body, stack, edits, index,
                    comparison: code is CilCode.Ceq or CilCode.Cgt or CilCode.Cgt_Un or CilCode.Clt or CilCode.Clt_Un,
                    integerOnly: code is CilCode.And or CilCode.Or or CilCode.Xor or CilCode.Div_Un or CilCode.Rem_Un,
                    nativeAsInt64: NativeArithmeticAsInt64 && code is CilCode.Add or CilCode.Sub or CilCode.Mul or CilCode.Div
                        or CilCode.Rem or CilCode.And or CilCode.Or or CilCode.Xor);

            // A shift count is its own thing - an int32 or a native int, never the shifted value's type -
            // so the two sides are not reconciled with each other and only the count is brought into range.
            case CilCode.Shl or CilCode.Shr or CilCode.Shr_Un:
                if (stack.Count < 2)
                    return false;

                if (stack[^1].Kind == Kind.Int64)
                    edits.Add(new Edit(stack[^1].ProducedBy, edits.Count, [new CilInstruction(CilOpCodes.Conv_I4)]));

                Unwrap(body, stack, edits, stack.Count - 2);

                var shifted = stack[^2];
                return Pop(stack, 2) && Push(stack, shifted.Type, next);

            // One operand, but the same story as two: a wrapper struct reaches the opcode because il2cpp
            // negated or complemented the register the primitive was living in.
            case CilCode.Neg or CilCode.Not:
                if (stack.Count == 0)
                    return false;

                Unwrap(body, stack, edits, stack.Count - 1);

                // The result is produced here, not where the operand was: a conversion for whatever
                // consumes it next has to land after the opcode rather than in front of its input.
                stack[^1] = new Value(stack[^1].Type, stack[^1].Kind, next);
                return true;

            case CilCode.Conv_I1 or CilCode.Conv_U1 or CilCode.Conv_I2 or CilCode.Conv_U2
                or CilCode.Conv_I4 or CilCode.Conv_U4:
                return Retype(stack, next, factory.Int32);

            case CilCode.Conv_I8 or CilCode.Conv_U8:
                return Retype(stack, next, factory.Int64);

            case CilCode.Conv_I or CilCode.Conv_U:
                return Retype(stack, next, factory.IntPtr);

            case CilCode.Conv_R4:
                return Retype(stack, next, factory.Single);

            case CilCode.Conv_R8 or CilCode.Conv_R_Un:
                return Retype(stack, next, factory.Double);

            case CilCode.Call or CilCode.Callvirt or CilCode.Newobj:
                return Invoke(body, stack, edits, instruction, next);

            default:
                return false;
        }
    }

    private static ModuleDefinition Module(CilMethodBody body) => body.Owner!.DeclaringModule!;

    private static TypeSignature? FieldType(CilInstruction instruction)
        => (instruction.Operand as IFieldDescriptor)?.Signature?.FieldType;

    private static TypeSignature? OperandType(CilInstruction instruction, CilMethodBody body)
        => Signature(instruction.Operand as ITypeDefOrRef, body);

    // Naming a reference as a signature has to decide whether it is a value type, and that needs the type
    // to be findable - which it is not for a reference into an assembly this run never loaded. It throws
    // rather than answering, and a type that cannot be named is a reason to leave a value alone, not to
    // lose the method it appears in.
    private static TypeSignature? Signature(ITypeDefOrRef? type, CilMethodBody body)
    {
        if (type == null)
            return null;

        try
        {
            return type.ToTypeSignature(Module(body).RuntimeContext);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Push(List<Value> stack, TypeSignature? type, int producedBy)
    {
        stack.Add(new Value(type, KindOf(type), producedBy));
        return true;
    }

    private static bool Pop(List<Value> stack, int count)
    {
        if (stack.Count < count)
            return false;

        stack.RemoveRange(stack.Count - count, count);
        return true;
    }

    private static bool Retype(List<Value> stack, int producedBy, TypeSignature type)
    {
        if (stack.Count == 0)
            return false;

        stack[^1] = new Value(type, KindOf(type), producedBy);
        return true;
    }

    /// <summary>
    /// Arguments are already stacked by the time the call is reached, so each is coerced back where it was
    /// produced. The receiver is left alone: a <c>this</c> that does not fit is a different problem, and
    /// nothing writable here would make the wrong object into the right one.
    /// </summary>
    private static bool Invoke(CilMethodBody body, List<Value> stack, List<Edit> edits,
        CilInstruction instruction, int next)
    {
        if (instruction.Operand is not IMethodDescriptor { Signature: { } signature } callee)
            return false;

        var arguments = signature.ParameterTypes.Count;
        var receiver = signature.HasThis && instruction.OpCode.Code != CilCode.Newobj ? 1 : 0;

        if (stack.Count < arguments + receiver)
            return false;

        for (var i = 0; i < arguments; i++)
            Coerce(body, stack, edits, stack.Count - arguments + i, signature.ParameterTypes[i]);

        if (!Pop(stack, arguments + receiver))
            return false;

        if (instruction.OpCode.Code == CilCode.Newobj)
            return Push(stack, Signature(callee.DeclaringType?.ToTypeDefOrRef(), body), next);

        return signature.ReturnType.ElementType == ElementType.Void || Push(stack, signature.ReturnType, next);
    }

    /// <summary>
    /// Brings the two operands of an arithmetic or comparison opcode to a shape IL accepts. Widening rather
    /// than truncating: the bits are the same either way, and where one side is already 64 bits the native
    /// code was working a 64-bit register, so widening the other states what the hardware did. Truncating
    /// instead would be a guess about which of the two types the inference got right.
    /// </summary>
    private static bool Binary(CilMethodBody body, List<Value> stack, List<Edit> edits, int index,
        bool comparison, bool integerOnly, bool nativeAsInt64)
    {
        if (stack.Count < 2)
            return false;

        var left = stack[^2];
        var right = stack[^1];
        var factory = Module(body).CorLibTypeFactory;
        var target = CommonOperandType(left, right, factory, integerOnly, nativeAsInt64);

        if (target != null)
        {
            Coerce(body, stack, edits, stack.Count - 2, target);
            Coerce(body, stack, edits, stack.Count - 1, target);
        }

        // A comparison answers with a bool, and it is the only opcode here whose result says nothing about
        // what went into it.
        var result = comparison ? factory.Int32 : target ?? left.Type;

        return Pop(stack, 2) && Push(stack, result, index + 1);
    }

    private static TypeSignature? CommonOperandType(Value left, Value right, CorLibTypeFactory factory,
        bool integerOnly, bool nativeAsInt64)
    {
        if (left.Kind == right.Kind)
        {
            // Two native ints need no conversion to make the opcode verify, but C# has no arithmetic on
            // IntPtr at all, so the pair decompiles to something that does not compile. int64 is the same
            // value - conv.i8 sign-extends and every opcode routed here commutes with that - and it is a
            // type the language can actually spell the operator on.
            if (left.Kind == Kind.Native && nativeAsInt64)
                return factory.Int64;

            // Two structs still have to be unwrapped, and doing so is only right where both hold the same
            // primitive - otherwise the opcode would be handed two different things than it was handed
            // before.
            if (left.Kind != Kind.Struct)
                return null;

            // Two registers meeting each other are compared whole, so neither side asks for a width.
            var leftValue = ValueFieldTypeOf(left.Type, 0);
            var rightValue = ValueFieldTypeOf(right.Type, 0);

            return leftValue != null && rightValue != null && leftValue.FullName == rightValue.FullName
                ? leftValue
                : null;
        }

        // A struct only reaches an arithmetic opcode because il2cpp kept it in a register as if it were the
        // primitive it wraps, so the primitive is what both sides have to be.
        if (left.Kind == Kind.Struct || right.Kind == Kind.Struct)
        {
            var other = left.Kind == Kind.Struct ? right : left;

            // Where the struct is a union rather than a wrapper, the other side's width is what says which of
            // its overlapping members the register was being read as - see IlGenerator.UnionValueField.
            var wrapped = ValueFieldTypeOf(left.Kind == Kind.Struct ? left.Type : right.Type, RegisterBits(other.Kind));

            return wrapped != null && IsNumeric(other.Kind) ? wrapped : null;
        }

        // An untyped local is declared object, and whatever went into it was boxed on the way in, so the
        // other side names what to unbox it back to. Nothing is claimed that the store did not claim first.
        if (left.Kind == Kind.Ref || right.Kind == Kind.Ref)
        {
            var reference = left.Kind == Kind.Ref ? left : right;
            var other = left.Kind == Kind.Ref ? right : left;

            return reference.Type?.ElementType == ElementType.Object && IsNumeric(other.Kind)
                ? other.Type
                : null;
        }

        // A bitwise opcode has no float form at all: meeting a float there means the register held integer
        // bits that the inference read as a number, so the integer side is the one both are brought to.
        if (left.Kind == Kind.Float || right.Kind == Kind.Float)
        {
            if (!integerOnly)
                return left.Kind == Kind.Float ? left.Type : right.Type;

            var integer = left.Kind == Kind.Float ? right : left;
            return integer.Kind is Kind.Int32 or Kind.Int64 or Kind.Native ? integer.Type : null;
        }

        if (left.Kind == Kind.Int64 || right.Kind == Kind.Int64)
            return factory.Int64;

        // Same reason as the pair above: the register held a native int, and int64 is the width the
        // language can write the operator on.
        if (left.Kind == Kind.Native || right.Kind == Kind.Native)
            return nativeAsInt64 ? factory.Int64 : factory.IntPtr;

        return null;
    }

    private static bool IsNumeric(Kind kind) => kind is Kind.Int32 or Kind.Int64 or Kind.Native or Kind.Float;

    // Reads a struct's value out where an opcode wants a number and was handed the struct. No width is asked
    // for, so a union gives up the whole register rather than one of its halves.
    private static void Unwrap(CilMethodBody body, List<Value> stack, List<Edit> edits, int slot)
    {
        if (stack[slot].Kind == Kind.Struct && ValueFieldTypeOf(stack[slot].Type, 0) is { } wrapped)
            Coerce(body, stack, edits, slot, wrapped);
    }

    private static void CoerceTop(CilMethodBody body, List<Value> stack, List<Edit> edits, TypeSignature? expected)
    {
        if (stack.Count > 0)
            Coerce(body, stack, edits, stack.Count - 1, expected);
    }

    private static void Coerce(CilMethodBody body, List<Value> stack, List<Edit> edits, int slot, TypeSignature? expected)
    {
        var value = stack[slot];

        if (expected == null || value.Kind == Kind.Unknown)
            return;

        if (Bridge(body, value, expected) is not { } bridge)
            return;

        edits.Add(new Edit(value.ProducedBy, edits.Count, bridge));
        stack[slot] = new Value(expected, KindOf(expected), value.ProducedBy);
    }

    /// <summary>
    /// The instructions that turn a value of one type into the same value under another, or null where the
    /// two cannot both describe the same bits - which makes the mismatch a typing error rather than a
    /// missing conversion, and not this pass's to paper over.
    /// </summary>
    private static List<CilInstruction>? Bridge(CilMethodBody body, Value value, TypeSignature expected)
    {
        var wanted = KindOf(expected);

        if (wanted is Kind.Unknown or Kind.ByRef || value.Kind is Kind.Unknown or Kind.ByRef)
            return null;

        if (wanted == value.Kind && wanted != Kind.Struct)
            return null;

        // A reference already fits any reference slot the verifier would accept it in; where it does not,
        // the two are unrelated types and a cast would be a claim rather than a conversion. The exception is
        // an untyped local, which is object precisely because nothing was known about it, and castclass
        // states what the use site established - null passes through it unharmed, unlike unbox.any.
        if (wanted == Kind.Ref)
        {
            if (value.Kind == Kind.Ref)
                return value.Type?.ElementType == ElementType.Object && expected.ElementType != ElementType.Object
                    ? [new CilInstruction(CilOpCodes.Castclass, expected.ToTypeDefOrRef())]
                    : null;

            // Only into object: boxing into a slot of some other reference type would still not fit it.
            return expected.ElementType == ElementType.Object && value.Type != null
                ? [new CilInstruction(CilOpCodes.Box, value.Type.ToTypeDefOrRef())]
                : null;
        }

        if (value.Kind == Kind.Ref)
            return value.Type?.ElementType == ElementType.Object
                ? [new CilInstruction(CilOpCodes.Unbox_Any, expected.ToTypeDefOrRef())]
                : null;

        // A wrapper struct is the primitive it holds as far as the register is concerned, so the value goes
        // in and out of it through that one field rather than being reinterpreted.
        if (value.Kind == Kind.Struct)
        {
            if (ValueFieldOf(value.Type, RegisterBits(wanted)) is not { } read)
                return null;

            var unwrapped = new Value(read.Signature!.FieldType, KindOf(read.Signature.FieldType), value.ProducedBy);
            var bridge = new List<CilInstruction> { new(CilOpCodes.Ldfld, Import(body, read)) };

            if (Bridge(body, unwrapped, expected) is { } rest)
                bridge.AddRange(rest);
            else if (unwrapped.Kind != wanted)
                return null;

            return bridge;
        }

        if (wanted == Kind.Struct)
        {
            // A store puts a whole register back, so a union is written through the member covering it.
            if (ValueFieldOf(expected, 0) is not { } written)
                return null;

            var target = written.Signature!.FieldType;
            var bridge = Numeric(value.Kind, KindOf(target)) is { } conversion
                ? [new CilInstruction(conversion)]
                : new List<CilInstruction>();

            // A readonly field cannot be written from outside its own constructor, and TimeSpan is one -
            // its whole value is a readonly long. The constructor that takes exactly that value is the
            // wrapping the type itself offers, so use it where there is one and leave the mismatch alone
            // where there is not, rather than writing IL that says the field is not readonly after all.
            if (written.IsInitOnly)
            {
                if (WrapperConstructor(expected, target) is not { } constructor)
                    return null;

                bridge.Add(new CilInstruction(CilOpCodes.Newobj, Import(body, constructor)));
                return bridge;
            }

            // stfld wants the destination's address underneath the value, so the value is parked while the
            // address goes there. A fresh slot each time: reusing one that merely has the right type would
            // clobber a live value.
            var parked = new CilLocalVariable(target);
            var wrapper = new CilLocalVariable(expected);
            body.LocalVariables.Add(parked);
            body.LocalVariables.Add(wrapper);

            bridge.Add(new CilInstruction(CilOpCodes.Stloc, parked));
            bridge.Add(new CilInstruction(CilOpCodes.Ldloca, wrapper));
            bridge.Add(new CilInstruction(CilOpCodes.Ldloc, parked));
            bridge.Add(new CilInstruction(CilOpCodes.Stfld, Import(body, written)));
            bridge.Add(new CilInstruction(CilOpCodes.Ldloc, wrapper));

            return bridge;
        }

        return Numeric(value.Kind, wanted) is { } numeric ? [new CilInstruction(numeric)] : null;
    }

    // The wrapper is resolved from wherever it is declared, which is not always the module being written -
    // FP lives in PhotonDeterministic and is used from every assembly that touches Quantum - so the
    // definition has to become a reference this module can actually carry.
    private static IFieldDescriptor Import(CilMethodBody body, FieldDefinition field)
        => Module(body).DefaultImporter.ImportField(field);

    private static IMethodDescriptor Import(CilMethodBody body, MethodDefinition method)
        => Module(body).DefaultImporter.ImportMethod(method);

    // The constructor a wrapper offers for its own value, which is how a readonly single field is meant to
    // be filled in.
    private static MethodDefinition? WrapperConstructor(TypeSignature wrapper, TypeSignature value)
    {
        if (Resolve(wrapper) is not { } definition)
            return null;

        foreach (var method in definition.Methods)
            if (method is { IsConstructor: true, IsStatic: false, Signature.ParameterTypes.Count: 1 }
                && method.Signature.ParameterTypes[0].FullName == value.FullName)
                return method;

        return null;
    }

    private static CilOpCode? Numeric(Kind from, Kind to)
    {
        if (from == to || !IsNumeric(from))
            return null;

        return to switch
        {
            Kind.Int32 => CilOpCodes.Conv_I4,
            Kind.Int64 => CilOpCodes.Conv_I8,
            Kind.Native => CilOpCodes.Conv_I,
            Kind.Float => CilOpCodes.Conv_R8,
            _ => null,
        };
    }

    private static Kind KindOf(TypeSignature? type)
    {
        if (type == null)
            return Kind.Unknown;

        switch (type.ElementType)
        {
            case ElementType.Boolean or ElementType.Char or ElementType.I1 or ElementType.U1
                or ElementType.I2 or ElementType.U2 or ElementType.I4 or ElementType.U4:
                return Kind.Int32;
            case ElementType.I8 or ElementType.U8:
                return Kind.Int64;
            case ElementType.I or ElementType.U or ElementType.Ptr or ElementType.FnPtr:
                return Kind.Native;
            case ElementType.R4 or ElementType.R8:
                return Kind.Float;
            case ElementType.String or ElementType.Object or ElementType.Class
                or ElementType.Array or ElementType.SzArray:
                return Kind.Ref;
            case ElementType.ByRef:
                return Kind.ByRef;
            case ElementType.ValueType or ElementType.GenericInst:
                return NamedKind(type);
            default:
                // Generic parameters, typed references, void, anything modified: nothing here can be said
                // about the width, and a conversion chosen without knowing it is worse than the mismatch.
                return Kind.Unknown;
        }
    }

    private static readonly ConcurrentDictionary<string, Kind> NamedKinds = new();

    /// <summary>
    /// The kind of a named type. IntPtr has to be matched by name because il2cpp metadata resolves it as an
    /// ordinary struct rather than the native int primitive - the same reason the emitter's own integer
    /// classification does. An enum is its underlying primitive once on the stack, so treating one as a
    /// struct would have a plain int32 wrapped into something that was never a wrapper.
    /// </summary>
    private static Kind NamedKind(TypeSignature type)
    {
        if (type.ElementType == ElementType.GenericInst)
            return type.IsValueType ? Kind.Struct : Kind.Ref;

        if (type.FullName is "System.IntPtr" or "System.UIntPtr")
            return Kind.Native;

        return NamedKinds.GetOrAdd(type.FullName, _ =>
        {
            if (Resolve(type) is not { IsEnum: true } definition)
                return Kind.Struct;

            foreach (var field in definition.Fields)
                if (!field.IsStatic && field.Signature != null)
                    return KindOf(field.Signature.FieldType);

            return Kind.Struct;
        });
    }

    private static readonly ConcurrentDictionary<string, FieldDefinition?> SingleFields = new();

    /// <summary>
    /// The one instance field a wrapper struct keeps its whole value in - Quantum's <c>FP</c> is a
    /// <c>long RawValue</c>, and this game is full of them. il2cpp keeps such a struct in a register exactly
    /// as if it were that primitive, so the primitive is what reaches an arithmetic opcode or a register
    /// move, and the field is how the two convert into each other.
    /// </summary>
    private static FieldDefinition? WrapperField(TypeSignature? type)
    {
        if (type is not { ElementType: ElementType.ValueType } || KindOf(type) != Kind.Struct)
            return null;

        return SingleFields.GetOrAdd(type.FullName, _ =>
        {
            if (Resolve(type) is not { } definition)
                return null;

            FieldDefinition? only = null;

            foreach (var field in definition.Fields)
            {
                if (field.IsStatic)
                    continue;

                // Two fields and there is no single value to unwrap to - a vector, a struct of structs.
                if (only != null)
                    return null;

                only = field;
            }

            return only?.Signature?.FieldType is CorLibTypeSignature ? only : null;
        });
    }

    private static readonly ConcurrentDictionary<string, List<FieldDefinition>?> UnionFields = new();

    /// <summary>
    /// The field a value goes into or out of a struct through: the one value a wrapper holds, or the member of
    /// a union that is the register at the width asked for. The wrapper case is tried first, so a type that
    /// already converted keeps converting through exactly the field it always did.
    /// </summary>
    private static FieldDefinition? ValueFieldOf(TypeSignature? type, int registerBits)
        => WrapperField(type) ?? UnionField(type, registerBits);

    private static TypeSignature? ValueFieldTypeOf(TypeSignature? type, int registerBits)
        => ValueFieldOf(type, registerBits)?.Signature?.FieldType;

    /// <summary>
    /// The emitter's <c>IlGenerator.UnionValueField</c> seen from this side: an explicit-layout struct whose
    /// fields overlap at offset 0 is one register under several names, and the width the opcode runs at is
    /// what says which name the original source used. Quantum's <c>EntityRef</c> is
    /// <c>{ int Index @0; int Version @4; ulong Raw @0 }</c> - <c>Index</c> where a 32-bit value is wanted,
    /// <c>Raw</c> where the whole thing is.
    /// </summary>
    private static FieldDefinition? UnionField(TypeSignature? type, int registerBits)
    {
        if (!UnwrapUnionStructs || type is not { ElementType: ElementType.ValueType } || KindOf(type) != Kind.Struct)
            return null;

        if (UnionFields.GetOrAdd(type.FullName, _ => UnionMembers(type)) is not { } members)
            return null;

        // The members are narrowest first, so the first one wide enough is the narrowest that fits. Where
        // nothing said how wide the opcode ran, the honest answer is the whole register.
        if (registerBits > 0)
            foreach (var member in members)
                if (FieldWidth(member) * 8 >= registerBits)
                    return member;

        return members[^1];
    }

    /// <summary>
    /// The members of a union that are the whole register or a low half of it, narrowest first. Null unless
    /// one of them covers the struct exactly and every field's width is known - together that is what says
    /// the fields really do overlay one register rather than sit side by side.
    /// </summary>
    private static List<FieldDefinition>? UnionMembers(TypeSignature type)
    {
        if (Resolve(type) is not { IsExplicitLayout: true } definition)
            return null;

        var extent = 0;
        var members = new List<FieldDefinition>();

        foreach (var field in definition.Fields)
        {
            if (field.IsStatic)
                continue;

            if (field.Signature?.FieldType is not CorLibTypeSignature primitive
                || PrimitiveWidth(primitive) is not { } width
                || field.FieldOffset is not { } offset)
                return null;

            extent = Math.Max(extent, offset + width);

            // A member further in is a piece of the register rather than a view of it, a float one is not what
            // an integer opcode was reading, and one below int32 is not a width the evaluation stack has - byte
            // 0 of a union is one byte of the value rather than a narrower way of holding all of it.
            if (offset == 0 && IsInteger(primitive) && width >= 4)
                members.Add(field);
        }

        members.Sort((left, right) => FieldWidth(left).CompareTo(FieldWidth(right)));

        return members.Count > 0 && FieldWidth(members[^1]) == extent ? members : null;
    }

    private static int FieldWidth(FieldDefinition field)
        => field.Signature?.FieldType is CorLibTypeSignature primitive && PrimitiveWidth(primitive) is { } width
            ? width
            : 0;

    // Null for native int, whose width is the one that is not written down, and for anything that is not a
    // primitive at all.
    private static int? PrimitiveWidth(CorLibTypeSignature type) => type.ElementType switch
    {
        ElementType.Boolean or ElementType.I1 or ElementType.U1 => 1,
        ElementType.Char or ElementType.I2 or ElementType.U2 => 2,
        ElementType.I4 or ElementType.U4 or ElementType.R4 => 4,
        ElementType.I8 or ElementType.U8 or ElementType.R8 => 8,
        _ => null,
    };

    private static bool IsInteger(CorLibTypeSignature type) => type.ElementType is
        ElementType.I1 or ElementType.U1 or ElementType.I2 or ElementType.U2
        or ElementType.I4 or ElementType.U4 or ElementType.I8 or ElementType.U8;

    // What a kind occupies on the evaluation stack, which is the width the opcode ran at. Zero where the kind
    // says nothing, which UnionField reads as the whole register.
    private static int RegisterBits(Kind kind) => kind switch
    {
        Kind.Int32 => 32,
        Kind.Int64 or Kind.Native => 64,
        _ => 0,
    };

    // A type from an assembly that is not loaded, or a reference nothing can be found for, resolves to
    // nothing at all - and throws in some shapes rather than returning null.
    private static TypeDefinition? Resolve(TypeSignature type)
    {
        try
        {
            return type.ContextModule is { } module ? type.ToTypeDefOrRef().Resolve(module.RuntimeContext) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
