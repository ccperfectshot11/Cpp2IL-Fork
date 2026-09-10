using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Cpp2IL.VerifyCore;

// Hand-rolled writer instead of System.Text.Json, for the same reason the project has no package
// references at all: this has to build for a MelonLoader mod, and the emitted shape is a dozen scalar
// fields per method. The format is the contract between the two phases, so it lives here rather than in
// either host.
public static class SignatureJson
{
    public const string PhaseRecovered = "1-recovered";
    public const string PhaseNative = "2-native";

    public static void Write(TextWriter writer, string phase, string source, FuzzPlan plan, IList<MethodFuzzResult> results)
    {
        writer.WriteLine("{");
        writer.WriteLine("  \"tool\": \"Cpp2IL.VerifyCheck\",");
        writer.WriteLine("  \"phase\": " + Quote(phase) + ",");
        writer.WriteLine("  \"generatedUtc\": " + Quote(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)) + ",");
        writer.WriteLine("  \"source\": " + Quote(source) + ",");
        writer.WriteLine("  \"plan\": { \"seed\": " + plan.Seed + ", \"randomIterations\": " + plan.RandomIterations + ", \"maxEdgeIterations\": " + plan.MaxEdgeIterations + " },");
        writer.WriteLine("  \"methods\": [");

        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var builder = new StringBuilder("    {");
            builder.Append("\"key\": ").Append(Quote(r.Key));
            builder.Append(", \"assembly\": ").Append(Quote(r.Assembly));
            builder.Append(", \"type\": ").Append(Quote(r.Type));
            builder.Append(", \"method\": ").Append(Quote(r.Method));
            builder.Append(", \"token\": ").Append(Quote(r.Token));
            builder.Append(", \"supported\": ").Append(r.Supported ? "true" : "false");
            builder.Append(", \"signature\": ").Append(Quote(r.Signature));
            builder.Append(", \"secondPassSignature\": ").Append(Quote(r.SecondPassSignature));
            builder.Append(", \"nonDeterministic\": ").Append(r.NonDeterministic ? "true" : "false");
            builder.Append(", \"edgeIterations\": ").Append(r.EdgeIterations.ToString(CultureInfo.InvariantCulture));
            builder.Append(", \"randomIterations\": ").Append(r.RandomIterations.ToString(CultureInfo.InvariantCulture));
            builder.Append(", \"threw\": ").Append(r.ThrewCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(", \"allThrew\": ").Append(r.AllThrew ? "true" : "false");
            builder.Append(", \"abortedAfter\": ").Append(r.AbortedAfter.ToString(CultureInfo.InvariantCulture));
            builder.Append(", \"constantOutput\": ").Append(r.ConstantOutput ? "true" : "false");
            builder.Append(", \"instance\": ").Append(r.IsInstance ? "true" : "false");
            builder.Append(", \"receiverType\": ").Append(Quote(r.ReceiverType));
            builder.Append(", \"mutatesReceiver\": ").Append(r.MutatesReceiver ? "true" : "false");
            builder.Append(", \"mutatedCount\": ").Append(r.MutatedCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(", \"noObservableOutput\": ").Append(r.NoObservableOutput ? "true" : "false");
            builder.Append(", \"readsStatics\": ").Append(r.ReadsStatics ? "true" : "false");
            builder.Append(", \"elapsedMs\": ").Append(r.ElapsedMs.ToString(CultureInfo.InvariantCulture));
            builder.Append(", \"failure\": ").Append(Quote(r.Failure));
            builder.Append(", \"exceptionKinds\": [");
            for (var e = 0; e < r.ExceptionKinds.Count; e++)
            {
                if (e > 0)
                    builder.Append(", ");

                builder.Append(Quote(r.ExceptionKinds[e]));
            }

            builder.Append("]}");
            if (i < results.Count - 1)
                builder.Append(',');

            writer.WriteLine(builder.ToString());
        }

        writer.WriteLine("  ]");
        writer.WriteLine("}");
    }

    // A single result on one line, for the partial-results file a run appends to as it goes. Deliberately
    // not the document format above: that one is a whole JSON object and cannot be appended to, and this
    // one has to survive the process being killed mid-write, so each line stands alone. Tab-separated
    // rather than JSON because the reader is this file too, and a field split is enough.
    public static string WriteResultLine(MethodFuzzResult result)
    {
        var fields = new[]
        {
            result.Key, result.Assembly, result.Type, result.Method, result.Token,
            result.Signature, result.SecondPassSignature, result.Failure,
            result.Supported ? "1" : "0",
            result.ReadsStatics ? "1" : "0",
            result.NonDeterministic ? "1" : "0",
            result.AllThrew ? "1" : "0",
            result.ConstantOutput ? "1" : "0",
            result.EdgeIterations.ToString(CultureInfo.InvariantCulture),
            result.RandomIterations.ToString(CultureInfo.InvariantCulture),
            result.ThrewCount.ToString(CultureInfo.InvariantCulture),
            result.AbortedAfter.ToString(CultureInfo.InvariantCulture),
            result.ElapsedMs.ToString(CultureInfo.InvariantCulture),
            string.Join("|", result.ExceptionKinds ?? new List<string>()),

            // Tier 2. Left out of the first version of this format, which meant a resumed run reported
            // every instance method as a static one - 658 of them present in the file and zero in the
            // summary. Anything a result carries has to be here, or resuming quietly changes the answer.
            result.ReceiverType,
            result.IsInstance ? "1" : "0",
            result.MutatesReceiver ? "1" : "0",
            result.NoObservableOutput ? "1" : "0",
            result.MutatedCount.ToString(CultureInfo.InvariantCulture),
        };

        var line = new StringBuilder();

        foreach (var field in fields)
        {
            if (line.Length > 0)
                line.Append('\u001f');

            // A failure message can contain anything, newlines included, and one of those would split the
            // line in two and make every field after it land in the wrong column on resume.
            line.Append((field ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\u001f", " "));
        }

        return line.ToString();
    }

    public static MethodFuzzResult ReadResultLine(string line)
    {
        var fields = line.Split('\u001f');

        // An older partial file has fewer columns; the Tier 2 ones below are read only when present, so a
        // half-finished run from a previous build resumes rather than being thrown away.
        if (fields.Length < 19)
            return null;

        return new MethodFuzzResult
        {
            Key = fields[0], Assembly = fields[1], Type = fields[2], Method = fields[3], Token = fields[4],
            Signature = Blank(fields[5]), SecondPassSignature = Blank(fields[6]), Failure = Blank(fields[7]),
            Supported = fields[8] == "1",
            ReadsStatics = fields[9] == "1",
            NonDeterministic = fields[10] == "1",
            AllThrew = fields[11] == "1",
            ConstantOutput = fields[12] == "1",
            EdgeIterations = int.Parse(fields[13], CultureInfo.InvariantCulture),
            RandomIterations = int.Parse(fields[14], CultureInfo.InvariantCulture),
            ThrewCount = int.Parse(fields[15], CultureInfo.InvariantCulture),
            AbortedAfter = int.Parse(fields[16], CultureInfo.InvariantCulture),
            ElapsedMs = long.Parse(fields[17], CultureInfo.InvariantCulture),
            ExceptionKinds = fields[18].Length == 0
                ? new List<string>()
                : new List<string>(fields[18].Split('|')),
            ReceiverType = fields.Length > 19 ? Blank(fields[19]) : null,
            IsInstance = fields.Length > 20 && fields[20] == "1",
            MutatesReceiver = fields.Length > 21 && fields[21] == "1",
            NoObservableOutput = fields.Length > 22 && fields[22] == "1",
            MutatedCount = fields.Length > 23 ? int.Parse(fields[23], CultureInfo.InvariantCulture) : 0,
        };
    }

    // An empty column and a null field are the same thing here; keeping them distinct would only make the
    // resumed result differ from the original for no observable reason.
    private static string Blank(string value) => value.Length == 0 ? null : value;
    private static string Quote(string value)
    {
        if (value == null)
            return "null";

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20 || c > 0x7E)
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(c);

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
