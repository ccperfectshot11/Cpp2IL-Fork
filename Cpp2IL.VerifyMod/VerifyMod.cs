using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cpp2IL.VerifyCore;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

[assembly: MelonInfo(typeof(Cpp2IL.VerifyMod.VerifyMod), "Cpp2IL VerifyMod", "1.0", "Cpp2IL")]
[assembly: MelonGame(null, null)]

namespace Cpp2IL.VerifyMod;

/// <summary>
/// Phase 2 of the differential verification: the same signatures, computed from the REAL methods inside
/// the running game. Phase 1 (<c>Cpp2IL.VerifyCheck</c>) only proves a recovered method is a function of
/// its arguments; comparing the two files is what says whether it is the RIGHT function.
///
/// Reads the Phase 1 file for the method list, resolves each key against the game's own Il2CppInterop
/// assemblies, fuzzes it with the same plan and the same generator, and writes a file in the identical
/// format. Nothing about input generation or hashing lives here - it is all <c>Cpp2IL.VerifyCore</c>,
/// shared with Phase 1 on purpose, because two implementations drift and then every difference in the
/// output is a difference in the harness rather than in the game.
/// </summary>
public class VerifyMod : MelonMod
{
    private const string InputFile = "verifycheck-phase1.json";
    private const string OutputFile = "verifycheck-phase2.json";

    private string _directory;
    private bool _ran;
    private bool _auto;
    private int _frames;

    public override void OnInitializeMelon()
    {
        _directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";

        if (!File.Exists(Path.Combine(_directory, InputFile)))
        {
            LoggerInstance.Msg($"No {InputFile} beside the mod - nothing to verify.");
            return;
        }

        _auto = Environment.GetEnvironmentVariable("CPP2IL_VERIFY_AUTO") == "1";

        if (_auto)
        {
            LoggerInstance.Msg("CPP2IL_VERIFY_AUTO=1: the sweep starts by itself once the game is up.");
        }
        else
        {
            LoggerInstance.Msg("Phase 1 file found. Press F9 in-game to run the verification sweep.");
            LoggerInstance.Msg("It takes minutes and calls thousands of game methods with hostile inputs, so it is");
            LoggerInstance.Msg("not automatic by default: a sweep at startup would look like the game had frozen.");
        }
    }

    // Triggered rather than automatic, and the reason is not politeness. The sweep calls real game code
    // with NaN, denormals and int.MinValue, which is exactly what nothing in a shipped game is written to
    // survive; if one of those takes the process down, it must be the player's choice to have started it,
    // and the crash journal below must already name the culprit so the next run gets past it.
    public override void OnUpdate()
    {
        if (_ran)
            return;

        // Automatic mode waits a few frames rather than starting at the first one: Il2CppInterop fills in
        // its type cache lazily, and indexing the game's assemblies before it has settled finds fewer
        // methods, which would read as "the game does not have them" instead of "we asked too early".
        if (_auto && ++_frames < 300)
            return;

        if (!_auto && !Input.GetKeyDown(KeyCode.F9))
            return;

        _ran = true;

        try
        {
            Run(Path.Combine(_directory, InputFile), Path.Combine(_directory, OutputFile));
        }
        catch (Exception ex)
        {
            LoggerInstance.Error($"Verification run failed: {ex}");
        }
    }

    private void Run(string phase1Path, string outputPath)
    {
        var request = Phase1File.Read(File.ReadAllText(phase1Path));
        LoggerInstance.Msg($"Phase 1 lists {request.Keys.Count} comparable methods; plan seed {request.Plan.Seed}.");

        var index = BuildNativeIndex();
        LoggerInstance.Msg($"Indexed {index.Count} candidate methods from the game's own assemblies.");

        var journalPath = outputPath + ".inflight";
        var skipPath = outputPath + ".skip";
        var skip = File.Exists(skipPath)
            ? new HashSet<string>(File.ReadAllLines(skipPath).Where(l => l.Length > 0), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        if (File.Exists(journalPath))
        {
            var crashed = File.ReadAllText(journalPath).Trim();
            if (crashed.Length > 0 && skip.Add(crashed))
            {
                File.AppendAllText(skipPath, crashed + Environment.NewLine);
                LoggerInstance.Warning($"Last run died in {crashed} - skipping it from now on.");
            }

            File.Delete(journalPath);
        }

        var results = new List<MethodFuzzResult>();
        var resolved = 0;
        var missing = 0;

        foreach (var key in request.Keys)
        {
            if (!index.TryGetValue(key, out var method))
            {
                missing++;
                continue;
            }

            resolved++;

            if (skip.Contains(key))
                continue;

            // Written before the call, not after: a native method that takes the process down cannot be
            // caught, so the only way to get past it is to know on the next run which one it was. Same
            // mechanism as Phase 1's journal, and the two files are read the same way.
            File.WriteAllText(journalPath, key);

            MethodFuzzResult result;
            try
            {
                result = MethodFuzzer.Run(method, request.Plan);
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                result = new MethodFuzzResult { Key = key, Failure = inner.GetType().Name + ": " + inner.Message };
            }

            // The key from the file, not the one just computed: Phase 1 is the side that decided what a
            // method is called, and a key that differs by a normalisation detail would silently drop the
            // pair out of the comparison instead of failing loudly.
            result.Key = key;
            results.Add(result);

            if (results.Count % 250 == 0)
                LoggerInstance.Msg($"  {results.Count} / {resolved} fuzzed...");
        }

        if (File.Exists(journalPath))
            File.Delete(journalPath);

        using (var writer = new StreamWriter(outputPath, false))
            SignatureJson.Write(writer, SignatureJson.PhaseNative, "in-game", request.Plan, results);

        LoggerInstance.Msg($"Resolved {resolved}, could not find {missing}. Wrote {results.Count} signatures to {outputPath}.");
        LoggerInstance.Msg("Compare with: Cpp2IL.VerifyCheck --compare phase1.json phase2.json");

        // A scripted run has to end by itself, or the harness would wait for a window nobody is going to
        // close. Interactive runs stay up, because the point there is to keep playing.
        if (_auto)
        {
            LoggerInstance.Msg("VERIFY_DONE");
            UnityEngine.Application.Quit();
        }
    }

    /// <summary>
    /// Every method in the game's Il2CppInterop assemblies that could match a Phase 1 key, indexed by that
    /// key. Built by walking the loaded assemblies rather than by resolving keys one at a time, because a
    /// key names a type by its normalised name and there is no reverse lookup from that back to a Type.
    /// </summary>
    private Dictionary<string, MethodBase> BuildNativeIndex()
    {
        var index = new Dictionary<string, MethodBase>(StringComparer.Ordinal);

        LoadEveryInteropAssembly();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            // The mod's own assemblies would otherwise index the RECOVERED methods sitting next to it and
            // compare them against themselves, which agrees perfectly and means nothing.
            var name = assembly.GetName().Name ?? "";
            if (name.StartsWith("Cpp2IL.", StringComparison.Ordinal) || name.StartsWith("MelonLoader", StringComparison.Ordinal))
                continue;

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException partial)
            {
                // A half-loaded assembly still contains usable types, and dropping the whole assembly
                // because one type failed would quietly shrink the comparison.
                types = partial.Types.Where(t => t != null).ToArray();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var type in types)
                IndexType(type, index);
        }

        return index;
    }

    /// <summary>
    /// Forces every Il2CppInterop assembly to load before the index is built.
    ///
    /// They load lazily - only when something first touches a type in them - so at mod-init time the
    /// domain holds whatever the game happened to need so far, and everything else is simply absent.
    /// Indexing that gives an index that looks complete and is not: 273 Phase 1 methods came back
    /// "could not find" against a build that certainly contains them, and the ones that vanished were
    /// whole types at a time (FPMathUtils, UIGradientUtils, BattlePassLevel) rather than a scattering,
    /// which is what an unloaded assembly looks like from the outside.
    /// </summary>
    private void LoadEveryInteropAssembly()
    {
        var directory = Path.Combine(MelonEnvironment.MelonLoaderDirectory, "Il2CppAssemblies");

        if (!Directory.Exists(directory))
        {
            LoggerInstance.Warning($"No Il2CppAssemblies at {directory}; the index will only see what is already loaded.");
            return;
        }

        var loaded = 0;

        foreach (var path in Directory.GetFiles(directory, "*.dll"))
        {
            try
            {
                // By name, not from the file: loading the same assembly a second time from its path would
                // give a duplicate identity, and every type in it would compare unequal to the game's own.
                Assembly.Load(AssemblyName.GetAssemblyName(path));
                loaded++;
            }
            catch (Exception)
            {
                // A dependency that will not resolve costs that one assembly, not the run.
            }
        }

        LoggerInstance.Msg($"Loaded {loaded} interop assemblies before indexing.");
    }

    private static void IndexType(Type type, Dictionary<string, MethodBase> index)
    {
        MethodInfo[] methods;
        try
        {
            methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var method in methods)
        {
            if (method.IsGenericMethodDefinition || method.ContainsGenericParameters)
                continue;

            string key;
            try
            {
                key = MethodKeys.For(method);
            }
            catch (Exception)
            {
                continue;
            }

            // First one wins. A duplicate key means two methods Phase 1 could not tell apart either, so
            // choosing between them here would be guessing which one Phase 1 measured.
            if (!index.ContainsKey(key))
                index[key] = method;
        }
    }
}
