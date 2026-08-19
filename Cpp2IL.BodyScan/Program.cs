using System.Text.Json;
using System.Text.RegularExpressions;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;

// Measures how complete Cpp2IL's recovered method bodies are.
//
// IlGenerator emits a call to Cpp2ILInjected.Cpp2ILHelpers::NoteDecompilerIssue(string) wherever it
// could not lift something, preceded by an ldstr describing the problem. Counting those markers over
// a whole output directory is the ground truth for how much real logic made it into the bodies, which
// the decompiler's own "100% of methods decompiled" line does not tell you - that only means nothing
// crashed. Run this against an --output-to directory after a run.
//
// Usage:
//   Cpp2IL.BodyScan <dllDir> [--detail <category>] [--save <file.json>] [--baseline <file.json>]
//
//   --detail    also list the distinct (normalised) marker messages for one category, biggest first,
//               so the actual failing opcode/operand shapes are visible.
//   --save      write the totals to json.
//   --baseline  compare against a previously saved json and print the delta per category, which is
//               what tells you whether a change was a real improvement or a regression.

var dir = args.FirstOrDefault(a => !a.StartsWith("--"));
if (dir is null || !Directory.Exists(dir))
{
    Console.Error.WriteLine("usage: Cpp2IL.BodyScan <dllDir> [--detail <category>] [--save <f.json>] [--baseline <f.json>]");
    return 1;
}

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var detailCategory = Option("--detail");
var savePath = Option("--save");
var baselinePath = Option("--baseline");

var dlls = Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories);
long totalMethods = 0, incompleteMethods = 0, totalMarkers = 0;
var byCategory = new Dictionary<string, long>();
var byShape = new Dictionary<string, long>();
var detailMessages = new Dictionary<string, long>();
var perDll = new List<(string Name, long Methods, long Incomplete, long Markers)>();
var typedFieldOffsets = new Dictionary<long, long>();   // failing offset (mod grouping) -> count, for typed bases
var markerCountBuckets = new Dictionary<string, long>();  // "1", "2-5", ... -> methods
long framesTotal = 0, stackWarnTotal = 0, frameAndStackWarn = 0;
var worstMethods = new List<(string Method, long Markers)>();
var singleMarkerCategory = new Dictionary<string, long>();   // category of the ONLY marker in a 1-marker method
var fieldOffsetArrayBase = new Dictionary<long, long>();      // offset -> count, base is an array
var fieldOffsetObjectBase = new Dictionary<long, long>();     // offset -> count, base is a non-array object

foreach (var path in dlls)
{
    ModuleDefinition module;
    try { module = ModuleDefinition.FromFile(path); }
    catch { continue; }

    long dllMethods = 0, dllIncomplete = 0, dllMarkers = 0;

    foreach (var type in module.GetAllTypes())
    foreach (var method in type.Methods)
    {
        if (method.CilMethodBody is not { } body)
            continue;

        totalMethods++;
        dllMethods++;

        var instructions = body.Instructions;
        long markersHere = 0;
        string soleCategory = "";
        var hasStackWarning = false;
        var hasFrameMarker = false;

        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            if (instruction.OpCode.Code is not (CilCode.Call or CilCode.Callvirt))
                continue;

            var callee = (instruction.Operand as IMethodDescriptor)?.Name?.Value;
            if (callee is not ("NoteDecompilerIssue" or "WriteLine"))
                continue;

            // The message is the ldstr just before the call.
            var message = "";
            for (var j = i - 1; j >= 0 && j >= i - 3; j--)
            {
                if (instructions[j].OpCode.Code == CilCode.Ldstr && instructions[j].Operand is string s)
                {
                    message = s;
                    break;
                }
            }

            // A real Console.WriteLine from the original code is not a marker.
            if (callee == "WriteLine" && message.Length == 0)
                continue;

            markersHere++;
            var category = Categorise(message);
            byCategory[category] = byCategory.GetValueOrDefault(category) + 1;
            soleCategory = category;

            if (category == "Unmanaged memory load" && AddressShape(message) == "field: TYPED base" && TrailingOffset(message) is { } fo)
            {
                // A base whose printed type ends in [] is an array; anything else is a plain object.
                var typeText = ExtractBaseType(message);
                if (typeText != null && typeText.EndsWith("[]"))
                    fieldOffsetArrayBase[fo] = fieldOffsetArrayBase.GetValueOrDefault(fo) + 1;
                else
                    fieldOffsetObjectBase[fo] = fieldOffsetObjectBase.GetValueOrDefault(fo) + 1;
            }

            if (category == "Unmanaged memory load")
            {
                var shape = AddressShape(message);
                byShape[shape] = byShape.GetValueOrDefault(shape) + 1;

                if (shape == "field: TYPED base" && TrailingOffset(message) is { } off)
                    typedFieldOffsets[off] = typedFieldOffsets.GetValueOrDefault(off) + 1;
                if (shape == "frame/stack-register base")
                    hasFrameMarker = true;
            }
            if (message.StartsWith("Warning: Method ends with non empty stack"))
                hasStackWarning = true;

            if (detailCategory != null && category.Equals(detailCategory, StringComparison.OrdinalIgnoreCase))
            {
                var normalised = Normalise(message);
                detailMessages[normalised] = detailMessages.GetValueOrDefault(normalised) + 1;
            }
        }

        if (markersHere > 0)
        {
            incompleteMethods++;
            dllIncomplete++;
            var bucket = markersHere == 1 ? "1" : markersHere <= 5 ? "2-5" : markersHere <= 20 ? "6-20" : "20+";
            markerCountBuckets[bucket] = markerCountBuckets.GetValueOrDefault(bucket) + 1;
            if (markersHere >= 15)
                worstMethods.Add(($"{type.Name}::{method.Name}", markersHere));
            if (markersHere == 1)
                singleMarkerCategory[soleCategory] = singleMarkerCategory.GetValueOrDefault(soleCategory) + 1;
        }

        if (hasFrameMarker) framesTotal++;
        if (hasStackWarning) stackWarnTotal++;
        if (hasFrameMarker && hasStackWarning) frameAndStackWarn++;
        totalMarkers += markersHere;
        dllMarkers += markersHere;
    }

    perDll.Add((Path.GetFileName(path), dllMethods, dllIncomplete, dllMarkers));
}

if (totalMethods == 0)
{
    Console.Error.WriteLine($"No method bodies found under {dir}");
    return 1;
}

var clean = totalMethods - incompleteMethods;

Console.WriteLine("================ BODY COMPLETENESS ================");
Console.WriteLine($"DLLs scanned     : {dlls.Length}");
Console.WriteLine($"Methods w/ body  : {totalMethods:N0}");
Console.WriteLine($"  clean (0 mark) : {clean:N0}  ({100.0 * clean / totalMethods:F2}%)");
Console.WriteLine($"  incomplete     : {incompleteMethods:N0}  ({100.0 * incompleteMethods / totalMethods:F2}%)");
Console.WriteLine($"Total markers    : {totalMarkers:N0}");

Console.WriteLine();
Console.WriteLine("---- markers by category ----");
foreach (var kv in byCategory.OrderByDescending(k => k.Value))
    Console.WriteLine($"  {kv.Key,-26} {kv.Value,9:N0}  ({100.0 * kv.Value / totalMarkers:F1}%)");

if (byShape.Count > 0)
{
    var unmanaged = Math.Max(byCategory.GetValueOrDefault("Unmanaged memory load"), 1);
    Console.WriteLine();
    Console.WriteLine("---- 'Unmanaged memory load' by address shape ----");
    foreach (var kv in byShape.OrderByDescending(k => k.Value))
        Console.WriteLine($"  {kv.Key,-32} {kv.Value,9:N0}  ({100.0 * kv.Value / unmanaged:F1}%)");
}

Console.WriteLine();
Console.WriteLine("---- top 10 DLLs by markers ----");
foreach (var d in perDll.OrderByDescending(d => d.Markers).Take(10))
    Console.WriteLine($"  {d.Name,-42} methods={d.Methods,7:N0} incomplete={d.Incomplete,7:N0} markers={d.Markers,8:N0}");

if (detailCategory != null)
{
    Console.WriteLine();
    Console.WriteLine($"---- distinct messages for '{detailCategory}' (top 25) ----");
    foreach (var kv in detailMessages.OrderByDescending(k => k.Value).Take(25))
        Console.WriteLine($"  {kv.Value,8:N0}  {kv.Key}");
}

Console.WriteLine();
Console.WriteLine("---- markers per incomplete method ----");
foreach (var kv in markerCountBuckets.OrderBy(k => k.Key switch { "1" => 0, "2-5" => 1, "6-20" => 2, _ => 3 }))
    Console.WriteLine($"  {kv.Key,-6} methods: {kv.Value,8:N0}");

Console.WriteLine();
Console.WriteLine("---- OVERLAP: frame/stack markers vs stack-imbalance warning (per method) ----");
Console.WriteLine($"  methods with a frame/stack unmanaged marker : {framesTotal:N0}");
Console.WriteLine($"  methods with 'ends non-empty stack' warning : {stackWarnTotal:N0}");
Console.WriteLine($"  methods with BOTH                           : {frameAndStackWarn:N0}  ({(framesTotal>0?100.0*frameAndStackWarn/framesTotal:0):F1}% of frame-marker methods)");
Console.WriteLine();
Console.WriteLine("---- top 20 methods by marker count ----");
foreach (var m in worstMethods.OrderByDescending(m => m.Markers).Take(20))
    Console.WriteLine($"  {m.Markers,6:N0}  {m.Method}");

Console.WriteLine();
Console.WriteLine("---- category of methods with EXACTLY ONE marker (8.4k methods, one fix each) ----");
var oneTotal = Math.Max(singleMarkerCategory.Values.Sum(), 1);
foreach (var kv in singleMarkerCategory.OrderByDescending(k => k.Value))
    Console.WriteLine($"  {kv.Key,-26} {kv.Value,8:N0}  ({100.0 * kv.Value / oneTotal:F1}%)");

if (fieldOffsetArrayBase.Count + fieldOffsetObjectBase.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("---- TYPED-base field offsets split by base kind (top 12 each) ----");
    Console.WriteLine("  [ARRAY base]");
    foreach (var kv in fieldOffsetArrayBase.OrderByDescending(k => k.Value).Take(12))
        Console.WriteLine($"    0x{kv.Key:X4}  {kv.Value,8:N0}");
    Console.WriteLine("  [OBJECT base]");
    foreach (var kv in fieldOffsetObjectBase.OrderByDescending(k => k.Value).Take(12))
        Console.WriteLine($"    0x{kv.Key:X4}  {kv.Value,8:N0}");
}

if (typedFieldOffsets.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("---- TYPED-base field failure: top 25 offsets (hex) ----");
    var total = typedFieldOffsets.Values.Sum();
    foreach (var kv in typedFieldOffsets.OrderByDescending(k => k.Value).Take(25))
        Console.WriteLine($"  0x{kv.Key:X4}  {kv.Value,8:N0}  ({100.0 * kv.Value / total:F1}%)");
}

var snapshot = new Snapshot(totalMethods, clean, incompleteMethods, totalMarkers, byCategory, byShape);

if (baselinePath != null && File.Exists(baselinePath))
{
    var previous = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(baselinePath));
    if (previous != null)
    {
        Console.WriteLine();
        Console.WriteLine($"---- vs baseline {Path.GetFileName(baselinePath)} ----");
        Console.WriteLine($"  clean bodies : {previous.Clean:N0} -> {clean:N0}   ({Delta(clean - previous.Clean)})");
        Console.WriteLine($"  markers      : {previous.Markers:N0} -> {totalMarkers:N0}   ({Delta(totalMarkers - previous.Markers, lowerIsBetter: true)})");

        var keys = previous.ByCategory.Keys.Union(byCategory.Keys).OrderByDescending(k => byCategory.GetValueOrDefault(k));
        foreach (var key in keys)
        {
            var before = previous.ByCategory.GetValueOrDefault(key);
            var after = byCategory.GetValueOrDefault(key);
            if (before != after)
                Console.WriteLine($"    {key,-26} {before,9:N0} -> {after,9:N0}   ({Delta(after - before, lowerIsBetter: true)})");
        }
    }
}

if (savePath != null)
{
    File.WriteAllText(savePath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine();
    Console.WriteLine($"saved snapshot -> {savePath}");
}

return 0;

static string Delta(long value, bool lowerIsBetter = false)
{
    if (value == 0)
        return "no change";

    var better = lowerIsBetter ? value < 0 : value > 0;
    return $"{(value > 0 ? "+" : "")}{value:N0} {(better ? "BETTER" : "WORSE")}";
}

// The marker text always starts with the fixed prefix IlGenerator writes, so a prefix match is enough.
static string Categorise(string message) => message switch
{
    _ when message.StartsWith("Unmanaged memory load") => "Unmanaged memory load",
    _ when message.StartsWith("Method not found") => "Method not found",
    _ when message.StartsWith("Indirect call") => "Indirect call",
    _ when message.StartsWith("Indirect jump") => "Indirect jump",
    _ when message.StartsWith("Not implemented instruction") => "Not implemented",
    _ when message.StartsWith("Invalid instruction") => "Invalid instruction",
    _ when message.StartsWith("Unknown call target") => "Unknown call target",
    _ when message.StartsWith("Non static method called") => "Missing 'this'",
    _ when message.StartsWith("Stack shift") => "Stack shift",
    _ when message.StartsWith("Store into unknown operand") => "Store unknown operand",
    _ when message.StartsWith("Unknown operand") => "Unknown operand",
    _ when message.StartsWith("Unknown instruction") => "Unknown instruction",
    _ when message.StartsWith("Phi opcodes") => "Phi leak",
    _ when message.StartsWith("Warning:") => "Analysis warning",
    _ => "Other",
};

// Splits the biggest category by what the address looks like, which is what decides how it could be
// recovered: an element access, a field read off a typed or untyped base, a frame-register base.
static string AddressShape(string message)
{
    var colon = message.IndexOf(": ", StringComparison.Ordinal);
    var operand = colon >= 0 ? message[(colon + 2)..] : message;

    if (operand.Contains('*'))
        return "indexed (array-like)";

    if (operand.Contains("rsp") || operand.Contains("rbp") || operand.Contains("esp") || operand.Contains("ebp") || operand.Contains("stack["))
        return "frame/stack-register base";

    if (operand.Contains('+') || operand.Contains('-'))
        // A resolved base prints its type in parentheses; without one, type propagation never got there.
        return operand.Contains('(') ? "field: TYPED base" : "field: UNTYPED base";

    return "other / bare base";
}

// Pulls "Some.Type" out of the FIRST parenthesised type in the operand (the base's type).
static string? ExtractBaseType(string message)
{
    var open = message.IndexOf('(');
    if (open < 0) return null;
    var close = message.IndexOf(')', open + 1);
    return close < 0 ? null : message[(open + 1)..close];
}

// Pulls the constant field offset out of a "[base (Type)+HEX]" operand, i.e. the last +HEX that is
// not followed by an index term - the offset ResolveFieldOffsets tried and missed.
static long? TrailingOffset(string message)
{
    var close = message.LastIndexOf(']');
    if (close < 0) return null;
    var inner = message[..close];
    // ignore indexed forms; those are element access, not a field offset
    if (inner.Contains('*')) return null;
    var plus = inner.LastIndexOf('+');
    var minus = inner.LastIndexOf('-');
    var at = Math.Max(plus, minus);
    if (at < 0) return null;
    var hex = inner[(at + 1)..].Trim();
    if (hex.Length == 0 || hex.Length > 8) return null;
    if (!long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var value)) return null;
    return at == minus ? -value : value;
}

// Strips addresses and SSA version numbers so the same failing shape groups onto one line.
static string Normalise(string message)
{
    var text = Regex.Replace(message, @"0x[0-9A-Fa-f]+|@[0-9A-Fa-f]+|\bv\d+\b|_v\d+|\b[0-9A-F]{4,}\b", "#");
    return text.Length > 110 ? text[..110] : text;
}

internal sealed record Snapshot(
    long Methods,
    long Clean,
    long Incomplete,
    long Markers,
    Dictionary<string, long> ByCategory,
    Dictionary<string, long> ByShape);
