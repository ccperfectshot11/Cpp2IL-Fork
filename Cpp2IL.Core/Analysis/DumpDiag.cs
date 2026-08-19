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
/// "Unmanaged memory load", "Method not found", "Indirect call") and, for the first
/// CPP2IL_DUMP_MAX methods (default 8) whose recovered body contains that marker, it prints the
/// COMPLETE picture: the matching marker text, the full ISIL (every block, every instruction, every
/// operand with its resolved type), the local variables with their types, and the final generated IL.
/// Everything you need to see exactly what is wrong and craft the correct fix, gated so normal runs
/// pay nothing.
/// </summary>
internal static class DumpDiag
{
    private static readonly string? Target = Environment.GetEnvironmentVariable("CPP2IL_DUMP");
    private static readonly int Max = int.TryParse(Environment.GetEnvironmentVariable("CPP2IL_DUMP_MAX"), out var m) ? m : 8;
    private static int _dumped;
    private static readonly object Gate = new();

    public static void MaybeDump(MethodAnalysisContext context, MethodDefinition definition)
    {
        if (string.IsNullOrEmpty(Target) || _dumped >= Max)
            return;

        var body = definition.CilMethodBody;
        if (body == null)
            return;

        // Find the marker(s) this method's recovered body carries that match the requested category.
        var markers = body.Instructions
            .Where(i => i.OpCode.Code == CilCode.Ldstr && i.Operand is string s && s.Contains(Target!))
            .Select(i => (string)i.Operand!)
            .Distinct()
            .ToList();

        if (markers.Count == 0)
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

            sb.AppendLine($"[DUMP] --- MARKER(S) matching \"{Target}\" ---");
            foreach (var mk in markers)
                sb.AppendLine($"[DUMP]     {mk}");

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
