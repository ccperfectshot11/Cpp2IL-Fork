using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Cpp2IL.VerifyCore;

namespace Cpp2IL.VerifyCheck;

// Tier 1 differential fuzzing: does the recovered code BEHAVE like the original?
//
// Every other measurement in this repo answers a different question. BodyScan counts the holes the
// decompiler admits to, CompileCheck counts the methods whose C# a compiler accepts. Neither notices a
// method that compiles cleanly, runs happily, and returns the wrong number - and a wrong number out of
// FPMath.Sin is a desync in a lockstep game, not a warning in a build log.
//
// The instrument is a signature: pick the methods that are self-contained enough to call, feed each one
// the same deterministic flood of inputs, and hash every input/output pair into one digest. Phase 1
// (this program) produces that digest from Cpp2IL's output. Phase 2 produces it from the real native
// method inside the running game - see README.md - and the diff of the two files is the verification.
// A signature on its own proves nothing except that the method is a function; it is the COMPARISON that
// is the measurement, so everything here is arranged around making the two files comparable.
//
//   Cpp2IL.VerifyCheck <dllDir> [dllNameSubstring] [options]
//     --seed N            run seed, default 20260910. Same seed => same inputs, on any runtime.
//     --iterations N      random inputs per method, default 10000 (the edge sweep is on top of this)
//     --edge-cap N        cap on the edge-case cartesian product per method, default 2048
//     --out FILE          signature json, default verifycheck-signatures.json
//     --select-only       report the selection and stop, without invoking anything
//     --max N             only fuzz the first N selected methods (for a quick pass)
//     --timeout S         seconds one method may take before the run is abandoned, default 30
//     --no-statics        drop methods that read static fields instead of flagging them
//     --compare A B       diff two signature files (this is what Phase 2 is for) and stop
internal static class Program
{
    private static volatile string _inFlight;
    private static long _inFlightSince;
    private static string _journalPath;

    private static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--compare")
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("usage: Cpp2IL.VerifyCheck --compare <phase1.json> <phase2.json>");
                return 2;
            }

            return Compare(args[1], args[2]);
        }

        if (args.Length < 1 || args[0].StartsWith("--"))
        {
            Console.Error.WriteLine("usage: Cpp2IL.VerifyCheck <dllDir> [dllNameSubstring] [--seed N] [--iterations N]");
            Console.Error.WriteLine("       [--edge-cap N] [--out FILE] [--select-only] [--max N] [--timeout S] [--no-statics]");
            Console.Error.WriteLine("       Cpp2IL.VerifyCheck --compare <phase1.json> <phase2.json>");
            return 2;
        }

        var dllDir = args[0];
        var filter = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        var plan = new FuzzPlan
        {
            Seed = ulong.Parse(Option(args, "--seed") ?? "20260910", CultureInfo.InvariantCulture),
            RandomIterations = int.Parse(Option(args, "--iterations") ?? "10000", CultureInfo.InvariantCulture),
            MaxEdgeIterations = int.Parse(Option(args, "--edge-cap") ?? "2048", CultureInfo.InvariantCulture),
        };

        var outPath = Option(args, "--out") ?? "verifycheck-signatures.json";
        var selectOnly = args.Contains("--select-only");
        var allowStatics = !args.Contains("--no-statics");
        var max = int.Parse(Option(args, "--max") ?? int.MaxValue.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var timeout = TimeSpan.FromSeconds(double.Parse(Option(args, "--timeout") ?? "30", CultureInfo.InvariantCulture));

        if (!Directory.Exists(dllDir))
        {
            Console.Error.WriteLine("no such directory: " + dllDir);
            return 2;
        }

        // Absolute from here on: AssemblyLoadContext.LoadFromAssemblyPath rejects a relative path, and it
        // does so per method, so a run started with a relative directory reports every single method as
        // a load failure rather than as an error.
        dllDir = Path.GetFullPath(dllDir);

        var watch = Stopwatch.StartNew();
        var selection = Selector.Select(dllDir, filter, allowStatics);
        ReportSelection(selection, watch);

        if (selectOnly)
            return 0;

        var results = Fuzz(dllDir, selection, plan, max, timeout, outPath);
        using (var writer = new StreamWriter(outPath, false))
            SignatureJson.Write(writer, SignatureJson.PhaseRecovered, Path.GetFullPath(dllDir), plan, results);

        ReportRun(results, outPath, watch);
        return 0;
    }

    private static string Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // The five Photon Quantum fixed-point types. Matched on the simple name rather than the namespace so
    // a build where the namespace did not survive still lands in the same bucket. These are the reason
    // the tool exists: Quantum is lockstep-deterministic, so "close enough" is not a category there -
    // one raw unit of difference in FPMath.Sin desyncs a match. If bit-exact comparison is legitimate
    // anywhere in a game, it is here.
    private static readonly string[] QuantumTypes = ["FP", "FPMath", "FPVector2", "FPVector3", "FPQuaternion"];

    private static void ReportSelection(SelectionResult selection, Stopwatch watch)
    {
        double P(long a, long b) => b == 0 ? 0 : 100.0 * a / b;

        Console.WriteLine();
        Console.WriteLine("================ TIER 1: DIFFERENTIAL FUZZ - SELECTION ================");
        Console.WriteLine($"DLLs scanned               : {selection.DllsScanned:N0}   (unreadable: {selection.DllsUnreadable})");
        Console.WriteLine($"Methods with a body        : {selection.MethodsWithBody:N0}");
        Console.WriteLine($"  body-safe (own checks)   : {selection.All.Count(c => c.BodySafe):N0}  ({P(selection.All.Count(c => c.BodySafe), selection.MethodsWithBody):F2}%)");
        Console.WriteLine($"SELECTED (fuzzable)        : {selection.Selected.Count:N0}  ({P(selection.Selected.Count, selection.MethodsWithBody):F3}%)   <== static, primitive-only, closed call graph");
        Console.WriteLine($"  of those, read statics   : {selection.Selected.Count(c => c.ReadsStatics):N0}");
        Console.WriteLine($"  static-only whitelist    : {selection.SelectedStaticOnly:N0}   (the literal reading: instance helpers not allowed even as callees)");
        Console.WriteLine();
        Console.WriteLine("-- why the rest were dropped (first failing check) --");
        foreach (var kv in selection.DropReasons.OrderByDescending(k => k.Value).Take(15))
            Console.WriteLine($"  {kv.Key,-42} {kv.Value,10:N0}");

        Console.WriteLine();
        Console.WriteLine("-- selected methods by declaring type (top 25) --");
        foreach (var group in selection.Selected.GroupBy(c => c.TypeName).OrderByDescending(g => g.Count()).Take(25))
            Console.WriteLine($"  {group.Count(),6:N0}  {group.Key}");

        Console.WriteLine();
        Console.WriteLine("-- * PHOTON QUANTUM FIXED-POINT MATHS * --");
        long quantumSelected = 0, quantumTotal = 0;
        foreach (var name in QuantumTypes)
        {
            var all = selection.All.Where(c => SimpleName(c.TypeName) == name).ToList();
            var selected = selection.Selected.Where(c => SimpleName(c.TypeName) == name).ToList();
            quantumSelected += selected.Count;
            quantumTotal += all.Count;
            var reasons = string.Join(", ", all.Where(c => !c.BodySafe || !selected.Contains(c))
                .GroupBy(c => c.Reason).OrderByDescending(g => g.Count()).Take(3)
                .Select(g => $"{g.Key} x{g.Count()}"));
            Console.WriteLine($"  {name,-14} {selected.Count,5:N0} / {all.Count,5:N0} selected   {reasons}");
        }

        Console.WriteLine($"  {"TOTAL",-14} {quantumSelected,5:N0} / {quantumTotal,5:N0} selected");
        Console.Error.WriteLine($"[selection done in {watch.Elapsed.TotalSeconds:F0}s]");
    }

    private static string SimpleName(string fullName)
    {
        var dot = fullName.LastIndexOf('.');
        return dot >= 0 ? fullName.Substring(dot + 1) : fullName;
    }

    private static List<MethodFuzzResult> Fuzz(string dllDir, SelectionResult selection, FuzzPlan plan, int max, TimeSpan timeout, string outPath)
    {
        var results = new List<MethodFuzzResult>();
        var context = new RecoveredAssemblyContext(dllDir);
        var assemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        // Crash journal. A fuzzed method can take the whole process down - an access violation cannot be
        // caught, and a stack overflow from a mutually recursive pair is fatal by design in .NET - so the
        // method being invoked is written to disk BEFORE it is invoked. Whatever is still in the journal
        // when the process starts again is what killed it, and it goes on the skip list. Without this a
        // single bad method means the run never finishes, no matter how many times it is started.
        _journalPath = outPath + ".inflight";
        var skipPath = outPath + ".skip";
        var skip = File.Exists(skipPath)
            ? new HashSet<string>(File.ReadAllLines(skipPath).Where(l => l.Length > 0), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        if (File.Exists(_journalPath))
        {
            var crashed = File.ReadAllText(_journalPath).Trim();
            if (crashed.Length > 0 && skip.Add(crashed))
            {
                File.AppendAllText(skipPath, crashed + Environment.NewLine);
                Console.Error.WriteLine($"!! previous run died inside {crashed} - skipping it from now on");
            }

            File.Delete(_journalPath);
        }

        StartWatchdog(timeout, skipPath);

        var todo = selection.Selected.Take(max).ToList();
        var done = 0;
        foreach (var candidate in todo)
        {
            done++;
            var identity = candidate.AssemblyName + "::" + candidate.TypeName + "::" + candidate.MethodName + " (0x" + candidate.Token.ToString("X8") + ")";
            if (skip.Contains(identity))
                continue;

            if (done % 200 == 0)
                Console.Error.WriteLine($"[{done}/{todo.Count}] {candidate.TypeName}::{candidate.MethodName}");

            MethodFuzzResult result;
            try
            {
                if (RecoveredAssemblyContext.IsHostOwned(candidate.AssemblyName))
                    throw new BadImageFormatException("recovered core library: cannot be hosted next to the runtime's own");

                if (!assemblies.TryGetValue(candidate.DllPath, out var assembly))
                    assemblies[candidate.DllPath] = assembly = context.LoadFromAssemblyPath(candidate.DllPath);

                // Resolved by metadata token rather than by name and signature: the recovered build is
                // full of overloads that differ only in types reflection prints identically, and picking
                // the wrong one would silently fuzz a different method than the one that was selected.
                if (assembly.ManifestModule.ResolveMethod((int)candidate.Token) is not { } method)
                    throw new MissingMethodException("token did not resolve");

                _inFlight = identity;
                Interlocked.Exchange(ref _inFlightSince, Stopwatch.GetTimestamp());
                File.WriteAllText(_journalPath, identity);

                result = MethodFuzzer.Run(method, plan);
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                // The proper key needs the reflected signature, and there is none - the method never
                // loaded. Token-qualified so the entries stay distinct in the file; it will not match
                // anything in a Phase 2 file, but an entry with no signature has nothing to match.
                result = new MethodFuzzResult
                {
                    Key = candidate.TypeName + "::" + candidate.MethodName + " @" + candidate.AssemblyName + ":0x" + candidate.Token.ToString("X8"),
                    Type = candidate.TypeName,
                    Method = candidate.MethodName,
                    Failure = inner.GetType().Name + ": " + inner.Message,
                };
            }
            finally
            {
                _inFlight = null;
            }

            result.Assembly = candidate.AssemblyName;
            result.Token = "0x" + candidate.Token.ToString("X8");
            result.ReadsStatics = candidate.ReadsStatics;
            if (string.IsNullOrEmpty(result.Type))
                result.Type = candidate.TypeName;

            results.Add(result);
        }

        if (File.Exists(_journalPath))
            File.Delete(_journalPath);

        return results;
    }

    // .NET has no way to stop a thread that will not stop: Thread.Abort throws PlatformNotSupported on
    // core, so an infinite loop in a recovered body can only be survived by ending the process. The
    // journal is already on disk, so the next run skips the culprit and continues from there.
    private static void StartWatchdog(TimeSpan timeout, string skipPath)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(1000);
                var current = _inFlight;
                if (current == null)
                    continue;

                var elapsed = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - Interlocked.Read(ref _inFlightSince)) / (double)Stopwatch.Frequency);
                if (elapsed < timeout)
                    continue;

                Console.Error.WriteLine($"!! {current} has been running for {elapsed.TotalSeconds:F0}s - abandoning the run");
                File.AppendAllText(skipPath, current + Environment.NewLine);
                Console.Error.WriteLine("   re-run the same command: it will skip this method and carry on");
                Environment.Exit(4);
            }
        })
        { IsBackground = true, Name = "verifycheck-watchdog" };

        thread.Start();
    }

    private static void ReportRun(List<MethodFuzzResult> results, string outPath, Stopwatch watch)
    {
        double P(long a, long b) => b == 0 ? 0 : 100.0 * a / b;
        var ran = results.Where(r => r.Supported).ToList();

        Console.WriteLine();
        Console.WriteLine("================ TIER 1: DIFFERENTIAL FUZZ - RUN ================");
        Console.WriteLine($"Methods attempted          : {results.Count:N0}");
        Console.WriteLine($"  fuzzed                   : {ran.Count:N0}  ({P(ran.Count, results.Count):F1}%)");
        Console.WriteLine($"  never invoked            : {results.Count - ran.Count:N0}   (load, resolve or shape failure)");
        Console.WriteLine($"  ran, never threw         : {ran.Count(r => r.ThrewCount == 0):N0}  ({P(ran.Count(r => r.ThrewCount == 0), ran.Count):F1}%)");
        Console.WriteLine($"  threw on SOME inputs     : {ran.Count(r => r.ThrewCount > 0 && !r.AllThrew):N0}");
        Console.WriteLine($"  threw on EVERY input     : {ran.Count(r => r.AllThrew):N0}   <== a body that never returns anything is not recovered code");
        Console.WriteLine($"    of those, INVALID IL   : {ran.Count(r => r.ExceptionKinds.Contains("System.InvalidProgramException")):N0}   <== the JIT refused the body outright");
        Console.WriteLine($"    sweep aborted early    : {ran.Count(r => r.AbortedAfter > 0):N0}   (the failure is in the method, not in the input)");
        Console.WriteLine($"  NON-DETERMINISTIC        : {ran.Count(r => r.NonDeterministic):N0}   <== not a function of its arguments, cannot be compared");
        Console.WriteLine($"  CONSTANT OUTPUT          : {ran.Count(r => r.ConstantOutput):N0}   <== ran clean and ignored every argument: a body with its logic missing");
        Console.WriteLine($"  reads static fields      : {ran.Count(r => r.ReadsStatics):N0}   (comparable only if the game's cctor produced the same tables)");
        Console.WriteLine($"Total invocations          : {ran.Sum(r => r.AbortedAfter > 0 ? r.AbortedAfter : (long)(r.EdgeIterations + r.RandomIterations)) * 2:N0}   (two passes, for the determinism check)");
        Console.WriteLine($"Elapsed                    : {watch.Elapsed.TotalSeconds:F0}s");
        Console.WriteLine($"Signatures                 : {Path.GetFullPath(outPath)}");

        var quantum = ran.Where(r => QuantumTypes.Contains(SimpleName(r.Type ?? ""))).ToList();
        if (quantum.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("-- * PHOTON QUANTUM FIXED-POINT MATHS: signatures recorded * --");
            Console.WriteLine($"  methods fuzzed           : {quantum.Count:N0}");
            Console.WriteLine($"  clean (never threw)      : {quantum.Count(r => r.ThrewCount == 0):N0}");
            Console.WriteLine($"  threw on every input     : {quantum.Count(r => r.AllThrew):N0}");
            Console.WriteLine($"  non-deterministic        : {quantum.Count(r => r.NonDeterministic):N0}");
            // Only the ones that ran to completion: an aborted sweep has a digest too, but it is a
            // digest of thirty-two identical JIT failures and printing it next to a real one invites
            // reading it as a result.
            foreach (var r in quantum.Where(r => r.AbortedAfter == 0 && r.ThrewCount == 0).OrderBy(r => r.Type).ThenBy(r => r.Method).Take(25))
                Console.WriteLine($"    {r.Signature}  {SimpleName(r.Type),-13} {r.Method}");
        }

        var constant = ran.Where(r => r.ConstantOutput && r.ThrewCount == 0).ToList();
        if (constant.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("-- ran clean but returned the SAME value for every input (top 20) --");
            foreach (var r in constant.Take(20))
                Console.WriteLine($"  {SimpleName(r.Type),-30} {r.Method}");
        }

        var broken = ran.Where(r => r.AllThrew).ToList();
        if (broken.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("-- methods that threw on EVERY input (highest-value leads, top 20) --");
            foreach (var r in broken.Take(20))
                Console.WriteLine($"  {SimpleName(r.Type),-24} {r.Method,-28} {string.Join(", ", r.ExceptionKinds)}");
        }

        var failures = results.Where(r => !r.Supported).GroupBy(r => Shorten(r.Failure)).OrderByDescending(g => g.Count()).Take(10).ToList();
        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("-- never invoked, by cause --");
            foreach (var g in failures)
                Console.WriteLine($"  {g.Count(),6:N0}  {g.Key}");
        }
    }

    private static string Shorten(string message)
    {
        if (string.IsNullOrEmpty(message))
            return "(none)";

        return message.Length <= 120 ? message : message.Substring(0, 120);
    }

    // The actual verification, once Phase 2 exists: two files, one signature per method key, and the only
    // thing that matters is whether the digests agree. Everything else in this program is machinery for
    // making this comparison possible.
    private static int Compare(string leftPath, string rightPath)
    {
        var left = ReadSignatures(leftPath);
        var right = ReadSignatures(rightPath);

        long agree = 0, differ = 0, skipped = 0;
        var mismatches = new List<string>();
        foreach (var kv in left)
        {
            if (!right.TryGetValue(kv.Key, out var other))
                continue;

            // A method that was not a function of its arguments on either side has nothing to compare:
            // reporting it as a mismatch would bury the real ones.
            if (kv.Value.NonDeterministic || other.NonDeterministic || kv.Value.Aborted || other.Aborted || kv.Value.Signature == null || other.Signature == null)
            {
                skipped++;
                continue;
            }

            if (kv.Value.Signature == other.Signature)
                agree++;
            else
            {
                differ++;
                mismatches.Add($"  {kv.Key}\n      recovered {kv.Value.Signature}   native {other.Signature}");
            }
        }

        double P(long a, long b) => b == 0 ? 0 : 100.0 * a / b;
        var shared = agree + differ;
        Console.WriteLine();
        Console.WriteLine("================ TIER 1: SIGNATURE COMPARISON ================");
        Console.WriteLine($"{Path.GetFileName(leftPath)} methods  : {left.Count:N0}");
        Console.WriteLine($"{Path.GetFileName(rightPath)} methods  : {right.Count:N0}");
        Console.WriteLine($"comparable (in both)       : {shared:N0}");
        Console.WriteLine($"  BEHAVIOURALLY IDENTICAL  : {agree:N0}  ({P(agree, shared):F1}%)   <== THE HONEST NUMBER");
        Console.WriteLine($"  DIFFERENT                : {differ:N0}  ({P(differ, shared):F1}%)");
        Console.WriteLine($"  not comparable           : {skipped:N0}   (non-deterministic on one side)");
        Console.WriteLine($"  only in {Path.GetFileName(leftPath),-18}: {left.Count(kv => !right.ContainsKey(kv.Key)):N0}");
        Console.WriteLine($"  only in {Path.GetFileName(rightPath),-18}: {right.Count(kv => !left.ContainsKey(kv.Key)):N0}");
        if (mismatches.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("-- methods that behave differently (top 40) --");
            foreach (var line in mismatches.Take(40))
                Console.WriteLine(line);
        }

        return differ == 0 ? 0 : 1;
    }

    private sealed class SignatureEntry
    {
        public string Signature;
        public bool NonDeterministic;
        public bool Aborted;
    }

    private static Dictionary<string, SignatureEntry> ReadSignatures(string path)
    {
        var map = new Dictionary<string, SignatureEntry>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var element in document.RootElement.GetProperty("methods").EnumerateArray())
        {
            var key = element.GetProperty("key").GetString();
            if (key == null)
                continue;

            // A key that appears twice cannot be compared: there is no way to tell which of the two
            // entries on this side belongs with which on the other.
            if (map.ContainsKey(key))
            {
                map[key].Signature = null;
                continue;
            }

            map[key] = new SignatureEntry
            {
                Signature = element.TryGetProperty("signature", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null,
                NonDeterministic = element.TryGetProperty("nonDeterministic", out var n) && n.ValueKind == JsonValueKind.True,

                // A truncated sweep covered a different number of inputs, so its digest is not the same
                // experiment as a full one - comparing them would manufacture a mismatch.
                Aborted = element.TryGetProperty("abortedAfter", out var a) && a.ValueKind == JsonValueKind.Number && a.GetInt32() > 0,
            };
        }

        return map;
    }
}
