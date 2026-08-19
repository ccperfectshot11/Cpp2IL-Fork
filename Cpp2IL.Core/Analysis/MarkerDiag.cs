using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AsmResolver.DotNet;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

// Per-marker diagnostic (env CPP2IL_MARKERDIAG=1). Called exactly once at each "Unmanaged memory
// load" emission in IlGenerator, so counts are per marker in the FINAL output - no fixpoint
// inflation. Implements the full A1-4 / B1-4 taxonomy web-Claude asked for, an empirical flattening
// counterfactual (own-type-only lookup vs base-chain-walk lookup, to show whether walking base types
// recovers anything - production already walks the chain at MetadataResolver.cs:262, so this should
// be ~0), and per-method attribution so we can report, per bucket, how many methods lose ALL their
// Unmanaged markers if only that bucket is fixed. Acceptance test: the leaf buckets sum to total.
internal static class MarkerDiag
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("CPP2IL_MARKERDIAG") == "1";

    // leaf bucket name -> count. every marker lands in exactly one leaf, so these sum to _total.
    private static readonly ConcurrentDictionary<string, long> Buckets = new();
    private static long _total;

    // empirical flattening counterfactual: a field exists at the exact offset among the base CHAIN
    // (inherited) but NOT among the base type's own declared fields. production already walks the
    // chain, so this predicts the NET-NEW yield of "add inheritance to lookup" = expected ~0.
    private static long _flatteningRecovers;

    // delta-to-nearest-field histogram for the B (real class, no field) family.
    private static readonly ConcurrentDictionary<long, long> DeltaBucket = new();

    // per-method attribution: methodKey -> (bucket -> count). used for "methods that lose all their
    // Unmanaged markers if this bucket is fixed" and dumped to JSON for the BodyScan all-category join.
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, long>> PerMethod = new();

    // Method-not-found (Immediate call target) classification, to locate the regression: per target
    // address, is it flagged as a key function (Fix E territory), how many managed candidates does
    // MethodsByAddress hold, and which named key function (if any) does it map to.
    private static readonly ConcurrentDictionary<ulong, long> MnfCount = new();
    private static readonly ConcurrentDictionary<ulong, long> MnfVoidCount = new();   // of those, how many are CallVoid (return unused -> safe to elide)
    private static readonly ConcurrentDictionary<ulong, (bool isKey, string name, int cands, int dethunkCands, ulong dethunkTarget)> MnfClass = new();

    private static int _hooked;

    private static void Bump(string bucket, string methodKey)
    {
        Buckets.AddOrUpdate(bucket, 1, (_, v) => v + 1);
        var m = PerMethod.GetOrAdd(methodKey, _ => new ConcurrentDictionary<string, long>());
        m.AddOrUpdate(bucket, 1, (_, v) => v + 1);
    }

    private static void Hook()
    {
        if (System.Threading.Interlocked.Exchange(ref _hooked, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dump();
    }

    private static void Dump()
    {
        // leaf order: keep the taxonomy grouped for readability.
        string[] order =
        {
            "INDEXED",
            "A4_untyped_base",
            "BYREF_base",
            "A1_value_primitive", "A1_value_struct",
            "A2_object", "A2_other_nofields", "A3_interface",
            "B1_header_below_first", "B2_beyond_last", "B3_between_fields", "B4_exact_hit_lookup_miss",
        };

        long sum = order.Sum(b => Buckets.TryGetValue(b, out var v) ? v : 0);
        long listedTotal = Buckets.Values.Sum();

        Console.WriteLine("==== PER-MARKER DIAG v2 (Unmanaged memory load, counted once each) ====");
        Console.WriteLine($"  total markers                 : {_total}");
        foreach (var b in order)
            Console.WriteLine($"    {b,-28}: {(Buckets.TryGetValue(b, out var v) ? v : 0)}");
        foreach (var kv in Buckets.Where(k => !order.Contains(k.Key)).OrderByDescending(k => k.Value))
            Console.WriteLine($"    {kv.Key,-28}: {kv.Value}   <-- UNLISTED");
        Console.WriteLine($"  -- ACCEPTANCE: listed-sum={sum}  all-buckets-sum={listedTotal}  total={_total}  " +
                          (sum == _total && listedTotal == _total ? "CLOSES" : "MISMATCH!!"));
        Console.WriteLine($"  flattening counterfactual (exact-offset field in base chain but not own type): {_flatteningRecovers}");

        Console.WriteLine("  -- B family: delta from offset down to nearest field below --");
        foreach (var kv in DeltaBucket.OrderByDescending(k => k.Value).Take(20))
            Console.WriteLine($"     delta 0x{kv.Key:X4} : {kv.Value}");

        Console.WriteLine("  -- per bucket: [markers] and [methods that become Unmanaged-clean if only this bucket fixed] --");
        foreach (var b in order)
        {
            long markers = Buckets.TryGetValue(b, out var v) ? v : 0;
            if (markers == 0) continue;
            int methodsCleaned = PerMethod.Count(m =>
            {
                var d = m.Value;
                return d.TryGetValue(b, out var inBucket) && inBucket > 0 && d.Values.Sum() == inBucket;
            });
            Console.WriteLine($"     {b,-28}: markers={markers,-7} methods_fully_unmanaged_clean={methodsCleaned}");
        }

        DumpMnf();

        try
        {
            var dir = Path.Combine(Directory.GetCurrentDirectory(), "bodyscan-runs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "marker_methods.json");
            var sb = new StringBuilder();
            sb.Append('{');
            bool firstM = true;
            foreach (var m in PerMethod)
            {
                if (!firstM) sb.Append(','); firstM = false;
                sb.Append('"').Append(JsonEscape(m.Key)).Append("\":{");
                bool firstB = true;
                foreach (var kv in m.Value)
                {
                    if (!firstB) sb.Append(','); firstB = false;
                    sb.Append('"').Append(kv.Key).Append("\":").Append(kv.Value);
                }
                sb.Append('}');
            }
            sb.Append('}');
            File.WriteAllText(path, sb.ToString());
            Console.WriteLine($"  wrote per-method buckets -> {path} ({PerMethod.Count} methods)");
        }
        catch (Exception e) { Console.WriteLine($"  (per-method json dump failed: {e.Message})"); }
    }

    private static string JsonEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public static void Record(MemoryOperand memory, MethodDefinition method)
    {
        if (!Enabled) return;
        Hook();
        System.Threading.Interlocked.Increment(ref _total);
        var mk = (method.DeclaringType?.Name ?? "?") + "::" + method.Name;

        // 1. indexed access ([base + index*scale]) - not a plain field shape.
        if (memory.Index != null || memory.Scale != 0) { Bump("INDEXED", mk); return; }

        // 2. base is not a typed local -> type-prop never reached it (A4).
        if (memory.Base is not LocalVariable { Type: { } baseType }) { Bump("A4_untyped_base", mk); return; }

        // 3. byref base (managed pointer). Fix-D territory; kept as its own leaf.
        if (baseType is ByRefTypeAnalysisContext) { Bump("BYREF_base", mk); return; }

        // 4. value-type base (not byref): A1. split primitive/no-field vs struct-with-fields.
        if (baseType.IsValueType)
        {
            bool hasFields = EnumerateInstanceFields(baseType).Any();
            Bump(hasFields ? "A1_value_struct" : "A1_value_primitive", mk);
            return;
        }

        // 5. typed reference type. compute flattened (base-chain) + own-only field layout.
        long maxOffset = -1, nearestBelow = -1;
        bool flatExact = false, ownExact = false;
        long firstOffset = long.MaxValue;
        long fieldCount = 0;
        for (var t = baseType; t != null; t = t.BaseType)
            foreach (var f in t.Fields)
                if (!f.IsStatic && f.BackingData is { } bd)
                {
                    fieldCount++;
                    var off = bd.FieldOffset;
                    if (off > maxOffset) maxOffset = off;
                    if (off < firstOffset) firstOffset = off;
                    if (off <= memory.Addend && off > nearestBelow) nearestBelow = off;
                    if (off == memory.Addend) { flatExact = true; if (ReferenceEquals(t, baseType)) ownExact = true; }
                }

        if (fieldCount == 0)
        {
            if (baseType.FullName == "System.Object") Bump("A2_object", mk);
            else if (baseType.IsInterface) Bump("A3_interface", mk);
            else Bump("A2_other_nofields", mk);
            return;
        }

        if (flatExact && !ownExact) System.Threading.Interlocked.Increment(ref _flatteningRecovers);

        string bBucket;
        if (flatExact) bBucket = "B4_exact_hit_lookup_miss";
        else if (memory.Addend < firstOffset) bBucket = "B1_header_below_first";
        else if (memory.Addend > maxOffset) bBucket = "B2_beyond_last";
        else bBucket = "B3_between_fields";
        Bump(bBucket, mk);

        var delta = nearestBelow < 0 ? -1 : memory.Addend - nearestBelow;
        var dkey = delta < 0 ? -1 : delta > 0x200 ? 0x1000 : delta;
        DeltaBucket.AddOrUpdate(dkey, 1, (_, v) => v + 1);
    }

    // Called once at each "Method not found @addr" emission in IlGenerator. Classifies the target.
    // isVoid = the call had no result operand (CallVoid): its return is unused, so if the target is a
    // pure runtime helper (metadata/class init, write barrier) the whole call can be safely elided.
    public static void RecordMnf(ulong addr, ApplicationAnalysisContext app, bool isVoid)
    {
        if (!Enabled) return;
        Hook();
        DumpKeyFunctionsOnce(app);
        MnfCount.AddOrUpdate(addr, 1, (_, v) => v + 1);
        if (isVoid) MnfVoidCount.AddOrUpdate(addr, 1, (_, v) => v + 1);
        if (!MnfClass.ContainsKey(addr))
        {
            bool isKey = false; string name = ""; int cands = 0; int dethunkCands = 0; ulong dethunkTarget = 0;
            try
            {
                var kf = app.GetOrCreateKeyFunctionAddresses();
                isKey = kf.IsKeyFunctionAddress(addr);
                name = kf.Pairs.Where(p => p.Value == addr).Select(p => p.Key).FirstOrDefault() ?? "";
            }
            catch { }
            if (app.MethodsByAddress.TryGetValue(addr, out var l)) cands = l.Count;
            // If the address is not a managed method, follow it as a jmp thunk and see whether the
            // TARGET is a managed method. This measures the yield of de-thunking call targets.
            if (cands == 0)
            {
                try
                {
                    dethunkTarget = app.InstructionSet.GetThunkTarget(app, addr);
                    if (dethunkTarget != 0 && app.MethodsByAddress.TryGetValue(dethunkTarget, out var tl))
                        dethunkCands = tl.Count;
                }
                catch { }
            }
            MnfClass[addr] = (isKey, name, cands, dethunkCands, dethunkTarget);
        }
    }

    private static int _kfDumped;
    private static void DumpKeyFunctionsOnce(ApplicationAnalysisContext app)
    {
        if (System.Threading.Interlocked.Exchange(ref _kfDumped, 1) != 0) return;
        try
        {
            var kf = app.GetOrCreateKeyFunctionAddresses();
            var pairs = kf.Pairs.OrderBy(p => p.Value).ToList();
            Console.WriteLine($"==== DETECTED KEY FUNCTIONS: {pairs.Count(p => p.Value != 0)} found / {pairs.Count} total ====");
            foreach (var p in pairs)
                Console.WriteLine($"    {(p.Value == 0 ? "MISSING" : $"0x{p.Value:X}"),-14} {p.Key}");
        }
        catch (Exception e) { Console.WriteLine($"  (key-function dump failed: {e.Message})"); }
    }

    public static void DumpMnf()
    {
        if (MnfCount.Count == 0) return;
        (bool isKey, string name, int cands, int dethunkCands, ulong dethunkTarget) Def = (false, "", 0, 0, 0UL);
        long total = MnfCount.Values.Sum();
        long keyM = 0, multiM = 0, singleM = 0, noneM = 0;
        long dethunkResolvable = 0, dethunkSingle = 0;   // cands==0 markers recoverable by following the jmp thunk
        foreach (var kv in MnfCount)
        {
            var cl = MnfClass.TryGetValue(kv.Key, out var c) ? c : Def;
            if (cl.isKey) keyM += kv.Value;
            else if (cl.cands >= 2) multiM += kv.Value;
            else if (cl.cands == 1) singleM += kv.Value;
            else
            {
                noneM += kv.Value;
                if (cl.dethunkCands >= 1) { dethunkResolvable += kv.Value; if (cl.dethunkCands == 1) dethunkSingle += kv.Value; }
            }
        }
        long noneVoid = 0, noneNonVoid = 0;
        foreach (var kv in MnfCount)
        {
            var cl = MnfClass.TryGetValue(kv.Key, out var c) ? c : Def;
            if (cl.isKey || cl.cands >= 1) continue;      // only the cands==0 runtime-helper bucket
            var v = MnfVoidCount.TryGetValue(kv.Key, out var vc) ? vc : 0;
            noneVoid += v; noneNonVoid += kv.Value - v;
        }
        Console.WriteLine("==== METHOD-NOT-FOUND CLASSIFICATION ====");
        Console.WriteLine($"  total MNF markers      : {total}  over {MnfCount.Count} distinct targets");
        Console.WriteLine($"  isKeyFunctionAddress   : {keyM}   <- Fix E territory (key-func skipped managed binding)");
        Console.WriteLine($"  MethodsByAddress >=2   : {multiM}   <- multi-candidate (deferred to fixpoint)");
        Console.WriteLine($"  MethodsByAddress ==1   : {singleM}   <- single managed candidate, still unresolved");
        Console.WriteLine($"  MethodsByAddress ==0   : {noneM}   <- unknown / thunk / native (runtime helpers)");
        Console.WriteLine($"     of those: CallVoid (return unused, SAFE-ELIDE) : {noneVoid}    Call (return USED, needs modelling) : {noneNonVoid}");
        Console.WriteLine($"     of those, DE-THUNK to a managed method : {dethunkResolvable}   (single candidate: {dethunkSingle})");
        Console.WriteLine("  top 25 targets (addr : count  void/total  isKey  cands  dethunk  keyName):");
        foreach (var kv in MnfCount.OrderByDescending(k => k.Value).Take(25))
        {
            var cl = MnfClass.TryGetValue(kv.Key, out var c) ? c : Def;
            var dt = cl.dethunkTarget != 0 ? $"->{cl.dethunkTarget:X}({cl.dethunkCands})" : "";
            var vv = MnfVoidCount.TryGetValue(kv.Key, out var vc) ? vc : 0;
            Console.WriteLine($"    @{kv.Key:X}  {kv.Value,-7} void={vv,-6} isKey={cl.isKey,-5} cands={cl.cands,-3} {dt,-16} {cl.name}");
        }
    }

    private static IEnumerable<FieldAnalysisContext> EnumerateInstanceFields(TypeAnalysisContext t)
    {
        foreach (var f in t.Fields)
            if (!f.IsStatic && f.BackingData is not null)
                yield return f;
    }
}
