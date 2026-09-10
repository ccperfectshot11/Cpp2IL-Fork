using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core;

public static class IlGenerator
{
    private const string HelpersNamespace = "Cpp2ILInjected";
    private const string HelpersTypeName = "Cpp2ILHelpers";
    private const string NoteIssueMethodName = "NoteDecompilerIssue";

    /// <summary>
    /// Cpp2ILHelpers is the last type still injected into all 150 assemblies once the attribute types are
    /// dropped, and its 149 twins account for the remaining CS0433 - 5.207 methods. Skipping the injection
    /// makes GenerateIl fall back to Console.WriteLine for the markers, which is a corlib method and so has
    /// no twin. BodyScan matches the callee name against NoteDecompilerIssue or WriteLine, so the marker
    /// measurement stays intact either way.
    /// </summary>
    public static readonly bool SkipHelpersType = Environment.GetEnvironmentVariable("CPP2IL_NO_HELPERS") == "1";

    // Emits an unfused .ctor call as newobj instead of a call. On by default (CPP2IL_CTOR_NEWOBJ=0
    // disables) - `.ctor` cannot be named as a C# member, so a call to one never compiles.
    private static readonly bool ConstructAsNewobj = Environment.GetEnvironmentVariable("CPP2IL_CTOR_NEWOBJ") != "0";

    // Casts an untyped local to what the use site expects. On by default (CPP2IL_CAST_UNTYPED=0 disables).
    private static readonly bool CastUntypedLocals = Environment.GetEnvironmentVariable("CPP2IL_CAST_UNTYPED") != "0";

    // Restores the castclass il2cpp inlined, where the value's own type says the cast went missing. On by
    // default (CPP2IL_CAST_MISMATCH=0 disables) once measured with the class read below: STRICT
    // 11,363 -> 11,677, and it empties the "cannot convert 'System.Delegate' to 'System.Action'" shape,
    // 264 methods, on its own. Clean types and class-level errors both unchanged.
    private static readonly bool CastMismatchedValues = Environment.GetEnvironmentVariable("CPP2IL_CAST_MISMATCH") != "0";

    // Reads an object's class off the object where the callee's parameter is a System.Type. On by default
    // (CPP2IL_KLASS_TYPE=0 disables): it empties the "cannot convert 'System.Delegate' to 'System.Type'"
    // shape, 228 methods, and fires at 314 argument positions.
    private static readonly bool KlassArgumentsAsType = Environment.GetEnvironmentVariable("CPP2IL_KLASS_TYPE") != "0";

    // Pushes null rather than a native zero where a reference is expected. On by default, worth 45 methods.
    private static readonly bool PlaceholderNull = Environment.GetEnvironmentVariable("CPP2IL_PLACEHOLDER_NULL") != "0";

    // Reads the primitive out of a single-field wrapper struct before arithmetic. On by default: without it
    // the IL is invalid and the JIT refuses the method. CPP2IL_UNWRAP_STRUCTS=0 disables.
    private static readonly bool UnwrapValueStructs = Environment.GetEnvironmentVariable("CPP2IL_UNWRAP_STRUCTS") != "0";

    // Types each side of a comparison from the other. Measured worse; kept so the experiment can be redone.
    private static readonly bool ComparisonTypes = Environment.GetEnvironmentVariable("CPP2IL_CMP_TYPES") == "1";

    // Gives a read out of an il2cpp runtime structure the width its use site asks for, instead of always a
    // native int. On by default (CPP2IL_META_ZERO=0 disables).
    private static readonly bool MetadataZeroWidth = Environment.GetEnvironmentVariable("CPP2IL_META_ZERO") != "0";

    // In a comparison, gives the width to the side that could not be recovered, and only to that side.
    // On by default (CPP2IL_CMP_ZERO=0 disables).
    private static readonly bool ComparisonZeroWidth = Environment.GetEnvironmentVariable("CPP2IL_CMP_ZERO") != "0";

    // Does an ordering comparison of native ints on int64. On by default (CPP2IL_NINT_CMP=0 disables).
    private static readonly bool NativeIntOrdering = Environment.GetEnvironmentVariable("CPP2IL_NINT_CMP") != "0";

    // Reads a backing field owned by another type through its property. On by default (CPP2IL_BACKING_PROP=0).
    private static readonly bool RouteBackingFields = Environment.GetEnvironmentVariable("CPP2IL_BACKING_PROP") != "0";

    /// <summary>
    /// Puts by-ref where C# puts it - on the argument, not on the value. On by default
    /// (CPP2IL_REF_ARGS=0 disables). One switch rather than two because the emission and the typing it
    /// depends on are a single decision: <see cref="Analysis.LocalVariables"/> stops calling a
    /// pointer-carrying register a byref local only because <see cref="TryLoadByReference"/> now supplies
    /// the address the call site needs, and either half on its own would trade one compile error for another.
    /// </summary>
    public static readonly bool ByRefAtCallSite = Environment.GetEnvironmentVariable("CPP2IL_REF_ARGS") != "0";

    /// <summary>
    /// Emits an explicit initobj per local at method entry. The runtime already zeroes them because
    /// InitializeLocals is set, but the decompiled C# cannot prove it and every read becomes CS0165.
    /// Costs about 22% more output, so it is only worth it when the output is meant to be recompiled.
    /// </summary>
    public static readonly bool InitialiseLocals = Environment.GetEnvironmentVariable("CPP2IL_INIT_LOCALS") == "1";

    public static void InjectHelpersType(ApplicationAnalysisContext appContext)
    {
        if (SkipHelpersType)
            return;

        var helpersType = appContext.InjectTypeIntoSharedAssembly(
            HelpersNamespace,
            HelpersTypeName,
            null,
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);

        helpersType.InjectMethodToAllAssemblies(
            NoteIssueMethodName,
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            [appContext.SystemTypes.SystemStringType]);
    }

    /// <summary>
    /// True for the single instance field a primitive struct stores itself in - System.Int32::m_value and
    /// friends. Its type is the primitive itself, which no user-defined struct can claim, so the test does
    /// not catch a real field. String and object are excluded: their internal fields hold something other
    /// than the value (length, sync block) and dropping a read of them would change what the code does.
    /// </summary>
    private static bool IsPrimitiveBackingField(FieldAnalysisContext field)
    {
        if (field.IsStatic)
            return false;

        if (field.DeclaringType.ToTypeSignature() is not CorLibTypeSignature { ElementType: var owner })
            return false;

        if (owner is AsmResolver.PE.DotNet.Metadata.Tables.ElementType.String or AsmResolver.PE.DotNet.Metadata.Tables.ElementType.Object)
            return false;

        return field.FieldType.ToTypeSignature() is CorLibTypeSignature { ElementType: var value } && value == owner;
    }

    /// <summary>
    /// il2cpp compares a reference or a pointer against literal 0, and emitting that 0 as an int gives C#
    /// like <c>x == 0</c>, which does not compile - CS0019, and the largest single cause of it by far.
    /// A reference gets ldnull, restoring the null test the source had. A pointer keeps its numeric
    /// identity but is widened to native int, so the comparison is IntPtr against IntPtr rather than int;
    /// substituting null there would change the meaning, since 0 really is IntPtr.Zero.
    /// Anything else, including bitwise arithmetic on pointers, is left exactly as it was.
    /// </summary>
    // Diagnostic-only (env CPP2IL_ZERODIAG=1): names the operand kinds compared against literal 0 that the
    // rewrite below declines to touch, so the remaining shapes can be found rather than guessed at. One line
    // per distinct kind. Zero cost when off.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> UnhandledZeroOperands = new();

    private static void ReportUnhandledZeroComparison(IOperand operand)
    {
        if (Environment.GetEnvironmentVariable("CPP2IL_ZERODIAG") != "1")
            return;

        var kind = operand.GetType().Name;
        if (UnhandledZeroOperands.TryAdd(kind, 0))
            Console.Error.WriteLine($"[zerodiag] operand comparat cu 0, netratat: {kind}");
    }

    // Diagnostic-only (env CPP2IL_METADIAG=1): counts the reads out of il2cpp runtime structures that end
    // as a fabricated zero, by structure and by offset. Two hardcoded tables in this repo disagree about
    // where Il2CppClass keeps its bitfields - Il2CppClassUsefulOffsets says flags1 is at 0x132, while
    // MetadataInitGuardRemover looks for initialized_and_no_error at 0x135 and carries a TODO saying it is
    // probably wrong for some versions - and one guess or the other is why the class-init guard survives
    // into the output as `num = (IntPtr)0 & 1; if (num == 0) throw null;`. A run with this on says which
    // offsets this binary actually reads, so the next fix can be measured rather than guessed at.
    // Zero cost when off.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long[]> MetadataReads = new();
    private static int _metaDiagHooked;

    private static void ReportMetadataRead(TypeAnalysisContext baseType, long addend)
    {
        if (Environment.GetEnvironmentVariable("CPP2IL_METADIAG") != "1")
            return;

        if (System.Threading.Interlocked.Exchange(ref _metaDiagHooked, 1) == 0)
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                Console.WriteLine("==== METADATA-READ DIAG (structura+offset: cate ori) ====");
                foreach (var entry in MetadataReads.OrderByDescending(e => e.Value[0]))
                    Console.WriteLine($"  {entry.Key,-52} {entry.Value[0],8}");
            };

        var key = $"{baseType.GetType().Name}+0x{addend:X}";
        System.Threading.Interlocked.Increment(ref MetadataReads.GetOrAdd(key, _ => new long[1])[0]);
    }

    private static bool TryEmitZeroAgainstNonInt(Instruction instruction, int operandIndex, CilInstructionCollection instructions)
    {
        if (instruction.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual))
            return false;

        if (instruction.Operands[operandIndex] is not Immediate { UnsignedValue: 0 })
            return false;

        TypeAnalysisContext? otherType;
        switch (instruction.Operands[operandIndex == 1 ? 2 : 1])
        {
            // A local whose type was never resolved is declared as object further down, so the comparison is
            // against a reference either way and the literal 0 cannot stand. Treating it as one here is what
            // makes the untyped half of these comparisons compile at all.
            case LocalVariable { Type: null }:
                instructions.Add(CilOpCodes.Ldnull);
                return true;

            case LocalVariable local:
                otherType = local.Type;
                break;

            // Fields carry a declared type just as locals do, and a field compared against 0 is the same
            // situation; leaving them out is what kept the pointer comparisons unfixed.
            case FieldReference fieldRef:
                otherType = fieldRef.Field.FieldType;
                break;

            // A string is a reference, so the 0 is a null test whatever the other side looks like.
            case StringLiteral:
                instructions.Add(CilOpCodes.Ldnull);
                return true;

            // These are all emitted as a native int further down - a raw memory read, the address of
            // something, a method pointer, a class pointer - so the zero they are compared against has to be
            // native int too, or the comparison reads as IntPtr against int and does not compile.
            case MemoryOperand or AddressOf or RuntimeMethodInfoAnalysisContext or RuntimeClassTypeAnalysisContext:
                instructions.Add(CilOpCodes.Ldc_I4_0);
                instructions.Add(CilOpCodes.Conv_I);
                return true;

            default:
                ReportUnhandledZeroComparison(instruction.Operands[operandIndex == 1 ? 2 : 1]);
                return false;
        }

        if (otherType is null)
            return false;

        if (!otherType.IsValueType)
        {
            instructions.Add(CilOpCodes.Ldnull);
            return true;
        }

        // il2cpp metadata resolves System.IntPtr as an ordinary struct rather than the native int primitive,
        // so the signature check alone misses it and the comparison stays IntPtr against int. Match the name
        // as well, otherwise the largest pointer-comparison shape never gets fixed.
        if (otherType.ToTypeSignature() is CorLibTypeSignature { ElementType: AsmResolver.PE.DotNet.Metadata.Tables.ElementType.I or AsmResolver.PE.DotNet.Metadata.Tables.ElementType.U }
            || otherType.FullName is "System.IntPtr" or "System.UIntPtr")
        {
            instructions.Add(CilOpCodes.Ldc_I4_0);
            instructions.Add(CilOpCodes.Conv_I);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Internal fields the runtime reads directly but C# cannot name, so a plain ldfld gives CS1061. They
    /// need two different treatments, and confusing them is the worst outcome: one has a public equivalent
    /// and must be mapped onto it, the other is native plumbing and must be dropped, because mapping it to
    /// anything would compile and then behave differently. The caller has already pushed the instance.
    /// Anything not listed here keeps its ldfld, so an unknown field still shows up as an error rather than
    /// being quietly guessed at.
    /// </summary>
    private static bool TryEmitInternalFieldRead(FieldAnalysisContext field, MethodDefinition definition, CilInstructionCollection instructions)
    {
        var module = definition.DeclaringModule!;
        var factory = module.CorLibTypeFactory;

        // String stores its length in a private field; the public equivalent is Length.
        if (field.Name is "_stringLength" or "m_stringLength"
            && field.DeclaringType.ToTypeSignature() is CorLibTypeSignature { ElementType: AsmResolver.PE.DotNet.Metadata.Tables.ElementType.String })
        {
            instructions.Add(CilOpCodes.Callvirt, factory.CorLibScope
                .CreateTypeReference("System", "String")
                .CreateMemberReference("get_Length", MethodSignature.CreateInstance(factory.Int32)));
            return true;
        }

        // UnityEngine.Object::m_CachedPtr is the native object pointer. Nothing in C# corresponds to it, so
        // discard the instance and yield a null pointer, the same shape the runtime-metadata reads produce.
        if (field.Name == "m_CachedPtr")
        {
            instructions.Add(CilOpCodes.Pop);
            instructions.Add(CilOpCodes.Ldc_I4_0);
            instructions.Add(CilOpCodes.Conv_I);
            return true;
        }

        return false;
    }

    public static void GenerateIl(MethodAnalysisContext context, MethodDefinition definition)
    {
        var assembly = context.DeclaringType!.DeclaringAssembly;
        var module = definition.DeclaringModule!;
        var factory = module.CorLibTypeFactory;

        // The helper now lives in one shared assembly rather than a copy per assembly, so look there first
        // and fall back to a local copy for runs that still inject per-assembly. Without this the lookup
        // silently misses and every marker degrades into a Console.WriteLine.
        var helpersHost = assembly.AppContext.AssembliesByName.TryGetValue(ApplicationAnalysisContext.SharedInjectedAssemblyName, out var sharedHost)
            ? sharedHost
            : assembly;

        var noteIssueContext = helpersHost
            .GetTypeByFullName($"{HelpersNamespace}.{HelpersTypeName}")?.Methods.FirstOrDefault(m => m.Name == NoteIssueMethodName);

        var writeLine = noteIssueContext != null
            // A definition from another module cannot be called directly; importing turns it into a member
            // reference carrying the assembly reference. Same-module definitions pass through unchanged.
            ? module.DefaultImporter.ImportMethod(noteIssueContext.ToMethodDescriptor())
            : factory.CorLibScope
                .CreateTypeReference("System", "Console")
                .CreateMemberReference("WriteLine", MethodSignature.CreateStatic(factory.Void, [factory.String]));

        // Change branch targets to instructions
        foreach (var instruction in context.ControlFlowGraph!.Blocks.SelectMany(block => block.Instructions))
        {
            if (instruction.Operands.Count > 0 && instruction.Operands[0] is Block target)
            {
                if (target.Instructions.Count > 0)
                    instruction.SetOperand(0, target.Instructions[0]);
            }
        }

        var body = new CilMethodBody()
        {
            InitializeLocals = true, // Without this ILSpy does: CompilerServices.Unsafe.SkipInit(out object obj);
            ComputeMaxStackOnBuild = false // There's stack imbalance somewhere, but this works for now
        };

        definition.CilMethodBody = body;

        // Make sure context.Locals actually has all locals (idk why it doesn't sometimes)
        foreach (var operand in context.ControlFlowGraph.Instructions.SelectMany(i => i.Operands))
        {
            LocalVariable? local = null;

            if (operand is FieldReference field)
                local = field.Local;

            if (operand is LocalVariable local2)
                local = local2;

            if (operand is MemoryOperand memory && memory.Base is LocalVariable local3)
                local = local3;

            var elementOperand = operand is AddressOf { Target: ArrayAccess elementAddress } ? elementAddress : operand;

            if (elementOperand is ArrayAccess arrayAccess)
            {
                local = arrayAccess.Array;

                if (arrayAccess.Index is LocalVariable index && !context.Locals.Contains(index))
                    context.Locals.Add(index);
            }

            if (operand is ArrayLength arrayLength)
                local = arrayLength.Array;

            if (operand is AddressOf { Target: LocalVariable addressed })
                local = addressed;

            if (local != null && !context.Locals.Contains(local))
                context.Locals.Add(local);
        }

        TypeArithmeticDestinations(context);

        // Map ISIL locals to IL
        Dictionary<LocalVariable, CilLocalVariable> locals = [];
        foreach (var local in context.Locals)
        {
            TypeSignature ilType;

            // Use object if type couldn't be determined, or if it's void, which no locals sig can hold
            if (local.Type != null && local.Type != context.AppContext.SystemTypes.SystemVoidType)
                ilType = local.Type.ToTypeSignature();
            else
                ilType = module.CorLibTypeFactory.Object;

            var ilLocal = new CilLocalVariable(ilType);
            body.LocalVariables.Add(ilLocal);
            locals.Add(local, ilLocal);
        }

        // The runtime already zeroes these because InitializeLocals is set, but the decompiled C# declares
        // them without an initialiser and the compiler cannot prove assignment across recovered control
        // flow, so every read becomes CS0165 - thousands of methods, over obj, obj2, num, flag and the rest.
        // An explicit initobj at entry states what already happens at runtime, which costs two instructions
        // per local and changes nothing about behaviour.
        // Off by default because it costs two instructions per local, about 22% more output, which buys
        // nothing for a run meant to be read rather than recompiled.
        // Held rather than emitted, because in a constructor these cannot go first - see the call to
        // EmitLocalInitialisation once the body exists.
        var localInitialisation = new List<CilInstruction>();

        if (InitialiseLocals)
        {
            foreach (var ilLocal in body.LocalVariables)
            {
                // Not a managed pointer: `initobj byte&` is legal IL but has no C# form, and the decompiler
                // renders it as `ref byte ptr = default(ref byte);` - invalid twice over, and by volume the
                // biggest syntax problem in the output (1,907 occurrences, and with it the whole
                // CS8172/CS1510/CS1073 family). A byref has nothing meaningful to be zeroed to anyway.
                if (ilLocal.VariableType is ByReferenceTypeSignature)
                    continue;

                localInitialisation.Add(new CilInstruction(CilOpCodes.Ldloca, ilLocal));
                localInitialisation.Add(new CilInstruction(CilOpCodes.Initobj, ilLocal.VariableType.ToTypeDefOrRef()));
            }
        }

        /* foreach (var instruction in context.ControlFlowGraph!.Instructions)
        {
            body.Instructions.Add(CilOpCodes.Ldstr, instruction.ToString());
            body.Instructions.Add(CilOpCodes.Call, _importer!.ImportMethod(_writeLine!));
        }
        body.Instructions.Add(CilOpCodes.Ldstr, "-------------------------------------------------------------------------");
        body.Instructions.Add(CilOpCodes.Call, _importer!.ImportMethod(_writeLine!)); */

        // Generate IL
        Dictionary<Instruction, List<CilInstruction>> instructionMap = [];
        Dictionary<Block, CilInstruction> blockEntryMap = [];
        List<(CilInstruction BranchInstruction, Block TargetBlock)> pendingBlockBranchFixups = [];

        foreach (var block in context.ControlFlowGraph!.Blocks)
        {
            if (block == context.ControlFlowGraph.EntryBlock || block == context.ControlFlowGraph.ExitBlock)
                continue;

            if (block.Instructions.Count == 0)
                continue;

            foreach (var instruction in block.Instructions)
            {
                var generated = GenerateInstructions(instruction, context, definition, locals, writeLine);
                instructionMap.Add(instruction, generated);

                if (!blockEntryMap.ContainsKey(block) && generated.Count > 0)
                    blockEntryMap[block] = generated[0];
            }

            var lastInstruction = block.Instructions.Last();
            
            if (lastInstruction.OpCode == OpCode.ConditionalJump)
            {
                var trueTarget = TryResolveJumpTargetBlock(lastInstruction, context.ControlFlowGraph);
                var falseSuccessor = block.Successors.FirstOrDefault(s => s != trueTarget && s != context.ControlFlowGraph.ExitBlock);
                if (falseSuccessor == null) continue;
                var bridge = new CilInstruction(CilOpCodes.Br, new CilInstructionLabel());
                definition.CilMethodBody!.Instructions.Add(bridge);
                pendingBlockBranchFixups.Add((bridge, falseSuccessor));
            }

            else if (lastInstruction.OpCode != OpCode.Jump && lastInstruction.OpCode != OpCode.Return && lastInstruction.OpCode != OpCode.IndirectJump)
            {
                var successor = block.Successors.FirstOrDefault(s => s != context.ControlFlowGraph.ExitBlock);
                if (successor == null) continue;
                var bridge = new CilInstruction(CilOpCodes.Br, new CilInstructionLabel());
                definition.CilMethodBody!.Instructions.Add(bridge);
                pendingBlockBranchFixups.Add((bridge, successor));
            }
        }
        // Set IL branch targets
        foreach (var kvp in instructionMap)
        {
            var instruction = kvp.Key;
            var il = kvp.Value;

            if (instruction.OpCode == OpCode.Jump || instruction.OpCode == OpCode.ConditionalJump)
            {
                var ilBranch = il.First(i => i.OpCode == CilOpCodes.Br || i.OpCode == CilOpCodes.Brtrue);

                if (instruction.Operands[0] is Block targetBlock)
                {
                    context.AddWarning($"Branch target block not in cfg: {instruction} ({targetBlock})");
                    ilBranch.OpCode = CilOpCodes.Nop;
                    ilBranch.Operand = null;
                    continue;
                }

                var target = (Instruction)instruction.Operands[0];

                if (!instructionMap.ContainsKey(target))
                {
                    context.AddWarning($"Branch target not in ISIL to IL map: {instruction} --- {target}");
                    ilBranch.OpCode = CilOpCodes.Nop;
                    ilBranch.Operand = null;
                    continue;
                }

                ilBranch.Operand = new CilInstructionLabel(instructionMap[target][0]);
            }
        }
        
        foreach (var (branchInstruction, targetBlock) in pendingBlockBranchFixups)
        {
            var target = ResolveBlockEntryInstruction(targetBlock, blockEntryMap);
            if (target == null)
            {
                context.AddWarning($"Unable to resolve branch target block: {targetBlock}");
                branchInstruction.OpCode = CilOpCodes.Nop;
                branchInstruction.Operand = null;
                continue;
            }

            branchInstruction.Operand = new CilInstructionLabel(target);
        }

        EmitLocalInitialisation(body, localInitialisation);

        // Body-wide rather than per instruction, because the box and the unbox that have to agree about an
        // untyped local are emitted at unrelated sites and neither can see the other.
        Analysis.StackCoercion.AgreeWithBox(body);

        // The method header declares how deep the evaluation stack goes, and the runtime rejects the whole
        // body when the declared depth is too small - "Stack overflow at offset 0", before a single
        // instruction runs. It is why every recovered method that takes an argument is refused by the JIT
        // while the zero-argument getters work: the getters happen to fit in the default.
        //
        // AsmResolver can compute it, but throws where the stack does not balance, which recovered bodies
        // routinely do not. So compute it when possible and fall back to a depth that is certainly enough
        // otherwise - over-declaring costs a little stack space and is always accepted, while
        // under-declaring costs the entire method.
        SetMaxStack(body);

        // Add analysis warnings
        var instructions = body.Instructions;
        foreach (var warning in context.AnalysisWarnings)
        {
            instructions.Add(CilOpCodes.Ldstr, Diagnostic("Warning: " + warning));
            instructions.Add(CilOpCodes.Call, writeLine);
        }

        Analysis.DumpDiag.MaybeDump(context, definition);
    }

    // Explicit zeroing of every local, inserted only once the body exists so it can go in the right place.
    // Everywhere but a constructor that place is the start; in a constructor the base call has to stay
    // first. The decompiler recognises `: base(...)` by the constructor call being the opening instruction,
    // and with anything ahead of it the chain is not recognised and is written as `base._ctor();` - not
    // callable C#, and 503 occurrences of it in the output.
    private static void EmitLocalInitialisation(CilMethodBody body, List<CilInstruction> initialisation)
    {
        if (initialisation.Count == 0)
            return;

        var at = 0;

        if (body.Owner.IsConstructor)
            for (var i = 0; i < body.Instructions.Count; i++)
                if (body.Instructions[i].OpCode == CilOpCodes.Call
                    && body.Instructions[i].Operand is IMethodDescriptor { Name.Value: ".ctor" })
                {
                    at = i + 1;
                    break;
                }

        body.Instructions.InsertRange(at, initialisation);
    }

    // True when the value being returned is an exception and the method's own return type is not one, which
    // means the lifter dropped a throw rather than the source really returning it. A method that genuinely
    // returns an exception - a factory, or anything returning System.Object - is left alone.
    private static bool ReturnsMisplacedException(IOperand returned, TypeAnalysisContext? returnType)
    {
        if (returned is not (LocalVariable or FieldReference) || returnType == null || IsException(returnType))
            return false;

        var returnedType = returned switch
        {
            LocalVariable local => local.Type,
            FieldReference field => field.ResultType,
            _ => null,
        };

        return returnedType != null && IsException(returnedType);
    }

    // Walks the base chain rather than matching a name: the throw helpers build ArgumentException,
    // InvalidCastException and others besides the two common ones.
    private static bool IsException(TypeAnalysisContext type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.FullName == "System.Exception")
                return true;

            // A cycle in a recovered base chain would otherwise hang the whole run.
            if (ReferenceEquals(current.BaseType, current))
                return false;
        }

        return false;
    }

    // The type one side of a comparison should be loaded as, read off the other side. Null when the other
    // side carries no type either, which leaves the caller to pick a default.
    private static TypeAnalysisContext? ComparisonOperandType(Instruction instruction, int otherIndex, MethodAnalysisContext context)
        => instruction.Operands[otherIndex] switch
        {
            LocalVariable { Type: { } type } => type,
            FieldReference field => field.ResultType,
            StringLiteral => context.AppContext.SystemTypes.SystemStringType,
            FloatLiteral => context.AppContext.SystemTypes.SystemSingleType,
            DoubleLiteral => context.AppContext.SystemTypes.SystemDoubleType,
            // Not a declared type but the one the loader will actually push, which is what the other side
            // has to match: an immediate in int range becomes ldc.i4, anything wider ldc.i8.
            Immediate { Value: >= int.MinValue and <= int.MaxValue } => context.AppContext.SystemTypes.SystemInt32Type,
            Immediate => context.AppContext.SystemTypes.SystemInt64Type,
            _ => null,
        };


    // Puts the primitive left by the arithmetic back into the wrapper the destination is declared as. Emits
    // nothing at all when the destination is not a wrapper, which is the overwhelmingly common case.
    private static void RewrapValueStruct(IOperand destination, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, IMethodDescriptor writeLine, CilInstructionCollection instructions)
    {
        if (!UnwrapValueStructs || destination is not LocalVariable { Type: { } destinationType } local)
            return;

        if (SingleValueField(destinationType) is not { } field || !locals.TryGetValue(local, out var ilLocal))
            return;

        // The value has to be parked while the destination's address goes underneath it: stfld wants the
        // address first. A scratch slot per field type is enough, and it is reused across the method.
        var scratch = ScratchLocal(method, field.FieldType.ToTypeSignature());

        // What the arithmetic left on the stack is whatever width its operands ended up with, which is not
        // always the field's - inference regularly settles on int32 for a value that is really a long. IL
        // has no implicit widening, so storing one into the other is invalid and the JIT refuses the method.
        // The conversion states the width the field actually has.
        if (field.FieldType.ToTypeSignature() is CorLibTypeSignature { ElementType: var fieldElement })
            switch (fieldElement)
            {
                case AsmResolver.PE.DotNet.Metadata.Tables.ElementType.I8 or AsmResolver.PE.DotNet.Metadata.Tables.ElementType.U8:
                    instructions.Add(CilOpCodes.Conv_I8);
                    break;
                case AsmResolver.PE.DotNet.Metadata.Tables.ElementType.R4:
                    instructions.Add(CilOpCodes.Conv_R4);
                    break;
                case AsmResolver.PE.DotNet.Metadata.Tables.ElementType.R8:
                    instructions.Add(CilOpCodes.Conv_R8);
                    break;
            }

        instructions.Add(CilOpCodes.Stloc, scratch);
        instructions.Add(CilOpCodes.Ldloca, ilLocal);
        instructions.Add(CilOpCodes.Ldloc, scratch);
        instructions.Add(CilOpCodes.Stfld, field.ToFieldDescriptor());

        // StoreToOperand runs next and expects a value to store, so hand it the wrapper it just filled in.
        instructions.Add(CilOpCodes.Ldloc, ilLocal);
    }

    // Always a fresh slot. Reusing one that merely has the right type would silently clobber a real local
    // holding a live value, which is a correctness bug traded for a handful of bytes.
    private static CilLocalVariable ScratchLocal(MethodDefinition method, TypeSignature type)
    {
        var created = new CilLocalVariable(type);
        method.CilMethodBody!.LocalVariables.Add(created);
        return created;
    }

    // Boxes a primitive result before it is stored into a local that has no type, so the store is legal and
    // the matching unbox on the way out lines up. Emits nothing when the destination is typed.
    private static void BoxIntoUntypedLocal(IOperand destination, TypeAnalysisContext? resultType, CilInstructionCollection instructions)
    {
        if (!UnwrapValueStructs || destination is not LocalVariable { Type: null } || resultType == null)
            return;

        if (resultType.ToTypeSignature() is not CorLibTypeSignature { ElementType: not AsmResolver.PE.DotNet.Metadata.Tables.ElementType.Object })
            return;

        instructions.Add(CilOpCodes.Box, resultType.ToTypeSignature().ToTypeDefOrRef());
    }

    // The primitive an arithmetic instruction leaves on the stack: the operand's own type, or the primitive
    // inside it once a single-field wrapper has been unwrapped.
    private static TypeAnalysisContext? ArithmeticResultType(Instruction instruction, MethodAnalysisContext context)
    {
        for (var i = 1; i < instruction.Operands.Count; i++)
        {
            if (OperandType(instruction.Operands[i]) is not { } operandType)
                continue;

            if (SingleValueField(operandType) is { } wrapped)
                return wrapped.FieldType;

            if (operandType.ToTypeSignature() is CorLibTypeSignature)
                return operandType;
        }

        return null;
    }

    /// <summary>
    /// Gives a local that only inference failed to type the type its arithmetic plainly produces, so it is
    /// declared as that primitive rather than as <c>object</c>.
    ///
    /// The alternative - declaring it <c>object</c> and boxing on the way in - makes the store legal and the
    /// next read illegal: `xor` then receives a boxed reference and an int64, which is how Quantum's
    /// comparison operators ended up returning the same answer for every input. A local that holds the
    /// result of `a.RawValue - b.RawValue` is a long; saying so once removes the box, the unbox, and the
    /// mismatch between them.
    ///
    /// Runs after every analysis pass has settled, so filling in a type here cannot feed back into field
    /// resolution or call resolution and change what they decided.
    /// </summary>
    private static void TypeArithmeticDestinations(MethodAnalysisContext context)
    {
        if (!UnwrapValueStructs)
            return;

        foreach (var instruction in context.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode is not (OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
                or OpCode.Modulo or OpCode.And or OpCode.Or or OpCode.Xor or OpCode.ShiftLeft or OpCode.ShiftRight))
                continue;

            if (instruction.Operands[0] is not LocalVariable { Type: null } destination)
                continue;

            if (ArithmeticResultType(instruction, context) is { } resultType)
                destination.Type = resultType;
        }
    }

    // Loads a local's address rather than its value when the local is a struct, so a field store lands on
    // the local itself. Returns whether it did; a reference-typed local needs no such thing, since the
    // value on the stack already IS the object being written into.
    private static bool LoadLocalAddressIfStruct(LocalVariable local, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        if (local.Type is not { IsValueType: true } || local.IsThis || !locals.TryGetValue(local, out var ilLocal))
            return false;

        // A parameter is not in the locals map; LoadLocal resolves those, and ldarga is its business.
        method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldloca, ilLocal);
        return true;
    }

    // The type a loaded operand leaves on the stack, which is what decides whether it needs unwrapping.
    private static TypeAnalysisContext? OperandType(IOperand operand) => operand switch
    {
        LocalVariable local => local.Type,
        FieldReference field => field.ResultType,
        _ => null,
    };

    /// <summary>
    /// The single primitive instance field a wrapper struct stores its whole value in - Photon Quantum's
    /// <c>FP</c> is a <c>long RawValue</c>, and the game is full of these. il2cpp keeps such a struct in a
    /// register exactly as if it were the primitive, so the lifter sees `add` on two of them and emits
    /// `ldarg; ldarg; add` against the struct type. That is invalid IL: the JIT refuses the method outright,
    /// which is why 607 of the 1,285 fuzzable methods with parameters throw InvalidProgramException and every
    /// argument-taking FP method fails. Reading the field first is what the original source compiled to.
    /// </summary>
    private static FieldAnalysisContext? SingleValueField(TypeAnalysisContext? type)
    {
        if (type is not { IsValueType: true } || type.IsEnumType || type.ToTypeSignature() is CorLibTypeSignature)
            return null;

        FieldAnalysisContext? only = null;

        foreach (var field in type.Fields)
        {
            if (field.IsStatic)
                continue;

            // Two fields and there is no single value to unwrap to - a vector, a struct of structs.
            if (only != null)
                return null;

            only = field;
        }

        return only?.FieldType.ToTypeSignature() is CorLibTypeSignature ? only : null;
    }

    // Reads the wrapper's value out so the arithmetic opcode gets the primitive it needs. The operand is
    // already on the stack, so this only ever appends the read.
    private static void UnwrapValueStruct(IOperand operand, TypeAnalysisContext? loadedType, CilInstructionCollection instructions)
    {
        if (!UnwrapValueStructs || operand is Immediate or FloatLiteral or DoubleLiteral)
            return;

        if (SingleValueField(loadedType) is { } field)
            instructions.Add(CilOpCodes.Ldfld, field.ToFieldDescriptor());
    }
    /// <summary>
    /// True for an operand <see cref="LoadOperand"/> cannot turn into a value at all and stands in for with
    /// a fabricated zero - a read out of an il2cpp runtime structure, or an address computation that never
    /// resolved. Whether the stand-in is marked or silent makes no difference here; what matters is that it
    /// describes nothing about the program, so the width it is pushed at is free to be chosen.
    /// </summary>
    private static bool IsUnrecoverableOperand(IOperand operand) => operand switch
    {
        // A whole-register read loads the register own local, which really is a value - so it is not a
        // placeholder, and its type must not be second-guessed.
        MemoryOperand { Index: null, Addend: 0, Scale: 0, Base: LocalVariable } => false,
        MemoryOperand => true,
        RuntimeClassTypeAnalysisContext or RgctxTableTypeAnalysisContext
            or MethodRgctxTableTypeAnalysisContext or StaticFieldStorageTypeAnalysisContext
            or RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext => true,
        _ => false,
    };

    /// <summary>
    /// The width to load one side of a comparison at, but only where that side is a fabricated zero.
    ///
    /// This is the narrow half of what <see cref="ComparisonTypes"/> did, and the difference is the whole
    /// point. Typing every operand from its opposite cost 251 strict methods because it also spoke about
    /// real values: an untyped local picked up an unbox.any int32 from <see cref="CastUntypedLocal"/>, and
    /// an AddressOf operand met the value-type branch at the top of <see cref="LoadOperand"/> and was
    /// rewritten from the address to the value, turning a pointer comparison into a value comparison.
    /// Nothing is claimed about a real operand here - the type is handed only to a value already admitted
    /// not to exist, where the width is free and native int is simply the least likely answer.
    ///
    /// Where both sides are fabricated the comparison relates nothing to nothing, and int32 is picked so
    /// the meaningless test at least compiles: IntPtr against IntPtr is 200 of the counted offending lines,
    /// and IntPtr carries no ordering operators in the corlib these assemblies are rebuilt against.
    /// </summary>
    private static TypeAnalysisContext? PlaceholderComparisonType(Instruction instruction, int index, MethodAnalysisContext context)
    {
        if (!ComparisonZeroWidth || !IsUnrecoverableOperand(instruction.Operands[index]))
            return null;

        var otherIndex = index == 1 ? 2 : 1;
        var other = instruction.Operands[otherIndex];

        // Against a literal 0, TryEmitZeroAgainstNonInt has already paired the two sides up: it pushes that
        // literal as a native int precisely because this side is one. Retyping this side would unpair them
        // and turn a comparison that compiles into one that does not.
        if (instruction.OpCode is OpCode.CheckEqual or OpCode.CheckNotEqual && other is Immediate { UnsignedValue: 0 })
            return null;

        if (IsUnrecoverableOperand(other))
            return context.AppContext.SystemTypes.SystemInt32Type;

        return ComparisonOperandType(instruction, otherIndex, context);
    }

    /// <summary>
    /// True where a comparison is an ordering between native ints, which C# cannot spell: IntPtr carries no
    /// ordering operators in the corlib these assemblies are rebuilt against, so clt on two native ints
    /// prints as IntPtr against IntPtr and does not compile - the CS0019 ordering family, 556 methods.
    ///
    /// What follows is a widening, not a retyping: the emitted comparison is signed and conv.i8
    /// sign-extends, so the int64 comparison answers the same for every input on a 32- or a 64-bit target.
    /// Nothing is asserted about the operands beyond what they already are.
    ///
    /// Both sides have to be integers for the widening to mean anything, and at least one an actual native
    /// int, or there is nothing to repair and two instructions would be spent saying so.
    /// </summary>
    private static bool IsNativeIntOrdering(Instruction instruction, TypeAnalysisContext? leftType, TypeAnalysisContext? rightType)
    {
        if (!NativeIntOrdering)
            return false;

        if (instruction.OpCode is not (OpCode.CheckGreater or OpCode.CheckLess
            or OpCode.CheckGreaterOrEqual or OpCode.CheckLessOrEqual))
            return false;

        var left = EmittedIntegerKind(instruction.Operands[1], leftType);
        var right = EmittedIntegerKind(instruction.Operands[2], rightType);

        if (left == IntegerKind.NotInteger || right == IntegerKind.NotInteger)
            return false;

        return left == IntegerKind.Native || right == IntegerKind.Native;
    }

    private enum IntegerKind { NotInteger, Sized, Native }

    /// <summary>
    /// What <see cref="LoadOperand"/> will leave on the stack for this operand, as far as widening cares.
    /// Deliberately conservative: anything not provably an integer is left alone, because a conv.i8 over a
    /// reference or a managed pointer would be a second error rather than a fix for the first.
    /// </summary>
    private static IntegerKind EmittedIntegerKind(IOperand operand, TypeAnalysisContext? expectedType)
    {
        // A fabricated zero is whatever PushPlaceholderZero was told to make it, and a native int when it
        // was told nothing.
        if (IsUnrecoverableOperand(operand))
            return expectedType == null ? IntegerKind.Native : IntegerKindOf(expectedType);

        return operand switch
        {
            Immediate => IntegerKind.Sized,
            LocalVariable { Type: { } type } => IntegerKindOf(type),
            FieldReference field => IntegerKindOf(field.ResultType),
            _ => IntegerKind.NotInteger,
        };
    }

    private static IntegerKind IntegerKindOf(TypeAnalysisContext type) => type.FullName switch
    {
        // il2cpp metadata resolves IntPtr as an ordinary struct rather than the native int primitive, so
        // the name is the only thing that identifies it - the same reason TryEmitZeroAgainstNonInt has to
        // match on it by name.
        "System.IntPtr" or "System.UIntPtr" => IntegerKind.Native,
        "System.SByte" or "System.Byte" or "System.Int16" or "System.UInt16" or "System.Int32"
            or "System.UInt32" or "System.Int64" or "System.UInt64" or "System.Char" => IntegerKind.Sized,
        _ => IntegerKind.NotInteger,
    };

    private static void SetMaxStack(CilMethodBody body)
    {
        try
        {
            // Two separate reasons the old call was wrong, and the parameterless overload fixes both.
            // It recalculates offsets first: left stale, the walk follows branches to an address where the
            // target no longer sits, measures a path the body does not have, and can come back short -
            // everything inserted after emission, StackCoercion included, moves every offset behind it.
            // And it throws on an unbalanced stack instead of returning a depth computed from a stack it
            // already knows does not close, which is the too-small number the runtime rejects, returned
            // silently, so the fallback below never got a chance to run.
            body.MaxStack = body.ComputeMaxStack();
        }
        catch (Exception)
        {
            // One push per instruction is an upper bound no real body can exceed, since no CIL instruction
            // pushes more than one value. Eight keeps a trivial body from declaring nothing at all, and the
            // ceiling is the header field's own: MaxStack is a ushort, so a body with more than 65,535
            // instructions would otherwise wrap to a tiny number and be refused for the original reason.
            body.MaxStack = Math.Min(ushort.MaxValue, Math.Max(8, body.Instructions.Count));
        }
    }

    // Limit so we don't run into the 16mb limit (see AsmResolver issue #775)
    private static string Diagnostic(string message) 
        => message.Length <= 250 ? message : message[..250] + "…";
    
    private static Block? TryResolveJumpTargetBlock(Instruction jumpInstruction, ISILControlFlowGraph cfg)
    {
        if (jumpInstruction.Operands.Count == 0)
            return null;

        if (jumpInstruction.Operands[0] is Block targetBlock)
            return targetBlock;

        if (jumpInstruction.Operands[0] is Instruction targetInstruction)
            return cfg.FindBlockByInstruction(targetInstruction);

        return null;
    }

    private static CilInstruction? ResolveBlockEntryInstruction(Block block,
        Dictionary<Block, CilInstruction> blockEntryMap, HashSet<Block>? visited = null)
    {
        if (blockEntryMap.TryGetValue(block, out var target))
            return target;

        visited ??= [];
        if (!visited.Add(block))
            return null;

        foreach (var successor in block.Successors)
        {
            var resolved = ResolveBlockEntryInstruction(successor, blockEntryMap, visited);
            if (resolved != null)
                return resolved;
        }
        return null;
    }

    private static List<CilInstruction> GenerateInstructions(Instruction instruction, MethodAnalysisContext context,
        MethodDefinition method, Dictionary<LocalVariable, CilLocalVariable> locals, IMethodDescriptor writeLine)
    {
        var body = method.CilMethodBody!;
        var instructions = body.Instructions;
        var currentCount = instructions.Count;
        var startIndex = instructions.Count;

        var module = method.DeclaringModule!;

        switch (instruction.OpCode)
        {
            case OpCode.Invalid:
                instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Invalid instruction: {instruction}"));
                instructions.Add(CilOpCodes.Call, writeLine);
                break;

            case OpCode.NotImplemented:
                instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Not implemented instruction: {instruction.Operands[0]}"));
                instructions.Add(CilOpCodes.Call, writeLine);
                break;

            case OpCode.Interrupt:
            case OpCode.Nop:
                instructions.Add(CilOpCodes.Nop);
                break;

            case OpCode.Move:
                if (instruction.Operands[0] is FieldReference field) // stfld takes instance before value so LoadOperand StoreToOperand doesn't work
                {
                    // for a.b.c the store lands on the innermost field
                    var written = field.InnerPath.Length > 0 ? field.InnerPath[^1] : field.Field;

                    if (!written.IsStatic)
                    {
                        // The same reasoning the InnerPath below already applies, one level up: a struct
                        // loaded by value is a copy, and stfld into a copy writes somewhere that is thrown
                        // away - and at runtime it is worse than useless, because the value on the stack is
                        // not a reference, so the store faults. FPMatrix3x3::get_Zero sets five fields on a
                        // copy and returns the untouched original; nine Quantum methods failed exactly here.
                        if (!LoadLocalAddressIfStruct(field.Local, method, locals))
                            LoadLocal(field.Local, method, locals);

                        if (field.InnerPath.Length > 0)
                        {
                            // ldflda, not ldfld: writing through a copy of the struct would be discarded
                            instructions.Add(CilOpCodes.Ldflda, field.Field.ToFieldDescriptor());

                            for (var nested = 0; nested < field.InnerPath.Length - 1; nested++)
                                instructions.Add(CilOpCodes.Ldflda, field.InnerPath[nested].ToFieldDescriptor());
                        }
                    }

                    LoadOperand(instruction.Operands[1], method, locals, writeLine, written.FieldType);
                    instructions.Add(written.IsStatic ? CilOpCodes.Stsfld : CilOpCodes.Stfld, written.ToFieldDescriptor());
                    break;
                }

                // stelem needs array and index before the value, so like stfld it can't go through LoadOperand/StoreToOperand.
                // This also lets ILSpy handle it as a proper array initializer
                if (instruction.Operands[0] is ArrayAccess { Array.Type: SzArrayTypeAnalysisContext { ElementType: { } stored } } target)
                {
                    LoadLocal(target.Array, method, locals);
                    LoadOperand(target.Index, method, locals, writeLine);
                    LoadOperand(instruction.Operands[1], method, locals, writeLine, stored);
                    instructions.Add(CilOpCodes.Stelem, stored.ToTypeSignature().ToTypeDefOrRef());
                    break;
                }

                LoadOperand(instruction.Operands[1], method, locals, writeLine, DestinationType(instruction.Operands[0]));
                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            case OpCode.NewArr:
                if (instruction.Operands is [_, SzArrayTypeAnalysisContext { ElementType: { } newArrayElement }, { } length])
                {
                    LoadOperand(length, method, locals, writeLine);
                    instructions.Add(CilOpCodes.Newarr, newArrayElement.ToTypeSignature().ToTypeDefOrRef());
                }
                else
                    instructions.Add(CilOpCodes.Ldnull);

                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            case OpCode.Newobj:
                // Try and fuse our Newobj + the follow up constructor CallVoid into one IL newobj.
                // If we can't, just fall back to an Ldnull.
                if (FindConstructorCall(context, instruction) is { Operands: [MethodAnalysisContext constructor, _, ..] } constructorCall)
                {
                    // Operands run [ctor, newObject, arguments..., methodInfo], so take only as many as
                    // the constructor declares (i.e. drop methodInfo)
                    var constructorArgs = constructorCall.Operands.Skip(ConstructorReceiverIndex(constructorCall) + 1).Take(constructor.Parameters.Count).ToList();
                    for (var i = 0; i < constructorArgs.Count; i++)
                        LoadOperand(constructorArgs[i], method, locals, writeLine, constructor.Parameters[i].ParameterType);

                    instructions.Add(CilOpCodes.Newobj, constructor.ToMethodDescriptor());
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);

                    constructorCall.OpCode = OpCode.Nop;
                    constructorCall.SetOperands();
                }
                else if (instruction.Operands is [_, TypeAnalysisContext allocatedType] && allocatedType.Methods.FirstOrDefault(m => m is { Name: ".ctor", Parameters.Count: 0 }) is { } parameterlessCtor)
                {
                    // Nothing to fuse with, so the allocation was self-contained. The type is still right, so construct it bare.
                    instructions.Add(CilOpCodes.Newobj, parameterlessCtor.ToMethodDescriptor());
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                }
                else
                {
                    instructions.Add(CilOpCodes.Ldnull);
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                }
                break;

            case OpCode.Box:
                if (instruction.Operands is [_, TypeAnalysisContext boxedType, var boxedValue])
                {
                    // il2cpp_value_box takes the value by address, but IL boxes it by value
                    LoadOperand(boxedValue is AddressOf { Target: LocalVariable byRef } ? byRef : boxedValue, method, locals, writeLine, boxedType);
                    instructions.Add(CilOpCodes.Box, boxedType.ToTypeSignature().ToTypeDefOrRef());
                }
                else
                    instructions.Add(CilOpCodes.Ldnull);

                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            case OpCode.Throw:
                if (instruction.Operands is [TypeAnalysisContext exceptionType]
                    && exceptionType.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 0) is { } exceptionCtor)
                    instructions.Add(CilOpCodes.Newobj, exceptionCtor.ToMethodDescriptor());
                else if (instruction.Operands is [LocalVariable or FieldReference])
                    LoadOperand(instruction.Operands[0], method, locals, writeLine); // an already-constructed exception
                else
                    instructions.Add(CilOpCodes.Ldnull);

                instructions.Add(CilOpCodes.Throw);
                break;

            case OpCode.Phi:
                instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Phi opcodes should not exist at this point in decompilation ({instruction})"));
                instructions.Add(CilOpCodes.Call, writeLine);
                break;

            case OpCode.Call:
            case OpCode.CallVoid:
                if (instruction.Operands[0] is not MethodAnalysisContext targetMethod)
                {
                    if (instruction.Operands[0] is Immediate targetAddress)
                    {
                        Analysis.MarkerDiag.RecordMnf(targetAddress.UnsignedValue, context.AppContext, instruction.OpCode == OpCode.CallVoid);
                        instructions.Add(CilOpCodes.Ldstr, $"Method not found @{targetAddress.UnsignedValue:X}");
                    }
                    else // Probably key function. Just the target, the full operand dump is huge and blows the 16MB #US heap limit
                        instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Unknown call target operand: {instruction.Operands[0]}"));

                    instructions.Add(CilOpCodes.Call, writeLine);
                    break;
                }

                var importedMethod = targetMethod.ToMethodDescriptor();

                // A .ctor that reaches us as a plain call is a construction that never got fused with an
                // allocation: il2cpp creates delegates (and a handful of other runtime types) with no ISIL
                // Newobj at all, so FindConstructorCall has nothing to pair the call with. Emitted as a
                // call it decompiles to `x._ctor(...)` - `.ctor` is not a nameable C# member - which is the
                // largest remaining compile-error cause, 2,880 methods. newobj says what actually happened.
                if (targetMethod is { Name: ".ctor" } && ConstructAsNewobj && !IsChainedConstructorCall(context, instruction))
                {
                    var receiverOperand = ConstructorReceiverIndex(instruction);
                    var constructorArgIndex = receiverOperand + 1;
                    var suppliedArgs = instruction.Operands.Count - constructorArgIndex;

                    for (var i = 0; i < targetMethod.Parameters.Count; i++)
                    {
                        var parameterType = targetMethod.Parameters[i].ParameterType;

                        if (i < suppliedArgs)
                            LoadOperand(instruction.Operands[constructorArgIndex + i], method, locals, writeLine, parameterType);
                        else
                            PushDefaultOf(parameterType, method, instructions);
                    }

                    instructions.Add(CilOpCodes.Newobj, importedMethod);

                    // The receiver is the local il2cpp allocated into, so it is where the new object goes.
                    if (instruction.Operands.Count > receiverOperand)
                        StoreToOperand(instruction.Operands[receiverOperand], method, locals, writeLine);
                    else
                        instructions.Add(CilOpCodes.Pop);

                    break;
                }

                var thisParamIndex = instruction.OpCode == OpCode.Call ? 2 : 1;

                if (!targetMethod.IsStatic) // Load 'this' param
                {
                    if ((instruction.Operands.Count - 1) >= thisParamIndex)
                        LoadOperand(instruction.Operands[thisParamIndex], method, locals, writeLine, targetMethod.DeclaringType);
                    else
                    {
                        instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Non static method called without 'this' param ({instruction})"));
                        instructions.Add(CilOpCodes.Call, writeLine);
                        instructions.Add(CilOpCodes.Ldnull);
                    }
                }

                // Load normal params
                var callParamIndex = instruction.OpCode == OpCode.Call ? (targetMethod.IsStatic ? 2 : 3) : (targetMethod.IsStatic ? 1 : 2);
                // A call whose target was only identified after lifting still carries the operands the
                // unknown-callee convention gave it, which may be fewer than the method actually takes.
                // The stack still has to match the signature, so anything missing gets a placeholder.
                var availableArgs = instruction.Operands.Count - callParamIndex;
                for (var i = 0; i < targetMethod.Parameters.Count; i++)
                {
                    var parameterType = targetMethod.Parameters[i].ParameterType;

                    if (i < availableArgs)
                        LoadOperand(instruction.Operands[callParamIndex + i], method, locals, writeLine, parameterType);
                    else
                        PushDefaultOf(parameterType, method, instructions);
                }

                instructions.Add(CilOpCodes.Call, importedMethod);

                // the lifter's guess at whether the callee returns anything can disagree with the
                // signature we later resolved, so go by the signature and balance the stack
                if (!targetMethod.IsVoid)
                {
                    if (instruction.OpCode == OpCode.Call)
                        StoreToOperand(instruction.Operands[1], method, locals, writeLine);
                    else
                        instructions.Add(CilOpCodes.Pop);
                }

                break;

            case OpCode.IndirectCall:
                instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Indirect call: {instruction.Operands[0]} (should have been resolved before IL gen)"));
                instructions.Add(CilOpCodes.Call, writeLine);
                break;

            case OpCode.Return:
                // Returning an exception from a method that does not return one is not what the original
                // did - it threw. il2cpp emits its null and bounds checks as a branch that builds the
                // exception and hands it to a throw helper, and where the lifter loses the helper call the
                // tail looks like an ordinary return of the value that was just constructed. Restoring the
                // throw is both what the source said and the only form that compiles: 968 methods return a
                // NullReferenceException and 77 an IndexOutOfRangeException, all CS0029.
                if (!context.IsVoid && instruction.Operands.Count == 1
                    && ReturnsMisplacedException(instruction.Operands[0], context.ReturnType))
                {
                    LoadOperand(instruction.Operands[0], method, locals, writeLine);
                    instructions.Add(CilOpCodes.Throw);
                    break;
                }

                if (!context.IsVoid)
                {
                    if (instruction.Operands.Count == 1)
                        LoadOperand(instruction.Operands[0], method, locals, writeLine, context.ReturnType);
                    else
                        instructions.Add(CilOpCodes.Ldnull); // ret still pops a value even if we lost track of it
                }
                instructions.Add(CilOpCodes.Ret);
                break;

            case OpCode.Jump:
                instructions.Add(CilOpCodes.Br, new CilInstructionLabel());
                break;

            case OpCode.ConditionalJump:
                LoadOperand(instruction.Operands[1], method, locals, writeLine);
                instructions.Add(CilOpCodes.Brtrue, new CilInstructionLabel());
                break;

            case OpCode.IndirectJump:
                instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Indirect jump: {instruction.Operands[0]} (should have been resolved before IL gen)"));
                instructions.Add(CilOpCodes.Call, writeLine);
                break;

            case OpCode.ShiftStack:
                instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Stack shift: {instruction} (stack analysis should have removed these)"));
                instructions.Add(CilOpCodes.Call, writeLine);
                break;

            case OpCode.CheckEqual:
            case OpCode.CheckGreater:
            case OpCode.CheckLess:
            case OpCode.CheckNotEqual:
            case OpCode.CheckGreaterOrEqual:
            case OpCode.CheckLessOrEqual:

            case OpCode.Add:
            case OpCode.Subtract:
            case OpCode.Multiply:
            case OpCode.Divide:
            case OpCode.Modulo:

            case OpCode.ShiftLeft:
            case OpCode.ShiftRight:

            case OpCode.And:
            case OpCode.Or:
            case OpCode.Xor:
                // klass pointer read => GetType
                if (instruction.OpCode is OpCode.CheckEqual or OpCode.CheckNotEqual
                    && TryEmitExactTypeComparison(instruction, method, locals, writeLine))
                    break;

                // Float arithmetic on a promoted integer operand needs an explicit conversion, so both
                // operands are coerced to the (float) result type. A no-op when they already match.
                var floatConversion = FloatArithmeticConversion(instruction);

                // What the result is typed as is what the operands have to be, and telling the operand
                // loader so is what lets an unrecovered value be pushed at the right width and an untyped
                // local be cast instead of staying `object`. Only for arithmetic: a comparison's result is
                // bool, which says nothing at all about the two things being compared.
                var isComparison = instruction.OpCode is >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqual;

                // A comparison's result is bool, which says nothing about the two things being compared, so
                // each side takes its expected type from the other instead. Where neither side knows, an
                // int: two unrecovered values compared as native ints decompile to
                // `(IntPtr)0 >= (IntPtr)0`, which is 803 methods of CS0019 for a comparison that is
                // meaningless either way - as ints it at least compiles.
                // MEASURED HARMFUL, off by default (CPP2IL_CMP_TYPES=1 to retry): typing each side of a
                // comparison from the other cost 251 strict methods. Marker-free strict rose, so it does fix
                // the `(IntPtr)0 >= (IntPtr)0` shape - but forcing a type onto a comparison whose operands
                // are genuinely unknown breaks more methods elsewhere than it repairs.
                var leftType = !isComparison
                    ? DestinationType(instruction.Operands[0])
                    : ComparisonTypes
                        ? ComparisonOperandType(instruction, 2, context) ?? context.AppContext.SystemTypes.SystemInt32Type
                        // Only the side that could not be recovered is spoken for; see
                        // PlaceholderComparisonType for why the wider version of this was harmful.
                        : PlaceholderComparisonType(instruction, 1, context);
                var operandType = leftType;

                var rightType = instruction.OpCode switch
                {
                    // A shift's second operand is the count, always an int32, never the result type.
                    OpCode.ShiftLeft or OpCode.ShiftRight => null,
                    _ when isComparison => ComparisonTypes
                        ? ComparisonOperandType(instruction, 1, context) ?? leftType
                        : PlaceholderComparisonType(instruction, 2, context),
                    _ => operandType,
                };

                // Hoisted above the loads because the widening has to know what both sides will be before
                // either of them is emitted. It never collides with TryEmitZeroAgainstNonInt below, which
                // emits one side by itself: that only ever fires on == and !=, and this only on orderings.
                var wideningConversion = IsNativeIntOrdering(instruction, leftType, rightType) ? CilOpCodes.Conv_I8 : (CilOpCode?)null;

                if (!TryEmitZeroAgainstNonInt(instruction, 1, instructions))
                {
                    LoadOperand(instruction.Operands[1], method, locals, writeLine, leftType);
                    UnwrapValueStruct(instruction.Operands[1], OperandType(instruction.Operands[1]), instructions);
                    if (floatConversion is { } conv1)
                        instructions.Add(conv1);
                    if (wideningConversion is { } widen1)
                        instructions.Add(widen1);
                }

                if (!TryEmitZeroAgainstNonInt(instruction, 2, instructions))
                {
                    LoadOperand(instruction.Operands[2], method, locals, writeLine, rightType);
                    UnwrapValueStruct(instruction.Operands[2], OperandType(instruction.Operands[2]), instructions);
                    if (floatConversion is { } conv2)
                        instructions.Add(conv2);
                    if (wideningConversion is { } widen2)
                        instructions.Add(widen2);
                }

                switch (instruction.OpCode)
                {
                    case OpCode.CheckEqual: instructions.Add(CilOpCodes.Ceq); break;
                    case OpCode.CheckGreater: instructions.Add(CilOpCodes.Cgt); break;
                    case OpCode.CheckLess: instructions.Add(CilOpCodes.Clt); break;

                    // a != b  ==  (a == b) == 0
                    case OpCode.CheckNotEqual:
                        instructions.Add(CilOpCodes.Ceq);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;
                    // a >= b  ==  !(a < b)
                    case OpCode.CheckGreaterOrEqual:
                        instructions.Add(CilOpCodes.Clt);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;
                    // a <= b  ==  !(a > b)
                    case OpCode.CheckLessOrEqual:
                        instructions.Add(CilOpCodes.Cgt);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;

                    case OpCode.Add: instructions.Add(CilOpCodes.Add); break;
                    case OpCode.Subtract: instructions.Add(CilOpCodes.Sub); break;
                    case OpCode.Multiply: instructions.Add(CilOpCodes.Mul); break;
                    case OpCode.Divide: instructions.Add(CilOpCodes.Div); break;
                    case OpCode.Modulo: instructions.Add(CilOpCodes.Rem); break;

                    case OpCode.ShiftLeft: instructions.Add(CilOpCodes.Shl); break;
                    case OpCode.ShiftRight: instructions.Add(CilOpCodes.Shr); break;

                    case OpCode.And: instructions.Add(CilOpCodes.And); break;
                    case OpCode.Or: instructions.Add(CilOpCodes.Or); break;
                    case OpCode.Xor: instructions.Add(CilOpCodes.Xor); break;
                }

                // The operands were unwrapped to their primitive, so the result is a primitive too, and the
                // destination is not. Rewrapping keeps both halves consistent; without it the store is the
                // invalid IL the unwrap was there to remove.
                if (!isComparison)
                    RewrapValueStruct(instruction.Operands[0], method, locals, writeLine, instructions);

                // A local the inference never typed is declared `object`, and a primitive cannot be stored
                // into one unboxed - that is invalid IL, so the JIT refuses the whole method. It is the
                // reason arithmetic-carrying methods are unrunnable even when they compile: FPVector2::Dot
                // multiplies two longs and stores the result into an `object` slot. Boxing is also what the
                // reads further down already assume, since CastUntypedLocal unboxes them again.
                BoxIntoUntypedLocal(instruction.Operands[0], ArithmeticResultType(instruction, context), instructions);

                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            case OpCode.Not:
            case OpCode.Negate:
                LoadOperand(instruction.Operands[1], method, locals, writeLine);

                if (instruction.OpCode == OpCode.Negate)
                    instructions.Add(CilOpCodes.Neg);
                else if (IsBoolean(instruction.Operands[1], context))
                {
                    instructions.Add(CilOpCodes.Ldc_I4_0);
                    instructions.Add(CilOpCodes.Ceq);
                }
                else
                    instructions.Add(CilOpCodes.Not);

                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            default:
                instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Unknown instruction: {instruction}"));
                instructions.Add(CilOpCodes.Call, writeLine);
                break;
        }

        // Everything above emits the load one site asks for and the store another asks for, with no notion
        // of what is on the stack in between, so the two regularly disagree about type. This is where the
        // pair is reconciled, on the group that was just emitted rather than on the body: that group starts
        // and ends with an empty stack by construction, which is what makes the walk exact.
        Analysis.StackCoercion.Reconcile(body, startIndex);

        return instructions.ToList().GetRange(startIndex, instructions.Count - startIndex); // Return added IL
    }
    

    // A local the inference never typed is declared `object`, so every use of it as anything else - a
    // receiver, an arithmetic operand, an argument - fails to compile even though the site using it knows
    // exactly what the type has to be. The cast just states what the caller already established, and it is
    // what turns `obj.get_position_Injected(...)` (CS1061 on object) into
    // `((Transform)obj).get_position_Injected(...)`.
    //
    // unbox.any rather than castclass: it is correct for reference types, value types and generic
    // parameters alike, and decompiles to the same `(T)x` in every case. A byref or pointer target is
    // skipped - neither is castable, and a managed pointer is not what an untyped local is standing in for.
    private static void CastUntypedLocal(LocalVariable local, TypeAnalysisContext? expectedType, CilInstructionCollection instructions)
    {
        if (!CastUntypedLocals || local.Type != null || expectedType == null)
            return;

        if (expectedType is ByRefTypeAnalysisContext || expectedType.FullName is "System.Object" or "System.Void")
            return;

        // A context with no AsmResolver type behind it cannot be named in IL at all, and ToTypeSignature
        // throws rather than returning null. The cast is an improvement, never a requirement - the local
        // still loads correctly without it - so a type we cannot name is a reason to skip it, not to fail
        // the whole method.
        if (expectedType.GetExtraData<TypeDefinition>("AsmResolverType") == null && expectedType is not ReferencedTypeAnalysisContext)
            return;

        instructions.Add(CilOpCodes.Unbox_Any, expectedType.ToTypeSignature().ToTypeDefOrRef());
    }

    /// <summary>
    /// Puts back the cast il2cpp inlined. A C# <c>(Foo)x</c> leaves no castclass in the binary: the check
    /// becomes a class-pointer compare and the cast itself becomes a plain move of the object, so the value
    /// reaching the destination still carries the type it had before the cast - <c>System.Delegate</c> for
    /// everything Delegate.Combine produced, the object's own type for the rest. The destination is declared
    /// as the cast's target, so the store is a mismatch: <c>this.DeeplinkOpened = delegate4;</c> against an
    /// Action field, 292 sites for plain Action and about 450 counting the generic Action&lt;T&gt;s.
    ///
    /// castclass is the instruction that was lost, and it re-checks exactly what the excised guard checked,
    /// so this restores the program rather than papering over the mismatch: on the path the guard admits,
    /// the value really is a Foo and the cast costs nothing at runtime.
    ///
    /// Only where a cast could have been written. A conversion between two unrelated types is not a lost
    /// cast but a typing mistake further up, and C# rejects it outright (CS0030), so a sibling mismatch is
    /// left visible instead of being traded for a different error.
    /// </summary>
    private static void CastMismatchedValue(IOperand operand, TypeAnalysisContext? expectedType, CilInstructionCollection instructions)
    {
        if (!CastMismatchedValues || expectedType == null || SourceType(operand) is not { } sourceType)
            return;

        if (!IsCastableReference(sourceType) || !IsCastableReference(expectedType))
            return;

        if (IsAssignableTo(sourceType, expectedType) || !CastExists(sourceType, expectedType))
            return;

        // The same guard CastUntypedLocal uses: a context with no AsmResolver type behind it cannot be named
        // in IL at all, and ToTypeSignature throws rather than returning null.
        if (expectedType.GetExtraData<TypeDefinition>("AsmResolverType") == null && expectedType is not ReferencedTypeAnalysisContext)
            return;

        instructions.Add(CilOpCodes.Castclass, expectedType.ToTypeSignature().ToTypeDefOrRef());
    }

    /// <summary>
    /// The static type of the value an operand loads, for the two shapes that carry one. Everything else -
    /// an address, a raw memory read, a literal - either has no type that could disagree with the use site
    /// or is already loaded as exactly what the use site asked for.
    /// </summary>
    private static TypeAnalysisContext? SourceType(IOperand operand) => operand switch
    {
        // `this` is the declaring type by definition, so it never needs converting to reach one.
        LocalVariable { IsThis: false, Type: { } type } => type,
        FieldReference field => field.ResultType,
        _ => null,
    };

    // Ordinary reference types only. A value type needs a conversion rather than a cast, an array is
    // assignable by covariance rules the hierarchy walk below cannot see, and the synthetic il2cpp contexts
    // and generic parameters are not types a castclass could name.
    private static bool IsCastableReference(TypeAnalysisContext type) =>
        !type.IsValueType
        && type is not (ByRefTypeAnalysisContext or PointerTypeAnalysisContext or SzArrayTypeAnalysisContext
            or ArrayTypeAnalysisContext or GenericParameterTypeAnalysisContext or RuntimeClassTypeAnalysisContext
            or RgctxTableTypeAnalysisContext or MethodRgctxTableTypeAnalysisContext or StaticFieldStorageTypeAnalysisContext
            or RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext);

    // Whether C# would accept an explicit cast between the two at all: a downcast, or anything involving an
    // interface or System.Object. Two unrelated classes have no conversion in either direction.
    private static bool CastExists(TypeAnalysisContext from, TypeAnalysisContext to) =>
        from.FullName == "System.Object" || from.IsInterface || to.IsInterface || IsAssignableTo(to, from);

    /// <summary>
    /// Whether a value of <paramref name="from"/> can be stored as <paramref name="to"/> with no cast.
    ///
    /// Where a link cannot be resolved the answer is "not known to be assignable", which costs a castclass
    /// that does nothing at runtime when the value does fit. The opposite default would leave the mismatch
    /// in place, and the mismatch is the error being fixed.
    /// </summary>
    private static bool IsAssignableTo(TypeAnalysisContext from, TypeAnalysisContext to)
        => to.FullName == "System.Object" || Reaches(from, to.FullName, [], 0);

    private static bool Reaches(TypeAnalysisContext type, string? target, HashSet<string> seen, int depth)
    {
        // A recovered base chain can cycle and an interface list fans out, so both are bounded.
        if (depth > 16 || target == null || type.FullName is not { } name || !seen.Add(name))
            return false;

        if (name == target)
            return true;

        foreach (var iface in type.InterfaceContexts)
            if (iface != null && Reaches(iface, target, seen, depth + 1))
                return true;

        // A generic instance has no definition behind it, so its base type and its interface list both come
        // back empty. The open form has them, and the ancestry a cast is decided on is the same for every
        // instantiation: Action<string> is a Delegate because Action`1 is.
        if (type is GenericInstanceTypeAnalysisContext { GenericType: { } openForm } && Reaches(openForm, target, seen, depth + 1))
            return true;

        return type.BaseType is { } baseType && !ReferenceEquals(baseType, type) && Reaches(baseType, target, seen, depth + 1);
    }

    /// <summary>
    /// Loads an object's class where the callee asked for a <see cref="System.Type"/>.
    ///
    /// il2cpp hands these methods an <c>Il2CppClass*</c>, and for the object being tested it reads that
    /// pointer straight off the object - <c>[obj + 0]</c>, the first field of every Il2CppObject. The read
    /// does survive lifting, as a zero-offset memory operand, but the general case below loads such an
    /// operand as its base local, so the object arrives where its class was wanted: an <c>x as Action</c>
    /// comes out as <c>RuntimeTypeHandle.type_is_assignable_from(typeof(Action), someDelegate)</c>, CS1503,
    /// and that one callee accounts for 922 of these arguments.
    ///
    /// GetType() is the class pointer the native code passed, which makes this a recovery rather than a
    /// silencing - and it is the same substitution <see cref="TryEmitExactTypeComparison"/> already makes for
    /// the sibling shape, where the check is a compare instead of a call.
    /// </summary>
    private static bool TryLoadClassOfObject(IOperand operand, TypeAnalysisContext? expectedType, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        if (!KlassArgumentsAsType || expectedType?.FullName != "System.Type")
            return false;

        // The shape IsKlassPointerLoad matches, with an untyped base admitted as well: a slot nothing could
        // type is emitted as System.Object, and GetType() is valid on that. A byref base is excluded because
        // there the zero offset is a real dereference of a managed pointer rather than a class read, and so
        // are the il2cpp runtime structures, whose reads are already handled as metadata.
        if (operand is not MemoryOperand { Index: null, Addend: 0, Scale: 0, Base: LocalVariable objLocal })
            return false;

        if (objLocal.Type is { } baseType && (baseType.IsValueType || baseType is ByRefTypeAnalysisContext || IsRuntimeMetadata(baseType)))
            return false;

        var corLibScope = method.DeclaringModule!.CorLibTypeFactory.CorLibScope;

        LoadLocal(objLocal, method, locals);
        method.CilMethodBody!.Instructions.Add(CilOpCodes.Callvirt, corLibScope
            .CreateTypeReference("System", "Object")
            .CreateMemberReference("GetType", MethodSignature.CreateInstance(
                corLibScope.CreateTypeReference("System", "Type").ToTypeSignature(false))));

        return true;
    }

    // il2cpp inlines property accessors, so the game's own code reads <X>k__BackingField directly. That
    // name is not a C# identifier, the decompiler prints a sanitised spelling of it, and across assemblies
    // that spelling matches nothing - the largest remaining CS1061 shape. The property it backs carries the
    // name the original source actually used, so route the read back through it: `runner.Game`.
    //
    // Only across types. Inside the declaring type a direct field access is exactly what the original
    // source compiles to, and leaving it alone is what lets the decompiler collapse the pair back into
    // `{ get; set; }` - renaming the field instead would break that and make the output less like the
    // original project, not more.
    //
    // Value types are skipped: an instance call on one needs the address, and the receiver here is a value.
    private static bool TryEmitBackingFieldRead(FieldAnalysisContext field, MethodDefinition method, CilInstructionCollection instructions)
    {
        if (!RouteBackingFields || field.DeclaringType.IsValueType)
            return false;

        if (field.DeclaringType.FullName == method.DeclaringType?.FullName)
            return false;

        if (BackedPropertyName(field.Name) is not { } propertyName)
            return false;

        var getter = field.DeclaringType.Methods.FirstOrDefault(m => m.Name == "get_" + propertyName && m.Parameters.Count == 0);

        if (getter == null)
            return false;

        instructions.Add(CilOpCodes.Callvirt, getter.ToMethodDescriptor());
        return true;
    }

    // "<Game>k__BackingField" -> "Game"
    private static string? BackedPropertyName(string fieldName)
    {
        const string suffix = ">k__BackingField";

        if (fieldName.Length <= suffix.Length + 1 || fieldName[0] != '<' || !fieldName.EndsWith(suffix, StringComparison.Ordinal))
            return null;

        return fieldName.Substring(1, fieldName.Length - suffix.Length - 1);
    }

    // typeof(T) -> typeof(T).TypeHandle.Value, turning a System.Type on the stack into the IntPtr the
    // destination wants. Value is an instance property on a struct, so the handle has to be in a local
    // first to have an address to call it on; one such local is created per method and reused.
    private static void EmitTypeHandleValue(MethodDefinition method, CilInstructionCollection instructions, IResolutionScope corLibScope)
    {
        var handleType = corLibScope.CreateTypeReference("System", "RuntimeTypeHandle");
        var handleSignature = handleType.ToTypeSignature(true);

        instructions.Add(CilOpCodes.Callvirt, corLibScope
            .CreateTypeReference("System", "Type")
            .CreateMemberReference("get_TypeHandle", MethodSignature.CreateInstance(handleSignature)));

        var handleLocal = method.CilMethodBody!.LocalVariables
            .FirstOrDefault(l => l.VariableType is TypeDefOrRefSignature { FullName: "System.RuntimeTypeHandle" });

        if (handleLocal == null)
        {
            handleLocal = new CilLocalVariable(handleSignature);
            method.CilMethodBody.LocalVariables.Add(handleLocal);
        }

        instructions.Add(CilOpCodes.Stloc, handleLocal);
        instructions.Add(CilOpCodes.Ldloca, handleLocal);
        instructions.Add(CilOpCodes.Call, handleType.CreateMemberReference("get_Value",
            MethodSignature.CreateInstance(method.DeclaringModule!.CorLibTypeFactory.IntPtr)));
    }
    private static int ConstructorReceiverIndex(Instruction constructorCall) => constructorCall.OpCode == OpCode.CallVoid ? 1 : 2;

    // `: base(...)` and `: this(...)` are the only constructor calls that genuinely are calls on an object
    // that already exists - the one being constructed - so they must stay calls. They appear only inside a
    // constructor, and only on its own `this`, which is exactly what this checks: anything else named
    // .ctor is constructing a new object.
    private static bool IsChainedConstructorCall(MethodAnalysisContext context, Instruction call)
    {
        if (context.Name != ".ctor")
            return false;

        var receiver = ConstructorReceiverIndex(call);

        return call.Operands.Count > receiver && call.Operands[receiver] is LocalVariable { IsThis: true };
    }

    // Try find the follow up CallVoid for a constructor, after a Newobj.
    private static Instruction? FindConstructorCall(MethodAnalysisContext context, Instruction newobj)
    {
        var newObject = newobj.Operands[0];

        // The allocation and the constructor call routinely end up in different blocks
        var instructions = context.ControlFlowGraph!.Instructions;
        var index = instructions.IndexOf(newobj);

        if (index < 0)
            return null;

        for (var i = index + 1; i < instructions.Count; i++)
        {
            var candidate = instructions[i];

            if (candidate is not { OpCode: OpCode.Call or OpCode.CallVoid, Operands: [MethodAnalysisContext { Name: ".ctor" }, ..] })
                continue;

            var receiver = ConstructorReceiverIndex(candidate);

            if (candidate.Operands.Count > receiver && ReferenceEquals(candidate.Operands[receiver], newObject))
                return candidate;
        }

        return null;
    }

    private static CilOpCode? FloatArithmeticConversion(Instruction instruction)
    {
        if (instruction.OpCode is not (OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Modulo))
            return null;

        return (instruction.Operands[0] as LocalVariable)?.Type?.FullName switch
        {
            "System.Single" => CilOpCodes.Conv_R4,
            "System.Double" => CilOpCodes.Conv_R8,
            _ => null,
        };
    }


    // The stand-in pushed wherever a value could not be recovered. It used to be `ldc.i4.0; conv.i`
    // unconditionally, which is a native int, and the decompiler prints that as `(IntPtr)0`. Where the
    // surrounding code is ordinary integer maths the result does not compile - `num = (int)((IntPtr)0 & 16)`
    // - and that is the CS0019 `IntPtr & int` family, 694 methods, plus the `IntPtr >= IntPtr`
    // comparisons, 556 more. A zero is a zero in any width, so give it the one the use site expects and the
    // arithmetic around it stays writable.
    private static void PushPlaceholderZero(TypeAnalysisContext? expectedType, MethodDefinition method, CilInstructionCollection instructions)
    {
        // A struct is not a number, and a native zero standing in for one is invalid IL, not merely ugly
        // C#: the JIT refuses the whole method. That is what makes `FPMatrix2x2.get_Zero` and a large share
        // of the Quantum maths unrunnable - 120 of 157 fuzzable methods there throw InvalidProgramException.
        // A zeroed local of the right type is the stand-in that actually has that type.
        if (expectedType is { IsValueType: true } && expectedType.ToTypeSignature() is not CorLibTypeSignature
            && expectedType is not (RuntimeClassTypeAnalysisContext or RgctxTableTypeAnalysisContext
                or MethodRgctxTableTypeAnalysisContext or StaticFieldStorageTypeAnalysisContext
                or RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext or ByRefTypeAnalysisContext)
            && expectedType.GetExtraData<TypeDefinition>("AsmResolverType") != null)
        {
            var zeroed = ScratchLocal(method, expectedType.ToTypeSignature());

            instructions.Add(CilOpCodes.Ldloca, zeroed);
            instructions.Add(CilOpCodes.Initobj, expectedType.ToTypeSignature().ToTypeDefOrRef());
            instructions.Add(CilOpCodes.Ldloc, zeroed);
            return;
        }

        // A reference is compared and assigned as a reference, so its stand-in has to be null; a native zero
        // there is the same CS0019 in a different disguise. The synthetic il2cpp types are excluded: they
        // are not value types either, but each one IS a pointer and is emitted as one.
        if (PlaceholderNull && expectedType is { IsValueType: false } and not (RuntimeClassTypeAnalysisContext
            or RgctxTableTypeAnalysisContext or MethodRgctxTableTypeAnalysisContext
            or StaticFieldStorageTypeAnalysisContext or RuntimeMethodInfoAnalysisContext
            or RuntimeFieldInfoAnalysisContext or ByRefTypeAnalysisContext))
        {
            instructions.Add(CilOpCodes.Ldnull);
            return;
        }

        instructions.Add(CilOpCodes.Ldc_I4_0);

        switch (expectedType?.FullName)
        {
            // Already an int32 on the stack, and widening it would break the comparison it feeds.
            case "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16"
                or "System.Int32" or "System.UInt32" or "System.Char" or "System.Boolean":
                return;
            case "System.Int64" or "System.UInt64":
                instructions.Add(CilOpCodes.Conv_I8);
                return;
            case "System.Single":
                instructions.Add(CilOpCodes.Conv_R4);
                return;
            case "System.Double":
                instructions.Add(CilOpCodes.Conv_R8);
                return;
            default:
                // A pointer, or nothing known - keep the native int this has always been.
                instructions.Add(CilOpCodes.Conv_I);
                return;
        }
    }

    /// <summary>
    /// The IL type an ISIL local is actually stored in, whether it became a parameter or a local slot.
    /// </summary>
    private static TypeSignature? IlTypeOf(LocalVariable local, MethodDefinition method, Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        if (local.IsThis)
            return null;

        if (method.Parameters.FirstOrDefault(p => p.Name == local.Name) is { } parameter)
            return parameter.ParameterType;

        return locals.TryGetValue(local, out var ilLocal) ? ilLocal.VariableType : null;
    }

    /// <summary>
    /// Pushes the address of a fresh slot of <paramref name="referent"/>. The zeroing is not decoration:
    /// `ref x` demands a definitely-assigned variable where `out x` does not, and nothing here knows which
    /// of the two the callee declared, so the slot is given a value before its address is taken.
    /// </summary>
    private static void EmitAddressOfScratch(TypeAnalysisContext referent, MethodDefinition method, CilInstructionCollection instructions)
    {
        var signature = referent.ToTypeSignature();
        var scratch = new CilLocalVariable(signature);
        method.CilMethodBody!.LocalVariables.Add(scratch);

        instructions.Add(CilOpCodes.Ldloca, scratch);
        instructions.Add(CilOpCodes.Initobj, signature.ToTypeDefOrRef());
        instructions.Add(CilOpCodes.Ldloca, scratch);
    }

    /// <summary>
    /// Loads an argument for a by-ref parameter as the managed pointer the signature demands.
    ///
    /// il2cpp keeps that pointer in a register, and a register becomes an ordinary local here, so the
    /// argument arrives as a value: emitted as one it decompiles to `f(x)` against `f(out T)`, which is
    /// CS1620 - 622 methods across the two argument positions, and the single largest shape once the
    /// `_Injected` accessors il2cpp inlined are counted. C# spells by-ref at the call site rather than on
    /// the value, so that is where it goes back.
    ///
    /// Where the local is the slot itself, its address is exactly what the native code computed and the
    /// data flow survives - a write by the callee is visible to every later read, which is what makes this
    /// a recovery and not a silencing. Where it is not, nothing at this site can be addressed without
    /// claiming something false about it, so a slot is materialised: the `ref` is then right and only the
    /// identity of the variable is lost, which was already lost when the lea did not survive lifting.
    /// </summary>
    private static bool TryLoadByReference(IOperand operand, TypeAnalysisContext referent, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        // Already an address: the ldloca/ldelema in the switch produce the pointer directly.
        if (operand is AddressOf)
            return false;

        // Nothing can be a slot of these, so leave the operand to be emitted the way it always was.
        if (referent is ByRefTypeAnalysisContext || referent.FullName is "System.Void")
            return false;

        var instructions = method.CilMethodBody!.Instructions;

        if (operand is LocalVariable local && IlTypeOf(local, method, locals) is { } ilType)
        {
            // The local already carries a managed pointer - it is one of this method's own ref parameters,
            // or a copy of one - so its value is the argument.
            if (ilType is ByReferenceTypeSignature)
            {
                LoadLocal(local, method, locals);
                return true;
            }

            // Only when the slot's type is the referent exactly: ldloca on anything else yields the wrong
            // managed pointer type, trading CS1620 for a conversion error rather than fixing anything.
            if (ilType.FullName == referent.ToTypeSignature().FullName)
            {
                if (method.Parameters.FirstOrDefault(p => p.Name == local.Name) is { } parameter)
                    instructions.Add(CilOpCodes.Ldarga, parameter);
                else
                    instructions.Add(CilOpCodes.Ldloca, locals[local]);

                return true;
            }
        }

        EmitAddressOfScratch(referent, method, instructions);
        return true;
    }

    private static void LoadOperand(IOperand operand, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, IMethodDescriptor writeLine,
        TypeAnalysisContext? expectedType = null)
    {
        var instructions = method.CilMethodBody!.Instructions;

        var module = method.DeclaringModule!;

        // A by-ref parameter has to be handed the address of a variable, and that has to be decided here,
        // before anything below turns the operand into a value. Deliberately ahead of the null rewrite too:
        // a byref is not a value type, so a literal 0 argument would otherwise become `f(null)`.
        if (ByRefAtCallSite && expectedType is ByRefTypeAnalysisContext { ElementType: { } byRefReferent }
            && TryLoadByReference(operand, byRefReferent, method, locals))
            return;

        // The mirror: il2cpp passes a value type wider than a register by address even where the parameter
        // is declared by value, so the address-of the native code did is not the language's `ref` - the
        // callee wants the value in that slot. Reading it here is what the original source wrote, and it is
        // also what lets an untyped slot pick up the cast below. Emitted as an address it decompiles to
        // `f(ref x)` against a by-value parameter - CS1615, 535 methods, and CS8373 where the callee is a
        // property setter and the decompiler prints the call as an assignment.
        // IntPtr is excluded: there the pointer itself is what the parameter is declared to hold, so the
        // address really is the argument. And only where the slot is the parameter's own type, or has no
        // type at all and so picks up the cast below - reading a slot typed as something else would swap
        // CS1615 for a conversion error, which is movement rather than progress.
        if (ByRefAtCallSite && operand is AddressOf { Target: LocalVariable passedIndirectly }
            && expectedType is { IsValueType: true } && expectedType.FullName is not ("System.IntPtr" or "System.UIntPtr")
            && (passedIndirectly.Type == null || passedIndirectly.Type.FullName == expectedType.FullName))
            operand = passedIndirectly;

        // A null reference reaches us as an integer zero, which would otherwise be emitted as a literal 0
        // and read back as a cast from a number.
        if (expectedType is { IsValueType: false } && IsZeroConstant(operand))
        {
            instructions.Add(CilOpCodes.Ldnull);
            return;
        }

        // Ahead of the switch because it replaces the load rather than adding to it: what the callee asked
        // for is the object's class, and the switch would load the object.
        if (TryLoadClassOfObject(operand, expectedType, method, locals))
            return;

        switch (operand)
        {
            case Immediate { Value: >= int.MinValue and <= int.MaxValue } immediate:
                instructions.Add(CilOpCodes.Ldc_I4, (int)immediate.Value);
                break;
            case Immediate immediate:
                instructions.Add(CilOpCodes.Ldc_I8, immediate.Value);
                break;
            case FloatLiteral f:
                instructions.Add(CilOpCodes.Ldc_R4, f.Value);
                break;
            case DoubleLiteral d:
                instructions.Add(CilOpCodes.Ldc_R8, d.Value);
                break;
            case StringLiteral s:
                instructions.Add(CilOpCodes.Ldstr, s.Value);
                break;
            case LocalVariable local:
                LoadLocal(local, method, locals);
                CastUntypedLocal(local, expectedType, instructions);
                break;
            case ArrayLength arrayLength:
                LoadLocal(arrayLength.Array, method, locals);
                if (arrayLength.Array.Type?.FullName == "System.Array")
                {
                    // ldlen requires a zero-based vector on the stack; the abstract System.Array base
                    // (what a length read lands on when the element type is unknown) is not one, so use
                    // its Length property instead, which is valid on any array.
                    var arrayGetLength = module.CorLibTypeFactory.CorLibScope
                        .CreateTypeReference("System", "Array")
                        .CreateMemberReference("get_Length", MethodSignature.CreateInstance(module.CorLibTypeFactory.Int32));
                    instructions.Add(CilOpCodes.Callvirt, arrayGetLength);
                }
                else
                {
                    instructions.Add(CilOpCodes.Ldlen);
                    instructions.Add(CilOpCodes.Conv_I4);
                }
                break;
            case AddressOf { Target: LocalVariable addressed }:
                instructions.Add(CilOpCodes.Ldloca, locals[addressed]);
                break;
            case AddressOf { Target: ArrayAccess elementAddress }:
                LoadLocal(elementAddress.Array, method, locals);
                LoadOperand(elementAddress.Index, method, locals, writeLine);
                instructions.Add(CilOpCodes.Ldelema,
                    ((SzArrayTypeAnalysisContext)elementAddress.Array.Type!).ElementType.ToTypeSignature().ToTypeDefOrRef());
                break;
            case ArrayAccess arrayAccess:
                LoadLocal(arrayAccess.Array, method, locals);
                LoadOperand(arrayAccess.Index, method, locals, writeLine);
                instructions.Add(CilOpCodes.Ldelem,
                    ((SzArrayTypeAnalysisContext)arrayAccess.Array.Type!).ElementType.ToTypeSignature().ToTypeDefOrRef());
                break;
            case FieldReference field:
                if (field.Field.IsStatic)
                {
                    instructions.Add(CilOpCodes.Ldsfld, field.Field.ToFieldDescriptor());
                    break;
                }

                LoadLocal(field.Local, method, locals);

                // Reading System.Int32::m_value off an int is how il2cpp stores the value, but in C# the
                // local already IS the value, and the field is not accessible - the decompiled source gets
                // CS1061. The load is a no-op, so emit nothing for it.
                if (!IsPrimitiveBackingField(field.Field)
                    && !TryEmitBackingFieldRead(field.Field, method, instructions)
                    && !TryEmitInternalFieldRead(field.Field, method, instructions))
                    instructions.Add(CilOpCodes.Ldfld, field.Field.ToFieldDescriptor());

                // a.b.c: keep reading into the value-type field that was loaded
                foreach (var nested in field.InnerPath)
                    instructions.Add(CilOpCodes.Ldfld, nested.ToFieldDescriptor());

                break;
            case MemoryOperand memory:
                if (memory.Index == null && memory.Addend == 0 && memory.Scale == 0
                    && memory.Base is LocalVariable local2)
                {
                    LoadLocal(local2, method, locals);

                    // A load through a managed pointer (byref) dereferences it to yield the referent.
                    if (local2.Type is ByRefTypeAnalysisContext { ElementType: { } referent })
                        instructions.Add(referent.IsValueType
                            ? new CilInstruction(CilOpCodes.Ldobj, referent.ToTypeSignature().ToTypeDefOrRef())
                            : new CilInstruction(CilOpCodes.Ldind_Ref));
                    break;
                }
                // Reads out of il2cpp's own runtime structures (class and method info, rgctx tables,
                // static field storage) have no C# counterpart. Later passes consume the resolved
                // *type* of the operand rather than the pointer that is loaded here, so nothing is
                // actually lost and there is no issue to report.
                //
                // The stand-in used to be a native int unconditionally, and that is a claim the metadata
                // layout contradicts: what these reads fetch is `flags` bytes and `type` enums - see the
                // byte-wide flags1/flags2 in Il2CppClassUsefulOffsets - never a pointer. The value cannot
                // be recovered either way, but its width can be right, and where the use site says int the
                // arithmetic around it stays writable: `num = (IntPtr)0 & 16` is CS0019 where `num = 0 & 16`
                // is not, and the bit-test shapes alone are 613 of the counted offending lines.
                if (memory.Base is LocalVariable { Type: { } baseType } && IsRuntimeMetadata(baseType))
                {
                    ReportMetadataRead(baseType, memory.Addend);

                    if (MetadataZeroWidth)
                    {
                        PushPlaceholderZero(expectedType, method, instructions);
                        break;
                    }

                    instructions.Add(CilOpCodes.Ldc_I4_0);
                    instructions.Add(CilOpCodes.Conv_I);
                    break;
                }

                Analysis.MarkerDiag.Record(memory, method);
                instructions.Add(CilOpCodes.Ldstr, Diagnostic("Unmanaged memory load: " + operand));
                instructions.Add(CilOpCodes.Call, writeLine);
                PushPlaceholderZero(expectedType, method, instructions);
                break;
            case RuntimeMethodInfoAnalysisContext runtimeMethod:
                // A delegate constructor takes its target as a native pointer, which is exactly ldftn.
                if (expectedType?.FullName == "System.IntPtr")
                {
                    instructions.Add(CilOpCodes.Ldftn, runtimeMethod.RepresentedMethod.ToMethodDescriptor());
                    break;
                }

                //Not fully implemented, these basically shouldn't actually ever exist in the final IL.
                PushPlaceholderZero(expectedType, method, instructions);
                break;
            case RuntimeFieldInfoAnalysisContext runtimeField:
                // fieldof(F), e.g. the handle InitializeArray takes.
                if (expectedType?.FullName == "System.RuntimeFieldHandle")
                {
                    instructions.Add(CilOpCodes.Ldtoken, runtimeField.RepresentedField.ToFieldDescriptor());
                    break;
                }

                PushPlaceholderZero(expectedType, method, instructions);
                break;
            case RuntimeClassTypeAnalysisContext or RgctxTableTypeAnalysisContext
                or MethodRgctxTableTypeAnalysisContext or StaticFieldStorageTypeAnalysisContext:
                PushPlaceholderZero(expectedType, method, instructions);
                break;
            case TypeAnalysisContext type:
                //typeof(T)
                var corLibScope = module.CorLibTypeFactory.CorLibScope;
                var typeFromHandle = corLibScope
                    .CreateTypeReference("System", "Type")
                    .CreateMemberReference("GetTypeFromHandle", MethodSignature.CreateStatic(
                        corLibScope.CreateTypeReference("System", "Type").ToTypeSignature(false),
                        [corLibScope.CreateTypeReference("System", "RuntimeTypeHandle").ToTypeSignature(true)]));

                instructions.Add(CilOpCodes.Ldtoken, type.ToTypeSignature().ToTypeDefOrRef());
                instructions.Add(CilOpCodes.Call, typeFromHandle);

                // What il2cpp actually loads here is an Il2CppClass*, and where the destination holds one the
                // code goes on to use it as a pointer. A System.Type stored into an IntPtr is the top
                // compile error in the output (1,530 methods, CS0029), and the nearest thing C# can say is
                // the type handle's Value - also a pointer identifying the type, and a real expression
                // rather than a diagnostic.
                //
                // The destination is usually typed RuntimeClassTypeAnalysisContext rather than
                // System.IntPtr: that synthetic type IS the class pointer, and it is only lowered to IntPtr
                // when the local signature is written. Checking for System.IntPtr alone matched nothing.
                if (expectedType is RuntimeClassTypeAnalysisContext || expectedType?.FullName == "System.IntPtr")
                    EmitTypeHandleValue(method, instructions, corLibScope);

                break;
            default:
                instructions.Add(CilOpCodes.Ldstr, Diagnostic("Unknown operand: " + operand));
                instructions.Add(CilOpCodes.Call, writeLine);
                instructions.Add(CilOpCodes.Ldnull);
                break;
        }

        CastMismatchedValue(operand, expectedType, instructions);
    }
    
    private static bool TryEmitExactTypeComparison(Instruction instruction, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, IMethodDescriptor writeLine)
    {
        var left = instruction.Operands[1];
        var right = instruction.Operands[2];

        IOperand typeOperand;
        LocalVariable objLocal;

        if (left is TypeAnalysisContext && IsKlassPointerLoad(right, out var rightLocal))
            (typeOperand, objLocal) = (left, rightLocal);
        else if (right is TypeAnalysisContext && IsKlassPointerLoad(left, out var leftLocal))
            (typeOperand, objLocal) = (right, leftLocal);
        else
            return false;

        var module = method.DeclaringModule!;
        var instructions = method.CilMethodBody!.Instructions;

        var getType = module.CorLibTypeFactory.CorLibScope
            .CreateTypeReference("System", "Object")
            .CreateMemberReference("GetType", MethodSignature.CreateInstance(
                module.CorLibTypeFactory.CorLibScope.CreateTypeReference("System", "Type").ToTypeSignature(false)));

        LoadLocal(objLocal, method, locals);
        instructions.Add(CilOpCodes.Callvirt, getType);
        LoadOperand(typeOperand, method, locals, writeLine); // emits typeof(T)
        instructions.Add(CilOpCodes.Ceq);

        if (instruction.OpCode == OpCode.CheckNotEqual)
        {
            instructions.Add(CilOpCodes.Ldc_I4_0);
            instructions.Add(CilOpCodes.Ceq);
        }

        StoreToOperand(instruction.Operands[0], method, locals, writeLine);
        return true;
    }
    
    private static bool IsKlassPointerLoad(IOperand operand, out LocalVariable local)
    {
        if (operand is MemoryOperand { Index: null, Addend: 0, Scale: 0, Base: LocalVariable { Type.IsValueType: false } baseLocal })
        {
            local = baseLocal;
            return true;
        }

        local = null!;
        return false;
    }

    private static void PushDefaultOf(TypeAnalysisContext type, MethodDefinition method, CilInstructionCollection instructions)
    {
        //TODO Remove this, we should be handling arguments correctly in ISIL resolution, this is a hack to emit balanced stacks.
        //TODO At the *very* least we should emit a console.writeline saying that we did this.

        // A byref is not a value type but null is not a stand-in for one either: the callee is handed the
        // address of a variable, so the placeholder has to be a variable, not the absence of one.
        if (ByRefAtCallSite && type is ByRefTypeAnalysisContext { ElementType: { } referent }
            && referent is not ByRefTypeAnalysisContext && referent.FullName is not "System.Void")
        {
            EmitAddressOfScratch(referent, method, instructions);
            return;
        }

        if (!type.IsValueType)
        {
            instructions.Add(CilOpCodes.Ldnull);
            return;
        }

        switch (type.FullName)
        {
            case "System.Single": instructions.Add(CilOpCodes.Ldc_R4, 0f); break;
            case "System.Double": instructions.Add(CilOpCodes.Ldc_R8, 0d); break;
            case "System.Int64" or "System.UInt64": instructions.Add(CilOpCodes.Ldc_I8, 0L); break;
            default: instructions.Add(CilOpCodes.Ldc_I4_0); break;
        }
    }

    private static bool IsBoolean(IOperand operand, MethodAnalysisContext context) =>
        operand is LocalVariable { Type: { } type } && type == context.AppContext.SystemTypes.SystemBooleanType;

    private static bool IsZeroConstant(IOperand operand) => operand is Immediate { Value: 0 };
    
    private static TypeAnalysisContext? DestinationType(IOperand destination) =>
        destination switch
        {
            LocalVariable local => local.Type,
            FieldReference field => field.Field.FieldType,
            ArrayAccess { Array.Type: SzArrayTypeAnalysisContext array } => array.ElementType,
            _ => null
        };

    private static void LoadLocal(LocalVariable local, MethodDefinition method, Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        var instructions = method.CilMethodBody!.Instructions;

        if (local.IsThis)
        {
            instructions.Add(CilOpCodes.Ldarg_0);
            return;
        }

        var parameter = method.Parameters.FirstOrDefault(p => p.Name == local.Name);

        if (parameter != null)
            instructions.Add(CilOpCodes.Ldarg, parameter);
        else
            instructions.Add(CilOpCodes.Ldloc, locals[local]);
    }

    private static void StoreToOperand(IOperand operand, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, IMethodDescriptor writeLine)
    {
        var instructions = method.CilMethodBody!.Instructions;

        switch (operand)
        {
            case LocalVariable local:
                instructions.Add(CilOpCodes.Stloc, locals[local]);
                break;

            case FieldReference field:
                // for a.b.c the store lands on the innermost field; the ones before it only get us there
                var storedField = field.InnerPath.Length > 0 ? field.InnerPath[^1] : field.Field;
                var fieldDescriptor = storedField.ToFieldDescriptor();

                if (storedField.IsStatic)
                {
                    instructions.Add(CilOpCodes.Stsfld, fieldDescriptor);
                    break;
                }

                // stfld wants the object underneath the value, but the value is already on the stack, so
                // park it in a temporary while we load the object.
                var scratch = new CilLocalVariable(fieldDescriptor.Signature!.FieldType);
                method.CilMethodBody!.LocalVariables.Add(scratch);

                instructions.Add(CilOpCodes.Stloc, scratch);
                LoadLocal(field.Local, method, locals);

                if (field.InnerPath.Length > 0)
                {
                    // ldflda, not ldfld: writing through a copy of the struct would be discarded
                    instructions.Add(CilOpCodes.Ldflda, field.Field.ToFieldDescriptor());

                    for (var nested = 0; nested < field.InnerPath.Length - 1; nested++)
                        instructions.Add(CilOpCodes.Ldflda, field.InnerPath[nested].ToFieldDescriptor());
                }

                instructions.Add(CilOpCodes.Ldloc, scratch);
                instructions.Add(CilOpCodes.Stfld, fieldDescriptor);
                break;

            case ArrayAccess arrayAccess:
                // stelem needs array and index before the value, so the same trick as stfld
                var elementType = ((SzArrayTypeAnalysisContext)arrayAccess.Array.Type!).ElementType;
                var elementScratch = new CilLocalVariable(elementType.ToTypeSignature());
                method.CilMethodBody!.LocalVariables.Add(elementScratch);

                instructions.Add(CilOpCodes.Stloc, elementScratch);
                LoadLocal(arrayAccess.Array, method, locals);
                LoadOperand(arrayAccess.Index, method, locals, writeLine);
                instructions.Add(CilOpCodes.Ldloc, elementScratch);
                instructions.Add(CilOpCodes.Stelem, elementType.ToTypeSignature().ToTypeDefOrRef());
                break;

            case MemoryOperand memory:
                if (memory.Index == null && memory.Addend == 0 && memory.Scale == 0
                    && memory.Base is LocalVariable local2)
                {
                    // Can pointer assignments just be ignored because it's C#? (Move [local], 123)
                    instructions.Add(CilOpCodes.Stloc, locals[local2]);
                    break;
                }
                instructions.Add(CilOpCodes.Pop);
                break;

            default:
                instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Store into unknown operand: {operand}"));
                instructions.Add(CilOpCodes.Call, writeLine);
                instructions.Add(CilOpCodes.Pop);
                break;
        }
    }

    /// <summary>
    /// True for the il2cpp runtime structures that carry no value expressible in C#: class and
    /// method info, rgctx tables, and static field storage. Reads out of these exist only so the
    /// native code can find metadata, and the information they carry is already on the operand type.
    /// </summary>
    private static bool IsRuntimeMetadata(TypeAnalysisContext type) => type is
        RgctxTableTypeAnalysisContext
        or MethodRgctxTableTypeAnalysisContext
        or RuntimeClassTypeAnalysisContext
        or RuntimeMethodInfoAnalysisContext
        or RuntimeFieldInfoAnalysisContext
        or StaticFieldStorageTypeAnalysisContext;

}
