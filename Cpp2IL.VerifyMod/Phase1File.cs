using System;
using System.Collections.Generic;
using Cpp2IL.VerifyCore;

namespace Cpp2IL.VerifyMod;

/// <summary>
/// Reads back what Phase 1 wrote: the plan, and the keys of the methods worth comparing.
///
/// Hand-rolled for the same reason <see cref="SignatureJson"/> is written by hand - this has to run
/// inside MelonLoader's runtime with no package references. It only reads the handful of scalar fields
/// the comparison needs, and it reads them by scanning for the field name rather than by parsing JSON
/// properly, which is enough for a document this code also wrote.
/// </summary>
internal static class Phase1File
{
    internal sealed class Request
    {
        internal FuzzPlan Plan = new FuzzPlan();
        internal List<string> Keys = new List<string>();
    }

    internal static Request Read(string json)
    {
        var request = new Request
        {
            Plan = new FuzzPlan
            {
                Seed = (ulong)ReadNumber(json, "\"seed\"", (long)new FuzzPlan().Seed),
                RandomIterations = (int)ReadNumber(json, "\"randomIterations\"", new FuzzPlan().RandomIterations),
                MaxEdgeIterations = (int)ReadNumber(json, "\"maxEdgeIterations\"", new FuzzPlan().MaxEdgeIterations),
            },
        };

        foreach (var entry in SplitEntries(json))
        {
            if (ReadString(entry, "\"key\"") is not { } key || key.Length == 0)
                continue;

            // Skipped exactly as the README specifies. An entry Phase 1 could not measure has nothing to
            // compare against, and one that is non-deterministic or was cut short would report a
            // difference that says something about the harness rather than about the game.
            if (!ReadBool(entry, "\"supported\"") || ReadBool(entry, "\"nonDeterministic\"")
                || ReadBool(entry, "\"noObservableOutput\"") || ReadNumber(entry, "\"abortedAfter\"", 0) > 0)
                continue;

            request.Keys.Add(key);
        }

        return request;
    }

    // Entries are separated by the object boundary inside the "methods" array. Splitting on "},{" after
    // whitespace removal would break on any nested object, but a result has none - every field is a
    // scalar or a flat array of strings.
    private static IEnumerable<string> SplitEntries(string json)
    {
        var methods = json.IndexOf("\"methods\"", StringComparison.Ordinal);
        if (methods < 0)
            yield break;

        var depth = 0;
        var start = -1;

        for (var i = methods; i < json.Length; i++)
        {
            var c = json[i];

            if (c == '{')
            {
                if (depth == 0)
                    start = i;

                depth++;
            }
            else if (c == '}')
            {
                depth--;

                if (depth == 0 && start >= 0)
                {
                    yield return json.Substring(start, i - start + 1);
                    start = -1;
                }
            }
            else if (c == ']' && depth == 0)
            {
                yield break;
            }
        }
    }

    private static string ReadString(string source, string field)
    {
        var at = ValueStart(source, field);
        if (at < 0 || source[at] != '"')
            return null;

        var value = new System.Text.StringBuilder();

        for (var i = at + 1; i < source.Length; i++)
        {
            if (source[i] == '\\' && i + 1 < source.Length)
            {
                // Only the escapes SignatureJson emits: it quotes with the same small set.
                value.Append(source[++i] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', var other => other });
                continue;
            }

            if (source[i] == '"')
                return value.ToString();

            value.Append(source[i]);
        }

        return null;
    }

    private static bool ReadBool(string source, string field)
    {
        var at = ValueStart(source, field);
        return at >= 0 && string.CompareOrdinal(source, at, "true", 0, 4) == 0;
    }

    private static long ReadNumber(string source, string field, long fallback)
    {
        var at = ValueStart(source, field);
        if (at < 0)
            return fallback;

        var end = at;
        while (end < source.Length && (char.IsDigit(source[end]) || source[end] == '-'))
            end++;

        return end > at && long.TryParse(source.Substring(at, end - at), out var value) ? value : fallback;
    }

    private static int ValueStart(string source, string field)
    {
        var at = source.IndexOf(field, StringComparison.Ordinal);
        if (at < 0)
            return -1;

        at = source.IndexOf(':', at + field.Length);
        if (at < 0)
            return -1;

        at++;
        while (at < source.Length && char.IsWhiteSpace(source[at]))
            at++;

        return at < source.Length ? at : -1;
    }
}
