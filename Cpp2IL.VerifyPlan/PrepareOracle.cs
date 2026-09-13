using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cpp2IL.VerifyCore;

namespace Cpp2IL.VerifyPlan;

/// <summary>
/// Intrebarea "trece corpul asta de JIT?", pusa PE DISC.
///
/// De ce merita o unealta intreaga. Masurat pe cele 2.574 de rezultate stranse pana acum, 1.931 - adica
/// 75% - nu au ajuns niciodata la un apel: JIT-ul a refuzat corpul recuperat inainte de orice. Fiecare
/// dintre acele 1.931 de randuri a costat timp de sesiune de joc, iar unele au costat o repornire intreaga,
/// fiindca un corp destul de stricat nu da exceptie, ci ia procesul cu el.
///
/// Raspunsul insa nu atarna DELOC de joc. Atarna numai de corpul recuperat si de regulile dupa care se
/// leaga referintele lui - amandoua statice, amandoua pe disc. Deci intrebarea se pune aici, unde o moarte
/// de proces costa o secunda si o repornire, nu patruzeci de secunde de incarcat joc.
///
/// Si mai important decat economia: aici raspunsul se poate DESPARTI in doua, ceea ce in joc nu s-a facut
/// niciodata. "Refuzat de JIT" aduna azi doua lucruri care nu au nimic in comun - un corp care cere din
/// mscorlib membri pe care runtime-ul gazda nu ii are (aproximativ 900 din cele 1.931, si nu este vina lui
/// Cpp2IL) si un corp care este el insusi invalid (aproximativ 727, si aia chiar este vina lui Cpp2IL).
/// Un singur numar mare le ascundea pe amandoua.
/// </summary>
internal static class PrepareOracle
{
    public const string StateFile = "verify-prepare.tsv";
    public const string JournalFile = "verify-prepare-inflight.txt";

    public sealed class Outcome
    {
        public string Reason;      // "" cand a trecut; altfel una din Blocked.*
        public string Detail;      // exceptia, litera cu litera - ca impartirea sa se poata verifica din fisier
    }

    /// <summary>
    /// Trece prin candidati si cere JIT-ului sa compileze fiecare corp recuperat.
    ///
    /// Jurnalul se scrie INAINTE de fiecare incercare si se goleste dupa. Nu este prudenta, este singurul
    /// mecanism care merge: un corp care omoara procesul nu mai apuca sa scrie nimic despre sine, deci
    /// numele lui trebuie sa fie deja pe disc. Rularea urmatoare il gaseste acolo, il trece drept
    /// "killed-on-prepare" si merge mai departe de la el.
    ///
    /// Acelasi mecanism acopera si o cadere care NU este vina unui corp anume. Masina are 16 GB; jocul nu
    /// ruleaza in timpul planificarii, dar assembly-urile recuperate se incarca toate si niciunul nu se
    /// descarca, iar JIT-ul scoate cod nativ pentru zeci de mii de corpuri. Daca procesul moare de memorie,
    /// verify-prepare.tsv tine deja tot ce s-a aflat pana atunci si repornirea continua de acolo. Cand se
    /// intampla des, taie universul in felii cu --only sau --skip in loc sa astepti.
    /// </summary>
    public static Dictionary<string, Outcome> Run(string directory, List<Candidate> candidates, Action<string> log)
    {
        var known = ReadState(directory);
        var journalPath = Path.Combine(directory, JournalFile);

        var crashed = ReadJournal(journalPath);
        if (crashed != null && !known.ContainsKey(crashed))
        {
            known[crashed] = new Outcome { Reason = Blocked.KilledOnPrepare, Detail = "a omorat procesul la compilare" };
            Append(directory, crashed, known[crashed]);
            log("Rularea trecuta a murit compiland " + crashed + " - trecuta ca " + Blocked.KilledOnPrepare + " si sarita de acum.");
        }

        var attempted = 0;
        var refused = 0;

        using (var journal = new FileStream(journalPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        using (var state = new StreamWriter(new FileStream(Path.Combine(directory, StateFile), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)))
        {
            foreach (var candidate in candidates)
            {
                if (known.ContainsKey(candidate.Key))
                    continue;

                WriteJournal(journal, candidate.Key);

                var outcome = Try(candidate.Recovered);
                known[candidate.Key] = outcome;
                state.WriteLine(PlanFiles.Row(candidate.Key, outcome.Reason ?? "", outcome.Detail ?? ""));
                state.Flush();

                attempted++;
                if (outcome.Reason != null && outcome.Reason.Length > 0)
                    refused++;

                if (attempted % 2000 == 0)
                    log("  compilate " + attempted + " (refuzate " + refused + ")...");
            }

            // Golit ANUME la sfarsit: un jurnal ramas plin dupa o trecere linistita ar trece nevinovata
            // ultima metoda incercata drept ucigas la pornirea urmatoare.
            WriteJournal(journal, "");
        }

        try
        {
            File.Delete(journalPath);
        }
        catch (Exception)
        {
            // Un jurnal care nu se sterge inseamna cel mult ca aceeasi metoda se raporteaza inca o data.
        }

        log("Oracolul de compilare: " + attempted + " incercate acum, " + refused + " refuzate, "
            + known.Count + " stiute cu totul.");

        return known;
    }

    private static Outcome Try(MethodBase method)
    {
        try
        {
            RuntimeHelpers.PrepareMethod(method.MethodHandle);
            return new Outcome { Reason = "", Detail = "" };
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            return new Outcome { Reason = Classify(inner), Detail = inner.GetType().Name + ": " + inner.Message };
        }
    }

    /// <summary>
    /// De care parte cade un refuz.
    ///
    /// Regulile sunt scrise pe fata si detaliul brut se pastreaza NEATINS in fisier, ca impartirea sa se
    /// poata verifica si reface cu un grep fara sa mai ruleze nimeni nimic. Asta conteaza: impartirea este
    /// o judecata, si o judecata care nu se poate verifica este o presupunere.
    ///
    /// Tiparele vin din mesajele masurate, nu din inchipuire. Cele care cad la nepotrivire de BCL:
    /// "Could not load type System.ThrowHelper / System.SpanHelpers / System.Number / System.ReadOnlySpan`1
    /// din mscorlib", "Field not found: System.RuntimeTypeHandle.value", "Method not found:
    /// System.Globalization.CompareInfo.CompareOrdinalIgnoreCase". Cele care cad la IL invalid:
    /// "Common Language Runtime detected an invalid program", "The JIT compiler encountered invalid IL
    /// code or an internal limitation", "Bad IL format".
    /// </summary>
    public static string Classify(Exception ex)
    {
        var message = ex.Message ?? "";

        switch (ex)
        {
            case InvalidProgramException:
                return Blocked.InvalidIl;

            // Un membru sau un tip promis de metadate si negasit la rulare. Aproape intotdeauna un amanunt
            // al BCL-ului Mono al jocului care nu exista pe .NET 6 - dar poate fi si un membru al codului
            // recuperat pe care Cpp2IL nu l-a emis, si atunci detaliul din fisier il arata dupa nume.
            case TypeLoadException:
            case MissingFieldException:
            case MissingMethodException:
            case MissingMemberException:
            case FileNotFoundException:
                return Blocked.HostBclMismatch;

            case BadImageFormatException:
                // Doua lucruri foarte diferite poarta acelasi tip de exceptie. "Could not load file or
                // assembly X" este o referinta care nu se rezolva, adica nepotrivire; restul - inclusiv
                // "Bad IL format" si "incorrect format" pe corpul insusi - este IL stricat.
                return message.IndexOf("Could not load file or assembly", StringComparison.Ordinal) >= 0
                    ? Blocked.HostBclMismatch
                    : Blocked.InvalidIl;

            default:
                // PlatformNotSupportedException (metode extern fara implementare), SecurityException
                // ("ECall methods must be packaged into a system module"), InvalidOperationException pe
                // tipuri abstracte. Niciuna nu spune ceva despre calitatea recuperarii si niciuna nu se
                // repara cu alte argumente: corpul pur si simplu nu poate rula in procesul acesta.
                return Blocked.HostRefused;
        }
    }

    private static Dictionary<string, Outcome> ReadState(string directory)
    {
        var known = new Dictionary<string, Outcome>(StringComparer.Ordinal);
        var path = Path.Combine(directory, StateFile);

        if (!File.Exists(path))
            return known;

        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0)
                continue;

            var fields = PlanFiles.Fields(line);
            if (fields.Length < 3)
                continue;

            known[fields[0]] = new Outcome { Reason = fields[1], Detail = fields[2] };
        }

        return known;
    }

    private static void Append(string directory, string key, Outcome outcome)
    {
        File.AppendAllText(Path.Combine(directory, StateFile),
            PlanFiles.Row(key, outcome.Reason ?? "", outcome.Detail ?? "") + Environment.NewLine);
    }

    private static string ReadJournal(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var text = File.ReadAllText(path).Trim('\0', ' ', '\r', '\n');
            return text.Length > 0 ? text : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Scris peste, golit si dat pe disc la fiecare metoda. Scump, si merita: fara asta, o singura moarte de
    // proces obliga la reluarea de la zero, iar cu 33.365 de candidati asta inseamna ca sweep-ul nu se
    // termina niciodata.
    private static void WriteJournal(FileStream journal, string key)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(key + "\n");
        journal.Seek(0, SeekOrigin.Begin);
        journal.SetLength(0);
        journal.Write(bytes, 0, bytes.Length);
        journal.Flush(true);
    }

}
