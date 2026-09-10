using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using NetSpyAdapter;

// Level-3 measurement (web-Claude), combined + parallelized. For every Cpp2IL DLL we decompile each
// type through NetSpy (in memory), tally the leftover artifacts, then compile the C# with Roslyn
// against the other v20 DLLs to see how much of it actually RECOMPILES. The headline number is: of
// the MARKER-FREE methods (no NoteDecompilerIssue call), how many produce C# with no compile error.
//
//   Cpp2IL.CompileCheck <dllDir> [dllNameSubstring]
//   env CPP2IL_PAR = worker thread count (default 6). Decompiler recurses deeply, so each worker
//   runs on a 512 MB stack; the module is disposed per assembly so memory stays bounded.
internal static class Program
{
    private static string[] _allDlls;
    private static readonly Dictionary<string, MetadataReference> _refCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly bool ExcludeSelf = Environment.GetEnvironmentVariable("CPP2IL_CC_EXCLUDE_SELF") == "1";

    // recompilation
    private static long methodsTotal, methodsOk, mfTotal, mfOk, classLevelErrors, asmDecompileErrors, oomBatches;
    private static long methodsOkStrict, mfOkStrict, methodsInBrokenType;   // strict = method clean AND its type compiles
    // error-cause grouping: diagnostic id -> total count / class-level count / distinct methods / sample
    private static readonly ConcurrentDictionary<string, long> ErrCodeAll = new();     // every error code -> error count
    private static readonly ConcurrentDictionary<string, long> ErrCodeClass = new();   // class-level (not-in-method) error codes
    private static readonly ConcurrentDictionary<string, long> ErrCodeMethods = new(); // error code -> DISTINCT methods it touches (prioritization metric)
    private static readonly ConcurrentDictionary<string, long> ErrShapeMethods = new(); // "CODE | normalized message" -> distinct methods (splits CS0019 IntPtr-vs-byte[])
    private static readonly ConcurrentDictionary<string, long> SoleCause = new();       // methods whose ONLY problem (body + its type) is this code -> become strict-ok if it alone is fixed
    private static readonly ConcurrentDictionary<string, string> ErrExample = new();   // error code -> one example message
    // artifacts (per type)
    private static long typesTotal, decompFail, typesNative, typesGoto, typesMarker, typesClean;
    private static int _done;

    private static readonly CSharpCompilationOptions Options =
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
            .WithNullableContextOptions(NullableContextOptions.Disable);
    private static readonly CSharpParseOptions ParseOpts = new CSharpParseOptions(LanguageVersion.Latest);

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: Cpp2IL.CompileCheck <dllDir> [dllNameSubstring]");
            return 2;
        }

        var dllDir = args[0];
        var filter = args.Length > 1 ? args[1] : null;
        _allDlls = Directory.GetFiles(dllDir, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(p => p).ToArray();

        foreach (var d in _allDlls)
            try { _refCache[d] = MetadataReference.CreateFromFile(d); } catch { }

        var targets = _allDlls
            .Where(p => filter == null || Path.GetFileName(p).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            // biggest first so the long poles start early and the small ones fill the gaps
            .OrderByDescending(p => new FileInfo(p).Length)
            .ToArray();

        // Low default so the machine survives: batched compilation keeps each worker well under ~1.5 GB,
        // 2 workers -> ~3 GB peak. Override with CPP2IL_PAR when you have RAM to spare.
        var par = int.TryParse(Environment.GetEnvironmentVariable("CPP2IL_PAR"), out var p2) && p2 > 0 ? p2 : 2;
        var queue = new ConcurrentQueue<string>(targets);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var workers = new List<Thread>();
        for (var w = 0; w < par; w++)
        {
            var t = new Thread(() =>
            {
                while (queue.TryDequeue(out var dll))
                {
                    CompileAssembly(dll);
                    var n = Interlocked.Increment(ref _done);
                    var memMb = GC.GetTotalMemory(false) / (1024 * 1024);
                    Console.Error.WriteLine($"[{n}/{targets.Length}] {Path.GetFileNameWithoutExtension(dll),-40} ({memMb} MB, {sw.Elapsed.TotalSeconds:F0}s)");
                    GC.Collect();
                }
            }, 512 * 1024 * 1024);
            workers.Add(t);
            t.Start();
        }
        foreach (var t in workers) t.Join();

        double P(long a, long b) => b == 0 ? 0 : 100.0 * a / b;
        Console.WriteLine();
        Console.WriteLine("================ LEVEL 3: DECOMPILE -> RECOMPILE (NetSpy + Roslyn) ================");
        Console.WriteLine($"DLLs                       : {targets.Length}   (workers={par})");
        Console.WriteLine($"  assemblies decompile err : {asmDecompileErrors}");
        Console.WriteLine();
        Console.WriteLine("-- Faithfulness (artifacts, per type) --");
        Console.WriteLine($"Types decompiled           : {typesTotal:N0}");
        Console.WriteLine($"  decompiler CRASHED        : {decompFail:N0}  ({P(decompFail, typesTotal):F1}%)");
        Console.WriteLine($"  has NativeMethod_0x*      : {typesNative:N0}  ({P(typesNative, typesTotal):F1}%)");
        Console.WriteLine($"  has NoteDecompilerIssue   : {typesMarker:N0}  ({P(typesMarker, typesTotal):F1}%)");
        Console.WriteLine($"  has goto                  : {typesGoto:N0}  ({P(typesGoto, typesTotal):F1}%)");
        Console.WriteLine($"  CLEAN (none of the above) : {typesClean:N0}  ({P(typesClean, typesTotal):F1}%)");
        Console.WriteLine();
        Console.WriteLine("-- Recompilation (Roslyn, per method) --");
        Console.WriteLine($"Methods (all)              : {methodsTotal:N0}");
        Console.WriteLine($"  LENIENT (no error in own span)      : {methodsOk:N0}  ({P(methodsOk, methodsTotal):F1}%)");
        Console.WriteLine($"  STRICT  (own clean AND type compiles): {methodsOkStrict:N0}  ({P(methodsOkStrict, methodsTotal):F1}%)   <== THE HONEST NUMBER");
        Console.WriteLine($"  in a structurally-broken type       : {methodsInBrokenType:N0}  ({P(methodsInBrokenType, methodsTotal):F1}%)");
        Console.WriteLine($"MARKER-FREE methods        : {mfTotal:N0}");
        Console.WriteLine($"  LENIENT : {mfOk:N0}  ({P(mfOk, mfTotal):F1}%)");
        Console.WriteLine($"  STRICT  : {mfOkStrict:N0}  ({P(mfOkStrict, mfTotal):F1}%)   <== marker-free that truly recompile");
        Console.WriteLine($"class-level errors (count) : {classLevelErrors:N0}");
        PrintSourceLines();
        if (oomBatches > 0) Console.WriteLine($"OOM batches (skipped)      : {oomBatches}");

        Console.WriteLine();
        Console.WriteLine("-- ★ RECOVERY PER FIX: methods that become STRICT-compilable if ONLY this cause is fixed ★ --");
        foreach (var kv in SoleCause.OrderByDescending(k => k.Value).Take(20))
            Console.WriteLine($"  {kv.Key,-8} +{kv.Value,7:N0} strict methods   e.g. {(ErrExample.TryGetValue(kv.Key, out var ex0) ? ex0 : "")}");
        Console.WriteLine();
        Console.WriteLine("-- top causes by METHODS AFFECTED (upper bound: how many methods each cause touches) --");
        foreach (var kv in ErrCodeMethods.OrderByDescending(k => k.Value).Take(20))
            Console.WriteLine($"  {kv.Key,-8} methods={kv.Value,8:N0}  errs={(ErrCodeAll.TryGetValue(kv.Key, out var ec) ? ec : 0),8:N0}  e.g. {(ErrExample.TryGetValue(kv.Key, out var ex) ? ex : "")}");
        Console.WriteLine();
        Console.WriteLine("-- top causes by MESSAGE SHAPE (splits one code into distinct bugs; ranked by methods) --");
        foreach (var kv in ErrShapeMethods.OrderByDescending(k => k.Value).Take(30))
            Console.WriteLine($"  methods={kv.Value,7:N0}  {kv.Key}");
        Console.WriteLine();
        Console.WriteLine("-- top CLASS-LEVEL error codes (structural, kill whole types) --");
        foreach (var kv in ErrCodeClass.OrderByDescending(k => k.Value).Take(15))
            Console.WriteLine($"  {kv.Key,-8} {kv.Value,8:N0}   e.g. {(ErrExample.TryGetValue(kv.Key, out var ex) ? ex : "")}");
        return 0;
    }

    private static void CompileAssembly(string dll)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(dll); } catch { return; }

        // Reference ALL DLLs, this one included: cross-references to types in OTHER batches resolve
        // from the own-DLL metadata, while the source types shadow their metadata twins (CS0436, a
        // warning we ignore). This lets a huge assembly compile in memory-bounded batches instead of
        // binding all its methods at once - which spiked to ~9 GB and could take the machine down.
        // CPP2IL_CC_EXCLUDE_SELF=1 drops the assembly being compiled from its own reference set, the way a
        // real project builds. It costs the batching trick above, so types from other batches of the same
        // assembly stop resolving - measure both ways rather than assuming which is more honest.
        var refs = ExcludeSelf
            ? _refCache.Where(kv => !string.Equals(kv.Key, dll, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).ToList()
            : _refCache.Values.ToList();

        // STREAM: decompile straight into a batch buffer and compile+release each batch as it fills,
        // so even mscorlib (thousands of types) never holds more than one batch of source in memory.
        var batch = new List<SyntaxTree>(BatchSize);
        void Flush()
        {
            if (batch.Count == 0) return;
            try { CompileBatch(batch, refs); }
            catch (OutOfMemoryException) { Interlocked.Increment(ref oomBatches); }
            batch.Clear();
            GC.Collect();
        }

        try
        {
            NetSpyDecompiler.DecompileAssembly(bytes, (rel, code) =>
            {
                Interlocked.Increment(ref typesTotal);
                var failed = code.Contains("// NetSpy decompile failed");
                var nativ = code.Contains("NativeMethod_0x");
                var marker = HasDecompilerMarker(code);
                var gotoo = code.Contains("goto ");
                if (failed) Interlocked.Increment(ref decompFail);
                if (nativ) Interlocked.Increment(ref typesNative);
                if (marker) Interlocked.Increment(ref typesMarker);
                if (gotoo) Interlocked.Increment(ref typesGoto);
                if (!failed && !nativ && !marker && !gotoo) Interlocked.Increment(ref typesClean);

                batch.Add(CSharpSyntaxTree.ParseText(code, ParseOpts, path: rel));
                if (batch.Count >= BatchSize) Flush();
            });
            Flush();
        }
        catch (Exception ex)
        {
            // Swallowing this hides the reason a whole assembly produced no measurement at all, which
            // looks identical to a clean run of zero methods in the summary.
            Interlocked.Increment(ref asmDecompileErrors);
            var innermost = ex;
            while (innermost is AggregateException { InnerExceptions.Count: > 0 } agg) innermost = agg.InnerExceptions[0];
            while (innermost.InnerException is { } deeper) innermost = deeper;

            Console.Error.WriteLine($"  !! {Path.GetFileNameWithoutExtension(dll)}: {innermost.GetType().Name}: {innermost.Message}");
            Console.Error.WriteLine(innermost.StackTrace);
        }
    }

    // IlGenerator reports an unlifted construct by calling Cpp2ILHelpers::NoteDecompilerIssue, but falls back
    // to Console.WriteLine when that type is not injected. Matching only the helper name then counts every
    // marker as absent and reports the output as clean - worse than a wrong number, because it reads as an
    // improvement. The message text is identical either way, so match on that too.
    private static readonly string[] MarkerMessages =
    [
        "Unmanaged memory load", "Method not found @", "Indirect call:", "Indirect jump:",
        "Invalid instruction:", "Unknown instruction:", "Not implemented instruction:",
        "Unknown call target operand:", "Store into unknown operand:", "Stack shift:",
        "Non static method called without", "Phi opcodes should not exist",
    ];

    private static bool HasDecompilerMarker(string code)
    {
        if (code.Contains("NoteDecompilerIssue"))
            return true;

        foreach (var message in MarkerMessages)
            if (code.Contains(message))
                return true;

        return false;
    }

    // Overridable so a failure that happens to land on a batch boundary can be told apart from one caused by
    // a particular type: move the boundary and see whether the failure moves with it.
    private static readonly int BatchSize =
        int.TryParse(Environment.GetEnvironmentVariable("CPP2IL_CC_BATCH"), out var b) && b > 0 ? b : 800;

    private static void CompileBatch(List<SyntaxTree> trees, List<MetadataReference> refs)
    {
        var comp = CSharpCompilation.Create("CompileCheckTarget", trees, refs, Options);

        var errorsByTree = new Dictionary<SyntaxTree, List<(TextSpan span, string id, string msg)>>();
        foreach (var d in comp.GetDiagnostics())
        {
            if (d.Severity != DiagnosticSeverity.Error) continue;
            var tree = d.Location.SourceTree;
            if (tree == null) continue;
            if (!errorsByTree.TryGetValue(tree, out var list)) errorsByTree[tree] = list = new();
            list.Add((d.Location.SourceSpan, d.Id, d.GetMessage()));
        }

        foreach (var tree in trees)
        {
            errorsByTree.TryGetValue(tree, out var errs);
            var root = tree.GetRoot();

            // one tree = one type. a class-level error (not inside any method) means the type does not
            // compile structurally, so every method in it is blocked regardless of its own body.
            var typeHasClassError = false;
            var typeClassCodes = new HashSet<string>();   // distinct class-level error codes on this type
            if (errs != null)
            {
                foreach (var (span, id, msg) in errs)
                {
                    var node = root.FindToken(Math.Min(span.Start, Math.Max(0, root.FullSpan.End - 1))).Parent;
                    var inMethod = false;
                    for (var n = node; n != null; n = n.Parent)
                        if (n is BaseMethodDeclarationSyntax || n is AccessorDeclarationSyntax) { inMethod = true; break; }
                    ErrCodeAll.AddOrUpdate(id, 1, (_, v) => v + 1);
                    if (!inMethod)
                    {
                        Interlocked.Increment(ref classLevelErrors);
                        ErrCodeClass.AddOrUpdate(id, 1, (_, v) => v + 1);
                        typeHasClassError = true;
                        typeClassCodes.Add(id);
                    }
                    ErrExample.TryAdd(id, Trim(msg));
                    RecordSourceLine(id, tree, span);
                }
            }

            foreach (var method in root.DescendantNodes().Where(IsMethodLike))
            {
                if (!HasBody(method)) continue;
                Interlocked.Increment(ref methodsTotal);
                var markerFree = !HasDecompilerMarker(method.ToString());

                var inSpan = errs?.Where(e => method.Span.Contains(e.span.Start)).ToList();
                var hasErr = inSpan != null && inSpan.Count > 0;
                if (hasErr)
                {
                    foreach (var c in inSpan.Select(e => e.id).Distinct())               // methods this CODE touches
                        ErrCodeMethods.AddOrUpdate(c, 1, (_, v) => v + 1);
                    foreach (var s in inSpan.Select(e => e.id + " | " + Normalize(e.msg)).Distinct())  // methods this SHAPE touches
                        ErrShapeMethods.AddOrUpdate(s, 1, (_, v) => v + 1);
                }

                var strictOk = !hasErr && !typeHasClassError;            // truly compilable: own body clean AND its type compiles
                if (!hasErr) Interlocked.Increment(ref methodsOk);
                if (strictOk) Interlocked.Increment(ref methodsOkStrict);
                if (typeHasClassError) Interlocked.Increment(ref methodsInBrokenType);

                // recovery-per-fix: everything wrong with this method (its own body + its type's
                // structure). If that whole set is a single code, fixing that one code alone makes the
                // method strictly compilable - the only additive way to rank fixes.
                if (!strictOk)
                {
                    var all = new HashSet<string>(typeClassCodes);
                    if (inSpan != null) foreach (var e in inSpan) all.Add(e.id);
                    if (all.Count == 1) SoleCause.AddOrUpdate(System.Linq.Enumerable.First(all), 1, (_, v) => v + 1);
                }
                if (markerFree)
                {
                    Interlocked.Increment(ref mfTotal);
                    if (!hasErr) Interlocked.Increment(ref mfOk);
                    if (strictOk) Interlocked.Increment(ref mfOkStrict);
                }
            }
        }
    }

    private static string Trim(string s) => s.Length <= 140 ? s : s.Substring(0, 140);

    // Every diagnosis in this project that started from the `e.g.` message alone has been wrong, because
    // one error code covers several distinct bugs and the message says nothing about the code that
    // produced it. CPP2IL_CC_LINES=CS0201,CS1061 collects the actual offending source lines for those
    // codes, normalised and counted, so the real shape distribution is visible instead of guessed at.
    private static readonly HashSet<string> LineCodes = new(
        (Environment.GetEnvironmentVariable("CPP2IL_CC_LINES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries),
        StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> ErrLines = new();

    private static void RecordSourceLine(string id, SyntaxTree tree, TextSpan span)
    {
        if (LineCodes.Count == 0 || !LineCodes.Contains(id))
            return;

        var text = tree.GetText();
        var line = text.Lines.GetLineFromPosition(span.Start).ToString().Trim();

        ErrLines.AddOrUpdate($"{id} | {Trim(line)}", 1, (_, v) => v + 1);
    }

    private static void PrintSourceLines()
    {
        if (ErrLines.IsEmpty)
            return;

        Console.WriteLine();
        Console.WriteLine("-- offending source lines (CPP2IL_CC_LINES) --");
        foreach (var kv in ErrLines.OrderByDescending(k => k.Value).Take(40))
            Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
    }

    private static readonly System.Text.RegularExpressions.Regex Quoted =
        new(@"'([^']*)'", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Collapse a diagnostic message to its structural shape: keep operators and real type names
    // (int, byte[], IntPtr, System.Type, ...) but replace obfuscated identifiers (long alnum mixed-
    // case tokens with no '.'/'[') with «id». This splits one error code into its distinct causes -
    // e.g. CS0019 "'&' on 'IntPtr' and 'int'" vs "'!=' on 'byte[]' and 'int'".
    private static string Normalize(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return msg;
        var s = Quoted.Replace(msg, m =>
        {
            var t = m.Groups[1].Value;
            if (t.Length > 12 && t.All(char.IsLetterOrDigit) && t.Any(char.IsUpper) && t.Any(char.IsLower))
                return "'<id>'";
            return "'" + t + "'";
        });
        return Trim(s);
    }

    private static bool IsMethodLike(SyntaxNode n) =>
        n is MethodDeclarationSyntax or ConstructorDeclarationSyntax or OperatorDeclarationSyntax
          or ConversionOperatorDeclarationSyntax or DestructorDeclarationSyntax or AccessorDeclarationSyntax;

    private static bool HasBody(SyntaxNode n) => n switch
    {
        BaseMethodDeclarationSyntax m => m.Body != null || m.ExpressionBody != null,
        AccessorDeclarationSyntax a => a.Body != null || a.ExpressionBody != null,
        _ => false,
    };
}
