using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Removes the inlined cast checks il2cpp emits for <c>castclass</c> and <c>unbox</c>.
///
/// A C# <c>(Foo)x</c> does not survive as a call: il2cpp inlines <c>Class::HasParent</c> (and, for a
/// boxed value type, the element-class compare from <c>Object::Unbox</c>) straight into the caller, so
/// what is left in the binary is pointer arithmetic over <c>Il2CppClass</c> ending in a throw. None of
/// that arithmetic can be expressed in C#, and none of its operands resolve to a managed field, so the
/// region prints as a chain of fabricated <c>(IntPtr)0 &gt;= (IntPtr)0</c> comparisons followed by
/// <c>throw new InvalidCastException()</c>. IL puts the check inside the cast opcode, so the whole
/// region is redundant once it is recognised, and dropping it is the same excision
/// <see cref="InjectedCheckRemover"/> performs for the injected null and bounds checks.
///
/// Both recognised shapes begin with a comparison of the same offset read off two <em>different</em>
/// Il2CppClass pointers, and both are anchored on a conditional jump into a block that does nothing
/// but throw a helper-raised InvalidCastException:
///
/// <code>
/// // castclass, Class::HasParent, two blocks:
/// klass = [obj + 0]                                  // Il2CppObject::klass
/// if ([klass + D] &lt; [target + D]) throw            // typeHierarchyDepth
/// hierarchy = [klass + H]                            // typeHierarchy
/// if (hierarchy[[target + D] - 1] != target) throw   // typeHierarchy[depth - 1]
///
/// // unbox, Object::Unbox, one block:
/// klass = [obj + 0]
/// if ([klass + E] != [target + E]) throw             // element_class
/// </code>
///
/// The offsets D, H and E are never named. They fall out of the shape itself - D and E are whatever
/// offset is read off both class pointers and compared, H is whatever offset feeds the scaled
/// hierarchy index - which keeps this working across the Il2CppClass layout changes that
/// <see cref="Il2CppClassUsefulOffsets"/> and <see cref="MetadataInitGuardRemover"/> disagree about.
///
/// Only the throwing forms are handled. An <c>x as Foo</c> compiles the same comparisons into a
/// conditional null rather than a throw, and a cast to an interface goes through the far larger
/// inlined <c>Class::IsAssignableFrom</c>, whose only throw is an ordinary
/// <c>if (result == null) throw</c> on a value that region has already computed. Neither carries
/// anything that separates it from real code, so both are deliberately left alone - CPP2IL_CASTDIAG
/// counts what that leaves behind.
/// </summary>
public static class CastRecovery
{
    // Off by default: when this fires it deletes blocks, so a false positive would delete real code.
    // On by default (CPP2IL_CASTCLASS=0 disables) once measured: markers 1,495 -> 1,490, clean types
    // 42.9% -> 43.1%, STRICT 11,162 -> 11,178, method count unchanged. It recovers real code rather than
    // merely quieting an error, which is why it earns the default despite removing blocks when it fires.
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("CPP2IL_CASTCLASS") != "0";

    // CPP2IL_CASTDIAG=1 classifies every guard that jumps into an InvalidCastException throw block,
    // matched or not, so the miss rate is measurable without changing the output. CPP2IL_CASTDUMP=kind
    // then prints the first few guards of one kind in full.
    private static readonly bool Diag = Environment.GetEnvironmentVariable("CPP2IL_CASTDIAG") == "1";

    private static readonly ConcurrentDictionary<string, long> Counts = new();
    private static int _hooked;
    private static int _dumped;

    private const string InvalidCast = "System.InvalidCastException";

    public static void Run(MethodAnalysisContext method)
    {
        if (!Enabled && !Diag)
            return;

        if (Diag)
            HookDiagOutput();

        var cfg = method.ControlFlowGraph!;
        var pointerSize = method.AppContext.Binary.is32Bit ? 4 : 8;
        var definitions = BuildDefMap(cfg);

        // Collect before rewriting: matching walks successors, and excising rewrites them. The second
        // block of a HasParent pair is itself a guard into the same thrower, so remember which blocks
        // an earlier match already accounted for and do not re-read them as candidates of their own.
        var guards = new List<(Block guard, Block thrower)>();
        var claimed = new HashSet<Block>();

        foreach (var block in cfg.Blocks)
        {
            if (claimed.Contains(block) || ThrowTargetOf(block) is not { } thrower)
                continue;

            if (ClassFieldCompare(block, definitions) is not { } compare)
            {
                Report(method, block, thrower, UnmatchedKind(block, definitions));
                continue;
            }

            // typeHierarchyDepth is compared for ordering and needs its second block to be a cast;
            // element_class is compared for inequality and is the whole check on its own.
            if (!compare.Ordered)
            {
                Count("matched.unbox");
                guards.Add((block, thrower));
            }
            else if (HierarchyCheckAfter(block, thrower, compare, definitions, pointerSize) is { } hierarchyCheck)
            {
                Count("matched.castclass");
                guards.Add((block, thrower));
                guards.Add((hierarchyCheck, thrower));
                claimed.Add(hierarchyCheck);
            }
            else
            {
                Report(method, block, thrower, "unmatched.hierarchy");
            }
        }

        if (!Enabled || guards.Count == 0)
            return;

        foreach (var (guard, thrower) in guards)
            DropGuard(guard, thrower);

        // A thrower with no predecessors left is one whole `throw new InvalidCastException()` gone from
        // the output; one still reached by an unrecognised guard stays, and is counted so the two are
        // not confused.
        foreach (var thrower in guards.Select(pair => pair.thrower).Distinct())
            Count(thrower.Predecessors.Count == 0 ? "removed.throwBlock" : "kept.throwBlock");

        // The thrower is now unreachable - unless an unrecognised guard still reaches it - and the
        // class-pointer arithmetic that fed the tests has no remaining consumer.
        cfg.RemoveUnreachableBlocks();
        DeadCodeEliminator.Run(cfg);
    }

    // Neutralises the jump into the thrower, exactly as InjectedCheckRemover does for a null check.
    private static void DropGuard(Block guard, Block thrower)
    {
        if (guard.Instructions.Count == 0 || guard.Instructions[^1].OpCode != OpCode.ConditionalJump)
            return;

        guard.Instructions[^1].OpCode = OpCode.Nop;
        guard.Instructions[^1].SetOperands();

        guard.Successors.Remove(thrower);
        thrower.Predecessors.Remove(guard);
        guard.CalculateBlockType();
    }

    /// <summary>
    /// The block this one conditionally jumps to, if that block does nothing but throw an
    /// InvalidCastException, else null.
    ///
    /// The exception must arrive as a type operand rather than as a local: that is the form
    /// <see cref="ThrowHelperRecovery"/> produces for the shared il2cpp throw helper. A cast check the
    /// user wrote by hand constructs the exception first, so its Throw carries the newobj'd local, and
    /// requiring the helper form is what keeps a hand-written
    /// <c>if (...) throw new InvalidCastException()</c> out of this pass.
    /// </summary>
    private static Block? ThrowTargetOf(Block block)
    {
        if (block.BlockType != BlockType.TwoWay || block.Instructions.Count == 0)
            return null;

        var terminator = block.Instructions[^1];

        if (terminator.OpCode != OpCode.ConditionalJump || terminator.Operands[0] is not Block target)
            return null;

        var throws = false;

        foreach (var instruction in target.Instructions)
        {
            switch (instruction.OpCode)
            {
                case OpCode.Nop or OpCode.Interrupt:
                case OpCode.Return or OpCode.Jump when throws:
                    continue;

                case OpCode.Throw when !throws
                    && instruction.Operands is [TypeAnalysisContext { FullName: InvalidCast }]:
                    throws = true;
                    continue;

                default:
                    return null;
            }
        }

        return throws ? target : null;
    }

    /// <summary>The two class pointers a guard compares a shared field of, and how it compares them.</summary>
    private readonly record struct ClassCompare(LocalVariable Klass, LocalVariable Target, long Addend, bool Ordered);

    /// <summary>
    /// Reads a guard as "the same offset, read off two different Il2CppClass pointers, compared".
    ///
    /// That is the signature both cast checks share, and it pins the field down without needing to know
    /// which field it is: ordinary code has no reason to read one offset off two class pointers and
    /// compare the results, still less to throw InvalidCastException when the comparison fails.
    /// </summary>
    private static ClassCompare? ClassFieldCompare(Block guard, Dictionary<LocalVariable, Instruction> definitions)
    {
        if (ConditionDefinition(guard, definitions) is not { } comparison)
            return null;

        // klass->depth < target->depth means klass sits too high to be below target in the hierarchy;
        // accept the mirrored spelling of the same test. Inequality is the unbox element-class check.
        var (left, right, ordered) = comparison.OpCode switch
        {
            OpCode.CheckLess => (comparison.Operands[1], comparison.Operands[2], true),
            OpCode.CheckGreater => (comparison.Operands[2], comparison.Operands[1], true),
            OpCode.CheckNotEqual => (comparison.Operands[1], comparison.Operands[2], false),
            _ => (null, null, false),
        };

        if (FieldRead(left, definitions) is not { } klassField || FieldRead(right, definitions) is not { } targetField
            || klassField.Addend <= 0 || klassField.Addend != targetField.Addend)
            return null;

        // The right-hand side is the cast target, so it has to be a class pointer we can name. The left
        // is only ever the class of the object being cast, which the rest of the shape pins down
        // anyway, so an untyped local there is not a reason to give up.
        if (klassField.Base is not LocalVariable klass
            || targetField.Base is not LocalVariable { Type: RuntimeClassTypeAnalysisContext } target
            || ReferenceEquals(klass, target))
            return null;

        return new ClassCompare(klass, target, klassField.Addend, ordered);
    }

    /// <summary>
    /// The block holding the second half of an inlined <c>Class::HasParent</c>, if it follows the depth
    /// check and throws to the same block, else null.
    /// </summary>
    private static Block? HierarchyCheckAfter(Block depthCheck, Block thrower, ClassCompare compare,
        Dictionary<LocalVariable, Instruction> definitions, int pointerSize)
    {
        var hierarchyCheck = depthCheck.Successors.FirstOrDefault(successor => successor != thrower);

        if (hierarchyCheck == null || hierarchyCheck == depthCheck
            || ThrowTargetOf(hierarchyCheck) != thrower
            || ConditionDefinition(hierarchyCheck, definitions) is not { OpCode: OpCode.CheckNotEqual } comparison)
            return null;

        var (element, expected) = Sides(comparison, side => side is MemoryOperand { Scale: > 0 });

        // typeHierarchy[depth - 1]: the -1 folds into the addend, so the load is
        // [hierarchy - pointerSize + depth * pointerSize].
        if (element is not MemoryOperand indexed
            || indexed.Scale != pointerSize || SignedAddend(indexed.Addend) != -pointerSize
            || indexed.Index is not LocalVariable index || indexed.Base is not LocalVariable hierarchy)
            return null;

        // The index has to be the very depth the first block compared, read off the same target...
        if (FieldRead(index, definitions) is not { } indexSource
            || indexSource.Addend != compare.Addend
            || !ReferenceEquals(indexSource.Base, compare.Target))
            return null;

        // ...the array has to be a second field read off the same object class pointer...
        if (FieldRead(hierarchy, definitions) is not { } hierarchySource
            || hierarchySource.Addend <= 0 || hierarchySource.Addend == compare.Addend
            || !ReferenceEquals(hierarchySource.Base, compare.Klass))
            return null;

        // ...and the entry compared against has to be the cast target itself. With all four tied
        // together there is no reading of this region other than an inlined HasParent.
        return DescribesSameClass(expected, compare.Target) ? hierarchyCheck : null;
    }

    // Orders a comparison's two operands so the one the caller is looking for comes first.
    private static (IOperand?, IOperand?) Sides(Instruction comparison, Func<IOperand?, bool> isFirst)
    {
        var left = comparison.Operands[1];
        var right = comparison.Operands[2];

        return isFirst(left) ? (left, right) : (right, left);
    }

    // The managed type an Il2CppClass* operand names, whether it arrives as the resolved type, as a
    // runtime class pointer, or as a local carrying either.
    private static TypeAnalysisContext? ClassOf(IOperand? operand) => operand switch
    {
        RuntimeClassTypeAnalysisContext { RepresentedType: { } represented } => represented,
        LocalVariable { Type: RuntimeClassTypeAnalysisContext { RepresentedType: { } represented } } => represented,
        RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext or StaticFieldStorageTypeAnalysisContext => null,
        TypeAnalysisContext type => type,
        _ => null,
    };

    private static bool DescribesSameClass(IOperand? operand, LocalVariable target) =>
        ClassOf(operand) is { } named && ReferenceEquals(named, ClassOf(target));

    // A negative displacement arrives zero-extended from its 32-bit encoding, so -8 reads back as
    // 0xFFFFFFF8. Nothing in an Il2CppClass lives four gigabytes in, so any such value is the negative
    // it was written as.
    private static long SignedAddend(long addend) =>
        addend is > int.MaxValue and <= uint.MaxValue ? addend - 0x1_0000_0000L : addend;

    /// <summary>
    /// The unindexed <c>[base + offset]</c> load an operand denotes, either directly or through the
    /// single Move that copied it into a local.
    ///
    /// The indirection matters because this pass runs while these values still live in SSA locals: copy
    /// propagation folds a load into its consumer only when there is exactly one, and the depth read on
    /// the target has two - the comparison and the hierarchy index - so it stays behind a local.
    /// </summary>
    private static MemoryOperand? FieldRead(IOperand? operand, Dictionary<LocalVariable, Instruction> definitions)
    {
        if (operand is LocalVariable local)
            operand = Definition(local, definitions) is { OpCode: OpCode.Move } move ? move.Operands[1] : null;

        return operand is MemoryOperand { Index: null, Scale: 0 } memory ? memory : null;
    }

    // The comparison feeding a block's conditional jump, if the condition is a local defined in that
    // same block - a condition computed elsewhere is not part of this cast region.
    private static Instruction? ConditionDefinition(Block block, Dictionary<LocalVariable, Instruction> definitions)
    {
        if (block.Instructions[^1].Operands is not [_, LocalVariable condition])
            return null;

        return Definition(condition, definitions) is { } definition && block.Instructions.Contains(definition)
            ? definition
            : null;
    }

    private static Instruction? Definition(LocalVariable local, Dictionary<LocalVariable, Instruction> definitions) =>
        definitions.TryGetValue(local, out var definition) ? definition : null;

    // Single-assignment, so one definition per local. This pass runs in SSA form for exactly that
    // reason: every operand the match walks back through has one stable definition.
    private static Dictionary<LocalVariable, Instruction> BuildDefMap(ISILControlFlowGraph cfg)
    {
        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in cfg.Instructions)
            if (instruction.Destination is LocalVariable local)
                definitions[local] = instruction;

        return definitions;
    }

    private static void Report(MethodAnalysisContext method, Block block, Block thrower, string kind)
    {
        if (!Diag)
            return;

        Count(kind);
        DumpUnmatched(method, block, thrower, kind);
    }

    // Why a guard we could not claim was rejected, so the families left behind are countable rather
    // than guessed at. Diagnostic only.
    private static string UnmatchedKind(Block block, Dictionary<LocalVariable, Instruction> definitions)
    {
        if (ConditionDefinition(block, definitions) is not { } comparison)
            return "unmatched.conditionNotLocal";

        return comparison.OpCode switch
        {
            OpCode.CheckEqual when comparison.Operands.Any(operand => operand is Immediate { Value: 0 }) => "unmatched.isinstNull",
            OpCode.CheckEqual => "unmatched.equality",
            OpCode.CheckNotEqual when comparison.Operands.Any(operand => operand is MemoryOperand { Scale: > 0 }) => "unmatched.indexed",
            OpCode.CheckNotEqual => "unmatched.inequality",
            OpCode.CheckLess or OpCode.CheckGreater or OpCode.CheckLessOrEqual or OpCode.CheckGreaterOrEqual => "unmatched.ordering",
            _ => "unmatched." + comparison.OpCode,
        };
    }

    private static void Count(string bucket)
    {
        if (Diag)
            Counts.AddOrUpdate(bucket, 1, (_, value) => value + 1);
    }

    // Prints the first few guards of a chosen kind (CPP2IL_CASTDUMP) in full, so a family that did not
    // match can be read rather than guessed at. Diagnostic only.
    private static void DumpUnmatched(MethodAnalysisContext method, Block block, Block thrower, string kind)
    {
        var wanted = Environment.GetEnvironmentVariable("CPP2IL_CASTDUMP");

        if (wanted == null || !kind.Contains(wanted) || Interlocked.Increment(ref _dumped) > 4)
            return;

        var text = new StringBuilder($"[CASTDUMP] {kind} in {method.DeclaringType?.FullName}::{method.Name}\n");

        foreach (var neighbour in new[] { block }.Concat(block.Successors).Distinct())
        {
            text.AppendLine($"[CASTDUMP]   block {neighbour.ID} [{neighbour.BlockType}]{(neighbour == thrower ? " <thrower>" : "")}");

            foreach (var instruction in neighbour.Instructions.Where(i => i.OpCode != OpCode.Nop))
                text.AppendLine($"[CASTDUMP]     {instruction.OpCode,-16} {string.Join("  |  ", instruction.Operands.Select(Describe))}");
        }

        Console.WriteLine(text.ToString());
    }

    private static string Describe(IOperand? operand) => operand switch
    {
        LocalVariable local => $"{local.Name}({local.Type?.FullName ?? "?"})",
        MemoryOperand memory => $"MEM{memory}{{base:{(memory.Base as LocalVariable)?.Type?.FullName ?? "?"}}}",
        Block target => $"Block:{target.ID}",
        _ => $"{operand?.GetType().Name}:{operand}",
    };

    private static void HookDiagOutput()
    {
        if (Interlocked.Exchange(ref _hooked, 1) != 0)
            return;

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Console.WriteLine("[CASTDIAG] guards jumping into an InvalidCastException throw block:");
            foreach (var pair in Counts.OrderByDescending(entry => entry.Value))
                Console.WriteLine($"[CASTDIAG]   {pair.Key,-30} {pair.Value}");
        };
    }
}
