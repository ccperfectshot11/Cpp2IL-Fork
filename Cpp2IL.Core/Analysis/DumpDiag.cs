using System;
using System.Linq;
using System.Text;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// The biggest-dump-ever diagnostic. Set env CPP2IL_DUMP to a marker substring (e.g.
/// "Unmanaged memory load", "Method not found", "Indirect call"), or CPP2IL_DUMP_METHOD to a
/// "Type::Method" substring, and for the first CPP2IL_DUMP_MAX methods (default 8) that match it
/// prints the COMPLETE picture: the matching marker text, the parameter operands and the locals they
/// bound to, the machine code the lift started from, the full ISIL (every block, every instruction,
/// every operand with its resolved type), the locals with their types, and the final generated IL.
/// Everything you need to see exactly what is wrong and craft the correct fix, gated so normal runs
/// pay nothing.
/// </summary>
internal static class DumpDiag
{
    private static readonly string? Target = Environment.GetEnvironmentVariable("CPP2IL_DUMP");

    // Same dump, selected by "Type::Method" instead of by marker: the bodies worth reading are not always
    // the ones that failed loudly. A method that silently ignores its arguments carries no marker at all.
    private static readonly string? TargetMethod = Environment.GetEnvironmentVariable("CPP2IL_DUMP_METHOD");
    private static readonly int Max = int.TryParse(Environment.GetEnvironmentVariable("CPP2IL_DUMP_MAX"), out var m) ? m : 8;
    private static int _dumped;
    private static readonly object Gate = new();

    public static void MaybeDump(MethodAnalysisContext context, MethodDefinition definition)
    {
        if (string.IsNullOrEmpty(Target) && string.IsNullOrEmpty(TargetMethod))
            return;

        if (_dumped >= Max)
            return;

        var body = definition.CilMethodBody;
        if (body == null)
            return;

        if (!string.IsNullOrEmpty(TargetMethod)
            && !$"{context.DeclaringType?.FullName}::{context.Name}".Contains(TargetMethod!))
            return;

        // Find the marker(s) this method's recovered body carries that match the requested category.
        var markers = string.IsNullOrEmpty(Target)
            ? []
            : body.Instructions
                .Where(i => i.OpCode.Code == CilCode.Ldstr && i.Operand is string s && s.Contains(Target!))
                .Select(i => (string)i.Operand!)
                .Distinct()
                .ToList();

        if (!string.IsNullOrEmpty(Target) && markers.Count == 0)
            return;

        lock (Gate)
        {
            if (_dumped >= Max)
                return;
            _dumped++;

            var sb = new StringBuilder();
            sb.AppendLine($"\n[DUMP] ======================================================================");
            sb.AppendLine($"[DUMP] METHOD  {context.DeclaringType?.FullName}::{context.Name}");
            sb.AppendLine($"[DUMP] return={context.ReturnType?.FullName}  static={context.IsStatic}  params=[{string.Join(", ", context.Parameters.Select(p => $"{p.ParameterName}:{p.ParameterType?.FullName}"))}]");

            if (markers.Count > 0)
            {
                sb.AppendLine($"[DUMP] --- MARKER(S) matching \"{Target}\" ---");
                foreach (var mk in markers)
                    sb.AppendLine($"[DUMP]     {mk}");
            }

            sb.AppendLine($"[DUMP] --- PARAMETER OPERANDS ---");
            sb.AppendLine($"[DUMP]     operands: {string.Join(", ", context.ParameterOperands)}");
            sb.AppendLine($"[DUMP]     locals:   {string.Join(", ", context.ParameterLocals.Select(l => $"{l.Name}@{l.Register}"))}");

            // The machine code the lift started from. Reading the ISIL without it only ever tells you
            // what the lifter believed, never whether it was right.
            if (context.AppContext.InstructionSet is InstructionSets.X86InstructionSet)
            {
                sb.AppendLine($"[DUMP] --- NATIVE ---");
                try
                {
                    context.EnsureRawBytes();
                    foreach (var native in Utils.X86Utils.Disassemble(context.RawBytes.AsSpan(), context.UnderlyingPointer, context.AppContext.Binary.is32Bit))
                        sb.AppendLine($"[DUMP]     {native.IP:X}  {native}");
                }
                catch (Exception e)
                {
                    sb.AppendLine($"[DUMP]     <unavailable: {e.Message}>");
                }
            }

            sb.AppendLine($"[DUMP] --- LOCALS ({context.Locals.Count}) ---");
            foreach (var local in context.Locals)
                sb.AppendLine($"[DUMP]     {local.Name} @ {local.Register}  :  {local.Type?.FullName ?? "<untyped>"}");

            sb.AppendLine($"[DUMP] --- ISIL (control-flow graph) ---");
            var cfg = context.ControlFlowGraph;
            if (cfg != null)
            {
                var blocks = cfg.Blocks.ToList();
                for (var bi = 0; bi < blocks.Count; bi++)
                {
                    var block = blocks[bi];
                    sb.AppendLine($"[DUMP]   block #{bi} [{block.BlockType}] succ=[{string.Join(",", block.Successors.Select(s => blocks.IndexOf(s)))}]");
                    foreach (var ins in block.Instructions)
                        sb.AppendLine($"[DUMP]     {ins.OpCode,-16} {FormatOperands(ins)}");
                }
            }

            sb.AppendLine($"[DUMP] --- GENERATED IL ({body.Instructions.Count}) ---");
            foreach (var il in body.Instructions)
                sb.AppendLine($"[DUMP]     IL_{il.Offset:X4}: {il.OpCode.Mnemonic,-12} {FormatIlOperand(il.Operand)}");

            sb.AppendLine($"[DUMP] ======================================================================");
            Console.WriteLine(sb.ToString());
        }
    }

    // Each ISIL operand with its concrete kind and, for a local, its resolved type — the data the
    // resolution passes act on.
    private static string FormatOperands(Instruction ins) =>
        string.Join("  |  ", ins.Operands.Select(FormatOperand));

    private static string FormatOperand(IOperand? op) => op switch
    {
        null => "<null>",
        LocalVariable lv => $"{lv.Name}({lv.Type?.FullName ?? "?"})",
        MemoryOperand mem => $"MEM{mem}" + (mem.Base is LocalVariable b ? $"{{base:{b.Type?.FullName ?? "?"}}}" : ""),
        _ => $"{op.GetType().Name}:{op}",
    };

    private static string FormatIlOperand(object? op) => op switch
    {
        null => "",
        string s => "\"" + (s.Length > 60 ? s[..60] + "…" : s) + "\"",
        _ => op.ToString() ?? "",
    };
}
