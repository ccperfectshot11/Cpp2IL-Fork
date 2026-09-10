using System;
using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Widens a 32-bit local that is only ever a courier for a 64-bit value.
///
/// A local's type is what the inference guessed from the way the register was used; the type of the value
/// put into it is read off metadata - a long field, or arithmetic on two of them. Where the guess is the
/// narrower of the two, reconciliation has no choice but to write the conversion the slot demands, and what
/// comes out is a pair: conv.i4 on the way in and conv.i8 on the way back out. The high half is thrown away
/// and then invented again by sign extension, which is a different number for anything that did not fit.
///
/// Quantum is where that shows. FP is fixed point kept in a long, so a multiply is
/// (a.RawValue * b.RawValue) >> 16 - a product that spends most of its life needing more than 32 bits.
/// Squeezed through an int local it comes back close but not equal, which the verifier cannot see, the
/// decompiler cannot see, and a bit-exact comparison against the running game rejects outright:
/// FP::op_Multiply, FPVector2::Dot, FPVector3::Cross and FPMath::Lerp all differ for this one reason.
///
/// The round trip is also what makes the repair safe, because it is the proof that the narrowing was never
/// wanted. A truncation the native code meant would be followed by a use of the value as an int - stored to
/// an int field, passed as an int argument, returned. Where instead every read widens it straight back,
/// nothing ever observes the narrow form, so dropping it leaves a value that did fit exactly as it was and
/// only keeps the one that did not. The same holds one step further out, across an add, a multiply or a xor
/// of two such locals: those opcodes settle their low 32 bits from the low 32 bits of their operands, so a
/// result that is truncated later is the same result either way. That is why every GetHashCode in the game
/// keeps the hash it already had while FP gets its precision back.
/// </summary>
public static class IntegerWidening
{
    // On by default (CPP2IL_WIDE_LOCALS=0 disables).
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("CPP2IL_WIDE_LOCALS") != "0";

    // A round only ever removes, so the set settles in as many rounds as there are candidates. The cap
    // is for a body pathological enough that settling costs more than the method is worth, and it gives
    // up on the body rather than stopping halfway: a half-settled set would widen one side of a pair and
    // leave the other narrow, which is the one way this pass could write IL that does not verify.
    private const int SettlingRounds = 8;

    /// <summary>
    /// Whether an opcode's low 32 bits are decided by the low 32 bits of its operands, which is what lets a
    /// widened operand reach it without changing an answer that is truncated further down. A divide, a
    /// remainder and a shift all read the half being restored, so they are not on the list: widening one of
    /// those changes the result for a value that fitted, and that is the one case this pass must not touch.
    /// </summary>
    private static bool PreservesLowHalf(CilCode code)
        => code is CilCode.Add or CilCode.Sub or CilCode.Mul or CilCode.And or CilCode.Or or CilCode.Xor;

    public static void Widen(CilMethodBody body)
    {
        if (!Enabled)
            return;

        var instructions = body.Instructions;
        var stores = new Dictionary<CilLocalVariable, List<int>>();
        var reads = new Dictionary<CilLocalVariable, List<int>>();
        var zeroed = new Dictionary<CilLocalVariable, List<int>>();
        var addressed = new HashSet<CilLocalVariable>();

        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];

            if (instruction.OpCode.Code is CilCode.Ldloca or CilCode.Ldloca_S)
            {
                if (instruction.GetLocalVariable(body.LocalVariables) is not { } local)
                    continue;

                // The zeroing prologue is the one address use that can come along: it names the local's type
                // in its own operand, so it is renamed with it. Every other one hands the address out and
                // there is no telling what width the far end reads back.
                if (i + 1 < instructions.Count && instructions[i + 1].OpCode.Code == CilCode.Initobj)
                    Record(zeroed, local, i + 1);
                else
                    addressed.Add(local);
            }
            else if (instruction.IsStloc())
            {
                if (instruction.GetLocalVariable(body.LocalVariables) is { } local)
                    Record(stores, local, i);
            }
            else if (instruction.IsLdloc())
            {
                if (instruction.GetLocalVariable(body.LocalVariables) is { } local)
                    Record(reads, local, i);
            }
        }

        var candidates = new HashSet<CilLocalVariable>();

        foreach (var pair in stores)
        {
            var local = pair.Key;

            if (addressed.Contains(local) || !reads.ContainsKey(local))
                continue;

            // int32 itself and nothing else the stack happens to widen to int32. A bool or a char says what
            // the value means as well as how wide it is, and moving one of those to a long would be a lie
            // about both.
            if (local.VariableType is not CorLibTypeSignature { ElementType: ElementType.I4 })
                continue;

            var everyStoreNarrows = true;

            foreach (var at in pair.Value)
                if (at == 0 || instructions[at - 1].OpCode.Code != CilCode.Conv_I4)
                {
                    everyStoreNarrows = false;
                    break;
                }

            if (everyStoreNarrows)
                candidates.Add(local);
        }

        // Dropping one local can invalidate a read of another that was only wide because of it, so the set
        // is settled before anything is written. It only ever shrinks, so this terminates.
        var rounds = 0;
        var dropped = false;

        do
        {
            if (rounds++ == SettlingRounds)
            {
                candidates.Clear();
                break;
            }

            dropped = false;

            foreach (var local in new List<CilLocalVariable>(candidates))
                foreach (var at in reads[local])
                    if (!StaysWide(body, candidates, at))
                    {
                        candidates.Remove(local);
                        dropped = true;
                        break;
                    }
        } while (dropped);

        if (candidates.Count == 0)
            return;

        var int64 = body.Owner!.DeclaringModule!.CorLibTypeFactory.Int64;

        foreach (var local in candidates)
        {
            local.VariableType = int64;

            // conv.i8 rather than no conversion at all: what reaches the store is not always the long this
            // pass is after - a constant, a narrower field - and converting keeps the store valid whatever
            // it is, while costing nothing where the value is already the right width.
            foreach (var at in stores[local])
                instructions[at - 1].OpCode = CilOpCodes.Conv_I8;

            if (zeroed.TryGetValue(local, out var initialisations))
                foreach (var at in initialisations)
                    instructions[at].Operand = int64.ToTypeDefOrRef();
        }
    }

    /// <summary>
    /// Whether one read of a candidate leaves the value at 64 bits, which is what says the narrow form is
    /// never looked at.
    /// </summary>
    private static bool StaysWide(CilMethodBody body, HashSet<CilLocalVariable> candidates, int at)
    {
        var instructions = body.Instructions;

        // Widened straight back, so the conversion becomes a no-op and the read is settled.
        if (at + 1 < instructions.Count && instructions[at + 1].OpCode.Code == CilCode.Conv_I8)
            return true;

        // Or it is the left operand of an opcode that keeps the low half, against another local this pass
        // is widening, with the result widened after it. Both sides move together or neither moves.
        if (at + 3 < instructions.Count
            && instructions[at + 1].IsLdloc()
            && instructions[at + 1].GetLocalVariable(body.LocalVariables) is { } right
            && candidates.Contains(right)
            && PreservesLowHalf(instructions[at + 2].OpCode.Code)
            && instructions[at + 3].OpCode.Code == CilCode.Conv_I8)
            return true;

        // The same shape with this read as the right operand.
        return at >= 1 && at + 2 < instructions.Count
            && instructions[at - 1].IsLdloc()
            && instructions[at - 1].GetLocalVariable(body.LocalVariables) is { } left
            && candidates.Contains(left)
            && PreservesLowHalf(instructions[at + 1].OpCode.Code)
            && instructions[at + 2].OpCode.Code == CilCode.Conv_I8;
    }

    private static void Record(Dictionary<CilLocalVariable, List<int>> into, CilLocalVariable local, int at)
    {
        if (!into.TryGetValue(local, out var indices))
            into[local] = indices = [];

        indices.Add(at);
    }
}
