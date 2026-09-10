using System;
using System.IO;
using System.Linq;
using System.Threading;
using NetSpyAdapter;

// Level-3 measurement (web-Claude): take the Cpp2IL DLLs and run each type through the NetSpy
// (dnSpy/ILSpy) decompiler, then measure how faithfully the C# comes out. Stage A here: decompile
// every type, optionally write the .cs, and tally decompiler crashes + leftover artifacts
// (NativeMethod_0x*, NoteDecompilerIssue markers, goto, the "// NetSpy decompile failed" comment).
//
//   Cpp2IL.RecompileCheck <dllDir> <outDir|-> [dllNameSubstring]
//     outDir "-" = measure only, don't write files.
//
// The decompiler recurses over control flow, so a deeply nested method can blow the default 1 MB
// stack (uncatchable StackOverflow kills the process). We run every assembly on its own 512 MB
// thread so one pathological assembly can't abort the whole measurement.
internal static class Program
{
    private static string _referenceDirectory;
    private static long types, decompFail, withNativeMethod, withMarker, withGoto, clean;
    private static bool _write;
    private static string _outDir;

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: Cpp2IL.RecompileCheck <dllDir> <outDir|-> [dllNameSubstring]");
            return 2;
        }

        var dllDir = args[0];
        _referenceDirectory = Path.GetFullPath(dllDir);
        _outDir = args[1];
        var filter = args.Length > 2 ? args[2] : null;
        _write = _outDir != "-";
        if (_write) Directory.CreateDirectory(_outDir);

        var dlls = Directory.GetFiles(dllDir, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(p => filter == null || Path.GetFileName(p).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(p => p)
            .ToArray();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < dlls.Length; i++)
        {
            var dll = dlls[i];
            var before = types;
            var t = new Thread(() => DecompileAssemblyFile(dll), 512 * 1024 * 1024);
            t.Start();
            t.Join();
            var memMb = GC.GetTotalMemory(false) / (1024 * 1024);
            Console.Error.WriteLine($"[{i + 1}/{dlls.Length}] {Path.GetFileNameWithoutExtension(dll),-42} +{types - before,-5} types  (total {types:N0}, {memMb} MB, {sw.Elapsed.TotalSeconds:F0}s)");
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        double Pct(long n) => types == 0 ? 0 : 100.0 * n / types;
        Console.WriteLine("================ LEVEL 3: DECOMPILE FAITHFULNESS (Stage A) ================");
        Console.WriteLine($"DLLs decompiled           : {dlls.Length}");
        Console.WriteLine($"Types decompiled          : {types:N0}");
        Console.WriteLine($"  decompiler CRASHED       : {decompFail:N0}  ({Pct(decompFail):F1}%)   <- '// NetSpy decompile failed'");
        Console.WriteLine($"  has NativeMethod_0x*     : {withNativeMethod:N0}  ({Pct(withNativeMethod):F1}%)   <- unresolved native call stub");
        Console.WriteLine($"  has NoteDecompilerIssue  : {withMarker:N0}  ({Pct(withMarker):F1}%)   <- a lift-failure marker");
        Console.WriteLine($"  has goto                 : {withGoto:N0}  ({Pct(withGoto):F1}%)   <- irreducible control flow");
        Console.WriteLine($"  CLEAN (none of the above): {clean:N0}  ({Pct(clean):F1}%)");
        if (_write) Console.WriteLine($"C# written to             : {_outDir}");
        return 0;
    }

    private static void DecompileAssemblyFile(string dll)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(dll); }
        catch { return; }

        var asmName = Path.GetFileNameWithoutExtension(dll);
        var asmOut = _write ? Path.Combine(_outDir, asmName) : null;

        try
        {
            NetSpyDecompiler.DecompileAssembly(bytes, (rel, code) =>
            {
                Interlocked.Increment(ref types);
                if (_write)
                {
                    var full = Path.Combine(asmOut, rel);
                    try { Directory.CreateDirectory(Path.GetDirectoryName(full)); File.WriteAllText(full, code); } catch { }
                }

                var failed = code.Contains("// NetSpy decompile failed");
                var nativ = code.Contains("NativeMethod_0x");
                var marker = code.Contains("NoteDecompilerIssue");
                var gotoo = code.Contains("goto ");

                if (failed) Interlocked.Increment(ref decompFail);
                if (nativ) Interlocked.Increment(ref withNativeMethod);
                if (marker) Interlocked.Increment(ref withMarker);
                if (gotoo) Interlocked.Increment(ref withGoto);
                if (!failed && !nativ && !marker && !gotoo) Interlocked.Increment(ref clean);
            }, _referenceDirectory);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  [assembly decompile error] {asmName}: {e.Message}");
        }
    }
}
