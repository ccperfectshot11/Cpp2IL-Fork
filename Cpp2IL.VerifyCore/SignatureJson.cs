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
