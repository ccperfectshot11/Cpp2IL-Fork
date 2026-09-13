using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Cpp2IL.VerifyCore;
using MelonLoader;
using MelonLoader.Utils;

[assembly: MelonInfo(typeof(Cpp2IL.VerifyMod.VerifyMod), "Cpp2IL VerifyMod", "2.0", "Cpp2IL")]
[assembly: MelonGame(null, null)]

namespace Cpp2IL.VerifyMod;

/// <summary>
/// Partea din joc a verificarii diferentiale, in doua moduri si nimic altceva.
///
///   CPP2IL_VERIFY_MODE=index  scrie indexul metodelor jocului si iese. Nu cheama nimic, deci nu poate
///                             cadea. Se face O SINGURA DATA si se refoloseste.
///   CPP2IL_VERIFY_MODE=run    citeste lista de lucru facuta pe disc de Cpp2IL.VerifyPlan si o executa.
///
/// De ce doua moduri si nu patru faze. Unealta de dinainte avea patru, si se bateau cap in cap: hash-uri
/// intre procese, carlige Harmony, substitutie si maturare activa. Numai ultima raspundea la intrebarea
/// pusa - "cheama fiecare metoda pe amandoua partile cu aceleasi argumente si compara raspunsurile" - iar
/// celelalte trei se carau dupa ea. Acum exista un singur drum: index pe disc, plan pe disc, executie in
/// joc.
///
/// Si un principiu, care este de fapt intreaga schimbare: tot ce se poate afla pe disc se afla pe disc.
/// Jocul se porneste ca sa EXECUTE o lista gata facuta, nu ca sa descopere ce are de facut. Fiecare
/// necunoscuta lamurita in joc costa o repornire de patruzeci de secunde, iar masuratoarea a fost limpede -
/// unsprezece reporniri au produs treizeci si sapte de metode masurate. Pe un univers de 47.012, aritmetica
/// aceea nu iese niciodata.
/// </summary>
public class VerifyMod : MelonMod
{
    private string _directory;
    private string _mode = "";
    private string _dlls = "";
    private int _frames;
    private bool _started;
    private bool _done;

    public override void OnInitializeMelon()
    {
        _directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        _mode = (Environment.GetEnvironmentVariable("CPP2IL_VERIFY_MODE") ?? "").Trim().ToLowerInvariant();
        _dlls = Environment.GetEnvironmentVariable("CPP2IL_VERIFY_DLLS") ?? "";

        switch (_mode)
        {
            case "index":
                LoggerInstance.Msg("Mod INDEX: se scriu " + PlanFiles.GameIndexFile + " si "
                    + PlanFiles.GameFieldsFile + ". Nu se cheama nicio metoda.");
                return;

            case "run":
                if (_dlls.Length == 0 || !Directory.Exists(_dlls))
                {
                    LoggerInstance.Error("CPP2IL_VERIFY_MODE=run dar CPP2IL_VERIFY_DLLS nu arata catre un director existent.");
                    _mode = "";
                    return;
                }

                LoggerInstance.Msg("Mod RUN: se executa " + PlanFiles.WorklistFile + " peste DLL-urile din " + _dlls + ".");
                return;

            default:
                LoggerInstance.Msg("CPP2IL_VERIFY_MODE nu este nici 'index' nici 'run' - modul nu face nimic.");
                LoggerInstance.Msg("  index: scrie indexul jocului, o singura data.");
                LoggerInstance.Msg("  run:   executa lista de lucru facuta de Cpp2IL.VerifyPlan.");
                return;
        }
    }

    public override void OnUpdate()
    {
        if (_done || _mode.Length == 0)
            return;

        // Acelasi ragaz de cadre ca pana acum, si pentru acelasi motiv: Il2CppInterop isi umple lenes
        // cache-ul de tipuri, iar un index construit prea devreme arata ca si cum jocul n-ar avea metodele.
        // Masurat odata: 273 de metode raportate "nu se gasesc" fata de un build care sigur le contine.
        if (++_frames < 300)
            return;

        if (!_started)
        {
            _started = true;
            Start();
            return;
        }

        if (_mode != "run")
            return;

        try
        {
            Harness.Tick();
        }
        catch (Exception ex)
        {
            LoggerInstance.Error("Harnasul s-a oprit: " + ex);
            Stop();
            return;
        }

        if (Harness.Finished)
            Stop();
    }

    private void Start()
    {
        try
        {
            if (_mode == "index")
            {
                GameIndexDump.Write(_directory,
                    Path.Combine(MelonEnvironment.MelonLoaderDirectory, "Il2CppAssemblies"),
                    message => LoggerInstance.Msg(message));

                Stop();
                return;
            }

            var perFrame = Number("CPP2IL_VERIFY_PER_FRAME", 25, allowZero: false);
            var max = Number("CPP2IL_VERIFY_MAX", 2000, allowZero: true);
            var seedReceivers = Environment.GetEnvironmentVariable("CPP2IL_VERIFY_SEED_RECEIVERS") != "0";

            if (!Harness.Prepare(_directory, _dlls, perFrame, max, seedReceivers, BuildGameIndex,
                    message => LoggerInstance.Msg(message)))
                Stop();
        }
        catch (Exception ex)
        {
            LoggerInstance.Error("Pornirea a esuat: " + ex);
            Stop();
        }
    }

    /// <summary>
    /// Indexul jocului pentru modul de executie: aceleasi chei ca in fisierul scris de modul de index, dar
    /// legate de metodele vii, fiindca pe acelea trebuie sa le cheme cineva.
    ///
    /// Este singurul loc unde harnasul mai construieste ceva, si nu se poate altfel: un MethodBase nu trece
    /// prin fisier. Fisierul poarta hotararile - ce se cheama si cu ce - iar aici se face doar legatura
    /// dintre cheie si metoda.
    /// </summary>
    private Dictionary<string, MethodBase> BuildGameIndex()
    {
        var index = new Dictionary<string, MethodBase>(StringComparer.Ordinal);
        var collided = new HashSet<string>(StringComparer.Ordinal);
        var ignored = new Dictionary<string, Type>(StringComparer.Ordinal);

        GameIndexDump.Fill(Path.Combine(MelonEnvironment.MelonLoaderDirectory, "Il2CppAssemblies"),
            index, collided, ignored, message => LoggerInstance.Msg(message));

        return index;
    }

    private void Stop()
    {
        _done = true;
        LoggerInstance.Msg("Gata. Se inchide jocul.");

        try
        {
            UnityEngine.Application.Quit();
        }
        catch (Exception)
        {
            // Un joc care nu se inchide singur este o neplacere, nu un rezultat pierdut: fisierele sunt
            // deja scrise si golite pe disc.
        }
    }

    private static int Number(string name, int fallback, bool allowZero)
    {
        var text = Environment.GetEnvironmentVariable(name);
        if (text == null || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return fallback;

        if (value < 0 || (value == 0 && !allowZero))
            return fallback;

        return value;
    }
}
