using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Cpp2IL.VerifyCore;

/// <summary>
/// Forma fisierelor prin care trec cele trei bucati ale uneltei, tinuta INTR-UN SINGUR LOC.
///
/// Sunt trei programe care nu ruleaza niciodata deodata - dump-ul din joc scrie indexul, planificatorul de
/// pe disc il citeste si scrie lista de lucru, harnasul din joc citeste lista de lucru - deci ordinea
/// coloanelor este un contract intre trei bucati de cod. Tinuta intr-un singur loc, o coloana mutata se
/// muta peste tot deodata; tinuta in trei, s-ar desincroniza tacut si harnasul ar citi calitatea din
/// coloana argumentelor.
///
/// Fiecare fisier poarta in antet un semn de forma. Un fisier scris de o versiune mai veche NU se citeste
/// pe tacute: se spune limpede ca trebuie refacut. Un dump vechi langa o normalizare noua este cel mai
/// scump fel de a pierde o sesiune de joc - fiecare cheie s-ar raporta "nu exista in index" si jocul ar fi
/// pornit degeaba.
/// </summary>
public static class PlanFiles
{
    public const char Sep = '\u001f';

    /// <summary>
    /// Se schimba odata cu ORICE coloana noua si cu orice regula de normalizare a cheilor. Cuprinde
    /// dinadins si versiunea cheii: doua fisiere cu aceleasi coloane dar cu chei normalizate altfel nu se
    /// pot folosi impreuna.
    /// </summary>
    public static string Schema => "w29-1/" + MethodKeys.FormatVersion;

    public const string GameIndexFile = "verify-game-index.tsv";
    public const string GameFieldsFile = "verify-game-fields.tsv";
    public const string WorklistFile = "verify-worklist.tsv";
    public const string BlockedFile = "verify-blocked.tsv";
    public const string SkipFile = "verify-skip.txt";
    public const string ResultsFile = "verify-results.tsv";
    public const string JournalFile = "verify-inflight.txt";
    public const string SummaryFile = "verify-summary.txt";

    // ------------------------------------------------------------------------------------------------
    // verify-game-index.tsv - scris IN JOC, o singura data, fara sa se cheme nimic
    // ------------------------------------------------------------------------------------------------

    public static readonly string[] GameIndexColumns =
    {
        "key", "assembly", "type", "method", "static", "receiver_type", "parameters", "return_type", "flags",
    };

    public const int GameKey = 0;
    public const int GameAssembly = 1;
    public const int GameType = 2;
    public const int GameMethod = 3;
    public const int GameStatic = 4;
    public const int GameReceiverType = 5;
    public const int GameParameters = 6;
    public const int GameReturnType = 7;
    public const int GameFlags = 8;

    // ------------------------------------------------------------------------------------------------
    // verify-game-fields.tsv - campurile de instanta ale tipurilor jocului
    //
    // Exista pentru un singur lucru, dar acela deschide cea mai mare parte din univers: semanarea
    // receptorului. Masurat pe dump-ul real, 17.555 din cele 33.365 de metode chemabile intorc void si
    // 27.160 sunt metode de instanta - deci pentru majoritate singurul lucru care se poate privi dupa apel
    // sunt campurile receptorului. Ca sa se poata planifica PE DISC ce campuri se seamana, trebuie stiut
    // aici ce campuri are partea jocului; altfel planul ar fi facut numai dupa partea recuperata si
    // nepotrivirile s-ar descoperi abia in joc, una cate una.
    // ------------------------------------------------------------------------------------------------

    public static readonly string[] GameFieldColumns = { "type", "field", "field_type", "leaf_kind" };

    public const int FieldType = 0;
    public const int FieldName = 1;
    public const int FieldTypeName = 2;
    public const int FieldLeafKind = 3;

    // ------------------------------------------------------------------------------------------------
    // verify-worklist.tsv - scris PE DISC, citit in joc. Aici nu se mai hotaraste nimic.
    // ------------------------------------------------------------------------------------------------

    public static readonly string[] WorklistColumns =
    {
        "key", "assembly", "receiver", "receiver_seed", "arguments", "return_plan", "quality", "weight",
    };

    public const int WorkKey = 0;
    public const int WorkAssembly = 1;

    /// <summary>"static", "blank" sau "seeded".</summary>
    public const int WorkReceiver = 2;

    /// <summary>Perechi camp=reteta, despartite prin 0x1E. Gol cand receptorul este static sau gol.</summary>
    public const int WorkReceiverSeed = 3;

    /// <summary>Cate o reteta pe argument, despartite prin 0x1E.</summary>
    public const int WorkArguments = 4;

    /// <summary>Cum se compara valoarea intoarsa: bits / text / shape / array / nullness / void.</summary>
    public const int WorkReturnPlan = 5;

    /// <summary>Eticheta de calitate, vezi <see cref="Quality"/>.</summary>
    public const int WorkQuality = 6;

    /// <summary>Suma greutatilor retetelor. Serveste la sortare: intai ce se poate masura cel mai bine.</summary>
    public const int WorkWeight = 7;

    // ------------------------------------------------------------------------------------------------
    // verify-blocked.tsv - verdictele date pe disc, fara joc
    // ------------------------------------------------------------------------------------------------

    public static readonly string[] BlockedColumns = { "key", "assembly", "reason", "detail" };

    // ------------------------------------------------------------------------------------------------
    // verify-results.tsv - scris in joc, pe masura ce se masoara
    // ------------------------------------------------------------------------------------------------

    public static readonly string[] ResultColumns =
    {
        "key", "assembly", "verdict", "observed", "quality", "game", "ours", "note",
    };

    public const int ResultKey = 0;
    public const int ResultAssembly = 1;
    public const int ResultVerdict = 2;
    public const int ResultObserved = 3;
    public const int ResultQuality = 4;
    public const int ResultGame = 5;
    public const int ResultOurs = 6;
    public const int ResultNote = 7;

    // ------------------------------------------------------------------------------------------------

    public static string Header(string[] columns) => "#" + Schema + Sep + string.Join(Sep.ToString(), columns);

    /// <summary>
    /// Adevarat daca antetul este scris de ACEEASI forma. Se cere la fiecare citire, nu din prudenta
    /// generala: cheile din fisier sunt tot ce leaga cele trei bucati, iar un fisier vechi citit pe tacute
    /// raporteaza "nimic nu se potriveste" si arata exact ca o unealta stricata.
    /// </summary>
    public static bool HeaderMatches(string line) =>
        line != null && line.StartsWith("#" + Schema + Sep, StringComparison.Ordinal);

    public static string Row(params string[] values)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
                builder.Append(Sep);

            builder.Append(Clean(values[i]));
        }

        return builder.ToString();
    }

    public static string[] Fields(string line) => line.Split(Sep);

    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        // 0x1E NU se curata: este separatorul dinauntrul coloanelor de retete, si acolo are voie sa fie.
        // Sirurile care ar putea sa il contina trec prin Recipe.Escape inainte sa ajunga aici.
        return text.Replace(Sep, ' ').Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }

    public static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Cat valoreaza argumentele cu care s-a chemat o metoda. Se scrie langa fiecare rezultat, fiindca fara ea
/// raportul ar fi o minciuna prin omisiune: o metoda de instanta chemata cu receptor gol si argumente nule
/// care raspunde la fel pe ambele parti NU se aduna cu una chemata cu siruri si numere adevarate - prima
/// poate sa fi iesit pe prima ramura, aceeasi pe ambele parti, fara sa atinga nimic din ce voiam masurat.
/// </summary>
public static class Quality
{
    /// <summary>Fiecare argument si receptorul poarta valori adevarate, identice prin constructie.</summary>
    public const string Full = "full";

    /// <summary>Argumentele sunt adevarate, dar receptorul este gol - toate campurile pe zero.</summary>
    public const string BlankReceiver = "blank-receiver";

    /// <summary>Cel putin un argument a cazut pe null sau pe zero fiindca nu se poate fabrica identic.</summary>
    public const string Partial = "partial";

    /// <summary>Toate argumentele de referinta sunt nule. Cel mai slab fel de a chema o metoda.</summary>
    public const string AllNull = "all-null";

    /// <summary>Metoda nu ia argumente. Nu este nici bine nici rau - este doar de stiut la citirea raportului.</summary>
    public const string NoArguments = "no-arguments";

    /// <summary>
    /// Eticheta finala a unei chemari: cea mai slaba dintre cele care i se potrivesc, plus starea
    /// receptorului. Se cladeste aici si nu la fiecare loc de folosire, ca doua etichete sa nu insemne
    /// acelasi lucru sub nume diferite.
    /// </summary>
    public static string Of(bool isStatic, bool receiverSeeded, IList<Recipe> arguments)
    {
        var head = isStatic ? "" : (receiverSeeded ? "seeded-receiver" : BlankReceiver);

        string body;
        if (arguments == null || arguments.Count == 0)
        {
            body = NoArguments;
        }
        else
        {
            var nulls = 0;
            var weak = 0;

            foreach (var recipe in arguments)
            {
                if (recipe.Kind == RecipeKind.Null)
                    nulls++;

                if (recipe.Weight < 2)
                    weak++;
            }

            if (nulls == arguments.Count)
                body = AllNull;
            else if (weak > 0)
                body = Partial;
            else
                body = Full;
        }

        return head.Length == 0 ? body : head + "|" + body;
    }
}
