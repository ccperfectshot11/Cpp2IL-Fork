using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

public class StackAnalyzer
{
    [DebuggerDisplay("Size = {Size}")]
    private class StackState
    {
        public int Size;
        public StackState Copy() => new() { Size = this.Size };
    }

    private static int _stackDiagCount;

    // Recognises `lea frame, [rsp+k]` as a frame-pointer setup alongside the plain `mov frame, rsp` that
    // was already handled, and follows the resulting address through copies and constant displacements.
    // On by default; CPP2IL_FRAME_LEA=0 leaves only the plain copy form, which is what this pass used to
    // recognise. The flow-sensitivity below is not part of the switch - it is a fix on its own.
    private static readonly bool FrameAliases = Environment.GetEnvironmentVariable("CPP2IL_FRAME_LEA") != "0";

    private Dictionary<Block, StackState> _inComingState = [];
    private Dictionary<Block, StackState> _outGoingState = [];
    private Dictionary<Instruction, StackState> _instructionState = [];

    /// <summary>
    /// Max allowed count of blocks to visit (-1 for no limit).
    /// </summary>
    public static int MaxBlockVisitCount = 500000; //High enough to not be legitimately hit, but still give up if something loops infinitely.

    public static void Analyze(MethodAnalysisContext method)
    {
        var analyzer = new StackAnalyzer();

        var graph = method.ControlFlowGraph!;
        graph.RemoveUnreachableBlocks(); // Without this indirect jumps (in try catch i think) cause some weird stuff

        analyzer._inComingState = new Dictionary<Block, StackState> { { graph.EntryBlock, new StackState() } };

        analyzer.TraverseGraph(graph.EntryBlock);

        // The exit block has no outgoing state if it was never reached (e.g. every path loops or
        // throws). That's fine - just skip the end-of-method stack balance check in that case.
        if (analyzer._outGoingState.TryGetValue(graph.ExitBlock, out var outDelta) && outDelta.Size != 0)
        {
            var outText = outDelta.Size < 0 ? "-" + (-outDelta.Size).ToString("X") : outDelta.Size.ToString("X");
            method.AddWarning($"Method ends with non empty stack ({outText}), the output could be wrong!");
        }

        analyzer.ResolveFrameAliases(graph);
        analyzer.CorrectOffsets(graph);
        ReplaceStackWithRegisters(method);

        graph.RemoveNops();
        graph.RemoveEmptyBlocks();
    }

    // consider mov [reg], [stack pointer]
    // now we need to handle [reg] as if it were a stack pointer, forever.
    //
    // Flow-sensitive, because it has to be. An alias lives from the instruction that establishes it until
    // something else writes that register, and on x64 that "something else" is always the epilogue putting
    // the caller's frame pointer back. Asking flow-insensitively whether the register is ever written
    // again therefore answered yes for 19.451 of the 19.797 methods that set a frame pointer up at all,
    // and every frame-relative access in them stayed an unresolvable [untyped local + offset].
    private void ResolveFrameAliases(ISILControlFlowGraph graph)
    {
        // Register name -> distance of its value from the stack pointer AS IT WAS ON ENTRY. Measuring from
        // entry rather than from here is what makes the value survive the pushes and the frame allocation:
        // those move the stack pointer, not the alias.
        var incoming = new Dictionary<Block, Dictionary<string, int>>
        {
            { graph.EntryBlock, new Dictionary<string, int>() },
        };

        var pending = new Stack<Block>();
        pending.Push(graph.EntryBlock);
        var visits = 0;

        while (pending.Count > 0)
        {
            var block = pending.Pop();
            var state = new Dictionary<string, int>(incoming[block]);

            foreach (var instruction in block.Instructions)
                ApplyFrameAlias(instruction, state);

            if (MaxBlockVisitCount != -1 && ++visits > MaxBlockVisitCount)
                throw new DecompilerException($"Frame aliases not settling! ({visits} blocks already visited)");

            foreach (var successor in block.Successors)
            {
                if (!incoming.TryGetValue(successor, out var existing))
                {
                    incoming[successor] = new Dictionary<string, int>(state);
                    pending.Push(successor);
                    continue;
                }

                // A register is an alias at a join only if every path agrees it is, and agrees on where
                // the frame starts. The meet only ever drops entries, so this settles.
                var merged = existing.Where(e => state.TryGetValue(e.Key, out var mine) && mine == e.Value)
                    .ToDictionary(e => e.Key, e => e.Value);

                if (merged.Count == existing.Count)
                    continue;

                incoming[successor] = merged;
                pending.Push(successor);
            }
        }

        // Second walk, now that the entry state of every block is final: replay each block and rewrite the
        // frame-relative memory operands against the alias that is live exactly there.
        foreach (var block in graph.Blocks)
        {
            if (!incoming.TryGetValue(block, out var entryState))
                continue; // unreachable

            var state = new Dictionary<string, int>(entryState);

            foreach (var instruction in block.Instructions)
            {
                // Rewrite before applying the instruction: `mov rbp, [rbp+8]` reads through the old alias
                // and only then stops being one.
                if (_instructionState.TryGetValue(instruction, out var stackState))
                    for (var i = 0; i < instruction.Operands.Count; i++)
                    {
                        if (instruction.Operands[i] is not MemoryOperand { Index: null, Scale: 0, Base: Register frameBase } memory)
                            continue;

                        if (!state.TryGetValue(frameBase.Name, out var frameOffset))
                            continue;

                        // CorrectOffsets adds the stack state back, so hand it the value relative to the
                        // stack pointer here, not the entry-relative one this pass carries around.
                        instruction.SetOperand(i, new StackOffset((int)(frameOffset + memory.Addend - stackState.Size)));
                    }

                ApplyFrameAlias(instruction, state);
            }
        }
    }

    // One instruction's effect on the set of live frame aliases: it either establishes one, or it clobbers
    // the register it writes. ShiftStack has no destination and so leaves every alias intact - which is the
    // point of measuring them from the entry stack pointer.
    private void ApplyFrameAlias(Instruction instruction, Dictionary<string, int> state)
    {
        if (instruction.Destination is not Register written)
            return;

        if (FrameAliasDistance(instruction) is { } distance && _instructionState.TryGetValue(instruction, out var stackState))
        {
            state[written.Name] = stackState.Size + distance;
            return;
        }

        // A frame address stays a frame address across a plain copy and across a constant displacement.
        // The displacement case is not an optimisation: `lea rax, [rbp+30h]` arrives here as an Add,
        // because the lifter only keeps the address-of shape for leas measured from rsp itself.
        if (CarriedFrameOffset(instruction, state) is { } carried)
            state[written.Name] = carried;
        else
            state.Remove(written.Name);
    }

    private static int? CarriedFrameOffset(Instruction instruction, Dictionary<string, int> state)
    {
        if (!FrameAliases || instruction.Operands.Count < 2 || instruction.Operands[1] is not Register source)
            return null;

        if (!state.TryGetValue(source.Name, out var offset))
            return null;

        if (instruction.OpCode == OpCode.Move && instruction.Operands.Count == 2)
            return offset;

        if (instruction.Operands.Count == 3 && instruction.Operands[2] is Immediate shift)
            return instruction.OpCode switch
            {
                OpCode.Add => offset + (int)shift.Value,
                OpCode.Subtract => offset - (int)shift.Value,
                _ => null,
            };

        return null;
    }

    // How far the value this instruction writes sits from the stack pointer AS IT IS AT THIS INSTRUCTION,
    // or null if the instruction is not a frame-pointer setup at all.
    //
    // `mov rbp, rsp` copies the stack pointer itself. `lea rbp, [rsp+k]` - which the lifter hands over as
    // a move of the ADDRESS of a stack slot - puts a fixed distance from it into the register instead, and
    // that is what msvc emits whenever the frame is too big for the one-byte displacements a plain copy
    // would leave it needing. Both establish the same alias; only the constant differs. Recognising only
    // the first form left every access through the second as an unresolvable [untyped local + offset] -
    // measured as the single largest source of "Unmanaged memory load" markers in the whole output.
    private static int? FrameAliasDistance(Instruction instruction) =>
        FrameAliases && instruction is { OpCode: OpCode.Move, Operands: [Register, AddressOf { Target: StackOffset slot }] }
            ? slot.Offset
            : instruction is { OpCode: OpCode.Move, Operands: [Register, Register { Name: "rsp" }] }
                ? 0
                : null;

    private void CorrectOffsets(ISILControlFlowGraph graph)
    {
        foreach (var block in graph.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is { OpCode: OpCode.ShiftStack })
                {
                    // Nop the shift stack instruction
                    instruction.OpCode = OpCode.Nop;
                    instruction.SetOperands();
                    continue;
                }

                int? state = null;

                // Correct offset for stack operands.
                for (var i = 0; i < instruction.Operands.Count; i++)
                {
                    var op = instruction.Operands[i];

                    var slot = op switch
                    {
                        StackOffset direct => direct,
                        AddressOf { Target: StackOffset addressed } => addressed,
                        _ => (StackOffset?)null
                    };

                    if (slot is { } offset)
                    {
                        // This can only be done before modifying any of the instruction operands,
                        // as doing so will make the dictionary lookup impossible.
                        state ??= _instructionState[instruction].Size;

                        var actual = new StackOffset(state.Value + offset.Offset);
                        instruction.SetOperand(i, op is AddressOf ? new AddressOf(actual) : actual);
                    }
                }
            }
        }
    }

    // Traverse the graph and calculate the stack state for each block and instruction
    private void TraverseGraph(Block initialBlock, int initialVisitedBlockCount = 0)
    {
        var blockLevelState = new Stack<(Block, int)>();
        blockLevelState.Push((initialBlock, initialVisitedBlockCount));

        while (blockLevelState.Count > 0)
        {
            var (block, visitedBlockCount) = blockLevelState.Pop();

            // Copy current state
            var incomingState = _inComingState[block];
            var currentState = incomingState.Copy();

            // Process instructions
            foreach (var instruction in block.Instructions)
            {
                _instructionState[instruction] = currentState;

                if (instruction.OpCode == OpCode.ShiftStack)
                {
                    var offset = (int)((Immediate)instruction.Operands[0]).Value;
                    currentState = currentState.Copy();
                    currentState.Size += offset;
                }
                else if (block.Instructions[^1] == instruction && block.BlockType == BlockType.TailCall)
                {
                    // Tail calls clear stack
                    currentState = currentState.Copy();
                    currentState.Size = 0;
                }
            }

            // Tail calls clear stack
            if (block.BlockType == BlockType.TailCall)
                currentState.Size = 0;

            _outGoingState[block] = currentState;

            visitedBlockCount++;

            if (MaxBlockVisitCount != -1 && visitedBlockCount > MaxBlockVisitCount)
                throw new DecompilerException($"Stack state not settling! ({visitedBlockCount} blocks already visited)");

            // Visit successors
            foreach (var successor in block.Successors)
            {
                // Already visited
                if (_inComingState.TryGetValue(successor, out var existingState))
                {
                    if (existingState.Size != currentState.Size)
                    {
                        _inComingState[successor] = currentState.Copy();
                        blockLevelState.Push((successor, visitedBlockCount + 1));
                    }
                }
                else
                {
                    // Set incoming delta and add to queue
                    _inComingState[successor] = currentState.Copy();
                    blockLevelState.Push((successor, visitedBlockCount + 1));
                }
            }
        }
    }

    private static void ReplaceStackWithRegisters(MethodAnalysisContext method)
    {
        var instructions = method.ControlFlowGraph!.Instructions;

        // Replace stack offset operands
        foreach (var instruction in instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                var operand = instruction.Operands[i];

                if (operand is StackOffset offset)
                    instruction.SetOperand(i, new Register(null, NameForSlot(offset)));

                if (operand is AddressOf { Target: StackOffset addressed })
                    instruction.SetOperand(i, new AddressOf(new Register(null, NameForSlot(addressed))));
            }
        }

        // Replace params
        for (var i = 0; i < method.ParameterOperands.Count; i++)
        {
            var parameter = method.ParameterOperands[i];

            if (parameter is StackOffset offset)
                method.ParameterOperands[i] = new Register(null, NameForSlot(offset));
        }
    }

    private static string NameForSlot(StackOffset offset) => offset.Offset < 0 ? $"stack_-{-offset.Offset:X}" : $"stack_{offset.Offset:X}";
}
