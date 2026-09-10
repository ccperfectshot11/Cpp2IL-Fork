using System;
using System.Collections.Concurrent;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Deletes the reads that load an il2cpp runtime handle out of memory, once the analysis that needed
/// them has run.
///
/// The value such a read produces - an <c>Il2CppClass*</c>, an rgctx table, a method/field handle, the
/// static-field block - has no managed counterpart, so the local holding it is one of the synthetic
/// contexts, and those lower to <see cref="System.IntPtr"/> when the local signature is written. The
/// read itself is a plain <c>[base + offset]</c>, and the most common one by far is <c>[obj + 0]</c>:
/// the class pointer sits in the object header, so il2cpp fetches it from the object it already has.
/// The generator lowers <c>[local + 0]</c> to the local itself, and the result is
/// <c>ldloc (string[]); stloc (IntPtr)</c> - CS0029 on every one of them. Measured on the two
/// Assembly-CSharp DLLs: 2,204 stores of a reference into a native-int slot, led by String[] (648),
/// Delegate (254) and Object[] (151), all this shape; the 499 left over are a different bug.
///
/// Nothing downstream wants the loaded value. Every consumer of a runtime handle in the generator
/// matches on the *type* of the operand and emits a placeholder for the pointer - see the
/// "Reads out of il2cpp's own runtime structures ... have no C# counterpart" case in
/// <c>IlGenerator.LoadOperand</c> - so removing the definition changes nothing but the bad store, and
/// the local keeps the zero its initobj already gave it.
///
/// Only a memory source is dropped. A handle that comes from a metadata operand
/// (<c>Move local, methodof(M)</c>, <c>Move local, typeof(T)</c>) is a value the generator really does
/// emit, as ldftn or as a type handle, and deleting those would lose the delegate targets.
///
/// Runs last, after both rounds of copy propagation, and that placement is the whole difference
/// between a gain and a loss. The read is also what a use site able to lower it consumes: an
/// <c>[obj + 0]</c> forwarded into a System.Type argument becomes <c>obj.GetType()</c>, which is how
/// the inlined cast checks get their second operand. Dropping the definition before
/// <see cref="Simplifier"/> can forward it leaves those calls holding the handle local instead -
/// measured as 254 methods trading CS0029 for CS1503, and 5 strict methods lost. Moved after it, the
/// same removal is worth +4 strict.
///
/// The strict gain is small; the reason it is on by default is the other half of the same removal -
/// 953 fewer "Unmanaged memory load" markers. A read whose result is a runtime handle is not a lift
/// that failed, it is a read with nothing to lift, and it should not have been reported as one.
/// </summary>
public static class RuntimeHandleReadRemover
{
    // On by default; CPP2IL_DROP_HANDLE_READS=0 disables.
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("CPP2IL_DROP_HANDLE_READS") != "0";

    // CPP2IL_SLOTDIAG=1 tallies what was dropped, by the type of the base being read through, so a
    // regression can be attributed to a shape rather than guessed at. Zero cost when off.
    private static readonly bool Diag = Environment.GetEnvironmentVariable("CPP2IL_SLOTDIAG") == "1";
    private static readonly ConcurrentDictionary<string, long> Dropped = new();
    private static int _hooked;

    public static void Run(MethodAnalysisContext method)
    {
        if (!Enabled)
            return;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count < 2)
                continue;

            if (instruction.Operands[0] is not LocalVariable { Type: { } destinationType } || !IsRuntimeHandle(destinationType))
                continue;

            if (instruction.Operands[1] is not MemoryOperand source)
                continue;

            if (Diag)
                Record(source);

            instruction.OpCode = OpCode.Nop;
            instruction.SetOperands();
        }
    }

    // The synthetic contexts, and only those: each stands for a pointer into il2cpp's own metadata and
    // has no AsmResolver type behind it. System.IntPtr itself is deliberately not here - a local really
    // declared native int can be holding a value the original code computed and uses.
    private static bool IsRuntimeHandle(TypeAnalysisContext type) =>
        type is RuntimeClassTypeAnalysisContext or RgctxTableTypeAnalysisContext
            or MethodRgctxTableTypeAnalysisContext or StaticFieldStorageTypeAnalysisContext
            or RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext;

    private static void Record(MemoryOperand source)
    {
        if (System.Threading.Interlocked.Exchange(ref _hooked, 1) == 0)
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                Console.WriteLine("==== CITIRI DE HANDLE RUNTIME STERSE, dupa baza citita ====");
                foreach (var kv in Dropped.OrderByDescending(k => k.Value).Take(20))
                    Console.WriteLine($"   {kv.Value,9}  {kv.Key}");
            };

        var baseType = source.Base is LocalVariable { Type: { } type } ? type.FullName : "(fara baza tipizata)";
        var shape = source.Addend == 0 && source.Index == null ? "[baza + 0]" : "[baza + N]";

        Dropped.AddOrUpdate($"{shape} <- {baseType}", 1, (_, v) => v + 1);
    }
}
