using System;
using System.Linq;
using System.Reflection;

namespace Cpp2IL.VerifyMod;

/// <summary>
/// Indreapta jocul catre un backend local, ca sa treaca de autentificare si sa ajunga in meniuri.
///
/// Fara asta faza 3 masoara ecranul de incarcare: un carlig se declanseaza doar daca jocul chiar cheama
/// metoda, iar un joc blocat la login nu cheama nimic din meniuri, din meciuri sau din simulare. Tiparul
/// e luat din modul propriu al utilizatorului (StumblePeakTooSigma/BackendRedirect.cs): un prefix pe
/// Initializer.Awake care scrie _runtimeConfiguration._environmentRuntimeConfiguration._backendHost.
///
/// Totul se face prin reflexie, nu prin referinta la assembly-ul de interop: acela e regenerat de
/// MelonLoader cu un Cpp2IL din 2022 si o referinta de compilare ne-ar lega de forma lui de atunci.
///
/// CPP2IL_BACKEND=&lt;url&gt; porneste redirectionarea; fara variabila nu se atinge nimic.
/// </summary>
internal static class BackendRedirect
{
    private static string _url;
    private static Action<string> _log = _ => { };

    public static string Requested => Environment.GetEnvironmentVariable("CPP2IL_BACKEND");

    public static void Install(HarmonyLib.Harmony harmony, Action<string> log)
    {
        _log = log ?? (_ => { });
        _url = Requested;
        if (string.IsNullOrWhiteSpace(_url))
            return;

        var initializer = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.FullName == "Il2CppStumble.Initializer" || t.Name == "Initializer" && t.Namespace == "Il2CppStumble");

        if (initializer == null)
        {
            _log("BackendRedirect: nu am gasit Il2CppStumble.Initializer, jocul ramane pe backendul lui.");
            return;
        }

        var awake = initializer.GetMethod("Awake", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (awake == null)
        {
            _log("BackendRedirect: Initializer nu are Awake.");
            return;
        }

        var prefix = typeof(BackendRedirect).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static);
        harmony.Patch(awake, prefix: new HarmonyLib.HarmonyMethod(prefix));
        _log($"BackendRedirect: armat catre {_url}.");
    }

    private static void Prefix(object __instance)
    {
        try
        {
            // Lantul e _runtimeConfiguration -> _environmentRuntimeConfiguration -> _backendHost, iar
            // Il2CppInterop expune campurile native ca proprietati cu exact numele campului.
            var config = Read(__instance, "_runtimeConfiguration");
            var env = Read(config, "_environmentRuntimeConfiguration");
            if (env == null)
            {
                _log("BackendRedirect: lantul de configurare nu s-a rezolvat, jocul ramane pe backendul lui.");
                return;
            }

            Write(env, "_backendHost", _url);
            _log($"BackendRedirect: _backendHost := {_url}");
        }
        catch (Exception e)
        {
            _log("BackendRedirect: " + e.GetType().Name + ": " + e.Message);
        }
    }

    private static object Read(object target, string name)
    {
        if (target == null) return null;
        var t = target.GetType();
        var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop != null && prop.CanRead) return prop.GetValue(target);
        return t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
    }

    private static void Write(object target, string name, string value)
    {
        var t = target.GetType();
        var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop != null && prop.CanWrite) { prop.SetValue(target, value); return; }
        t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(target, value);
    }
}
