using System.Diagnostics;
using System.Net.Http;
using System.Text;

namespace IL2CPPDecompiler;

internal static class Program
{
    private const string DefaultCpp2Il =
        @"C:\Users\Helper\Desktop\CPP2IL\Cpp2IL\Cpp2IL\bin\Release\net10.0\Cpp2IL.dll";

    private const string DefaultNetSpy =
        @"C:\Users\Helper\Desktop\NetSpy\NetSpy\NetSpy\NetSpy\bin\Release\net10.0-windows\NetSpy.Console.dll";

    private const string DefaultAssetRipper =
        @"C:\Users\Helper\Desktop\CPP2IL\AssetRipper\Source\0Bins\AssetRipper.GUI.Free\Release\AssetRipper.GUI.Free.dll";

    private const int AssetRipperPort = 8123;

    private static int Main(string[] args)
    {
        string? gamePath = null;
        string? outputPath = null;

        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--path":
                case "-p":
                    gamePath = args[++i];
                    break;
                case "--output":
                case "-o":
                    outputPath = args[++i];
                    break;
            }
        }

        if (gamePath == null || outputPath == null)
        {
            Console.WriteLine("IL2CPPDecompiler --path <folder joc> --output <folder iesire>");
            Console.WriteLine();
            Console.WriteLine("Produce:");
            Console.WriteLine("  <iesire>\\dlls    - assembly-uri cu corpul metodelor recuperat (Cpp2IL)");
            Console.WriteLine("  <iesire>\\source  - cod C# decompilat (NetSpy)");
            Console.WriteLine("  <iesire>\\assets  - proiect Unity cu asset-urile (AssetRipper)");
            return 1;
        }

        if (!Directory.Exists(gamePath))
        {
            Console.Error.WriteLine($"Folderul jocului nu exista: {gamePath}");
            return 1;
        }

        var dllDir = Path.Combine(outputPath, "dlls");
        var srcDir = Path.Combine(outputPath, "source");
        var assetDir = Path.Combine(outputPath, "assets");
        Directory.CreateDirectory(dllDir);
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(assetDir);

        var cpp2il = Env("CPP2IL_DLL", DefaultCpp2Il);
        var netspy = Env("NETSPY_DLL", DefaultNetSpy);
        var assetRipper = Env("ASSETRIPPER_DLL", DefaultAssetRipper);

        if (!Step1RecoverAssemblies(cpp2il, gamePath, dllDir)) return 2;
        if (!Step2DecompileToCSharp(netspy, dllDir, srcDir)) return 3;
        if (!Step3ExportAssets(assetRipper, gamePath, assetDir)) return 4;

        Console.WriteLine();
        Console.WriteLine("Gata.");
        Console.WriteLine($"  DLL-uri : {dllDir}");
        Console.WriteLine($"  Sursa C#: {srcDir}");
        Console.WriteLine($"  Asset-uri: {assetDir}");
        return 0;
    }

    private static string Env(string name, string fallback)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    private static readonly string[] EnginePrefixes =
    [
        "UnityEngine", "Unity.", "Unity.Services", "UnityEditor",
        "System", "mscorlib", "netstandard", "Microsoft.", "Mono.",
        "Il2Cpp__Generated", "WindowsBase", "PresentationCore", "PresentationFramework",
    ];

    private static bool IsEngineOrRuntime(string fileName)
        => EnginePrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static bool Step1RecoverAssemblies(string cpp2il, string gamePath, string dllDir)
    {
        Console.WriteLine("[1/3] Cpp2IL - recuperez assembly-urile cu corpul metodelor...");
        if (!File.Exists(cpp2il))
        {
            Console.Error.WriteLine($"  Cpp2IL negasit: {cpp2il}");
            return false;
        }

        var ok = Run("dotnet", [cpp2il, "--game-path", gamePath, "--output-as", "dll_il_recovery", "--output-to", dllDir]);
        if (!ok) return false;

        var count = Directory.GetFiles(dllDir, "*.dll").Length;
        Console.WriteLine($"  -> {count} assembly-uri");
        return count > 0;
    }

    private static bool Step2DecompileToCSharp(string netspy, string dllDir, string srcDir)
    {
        Console.WriteLine("[2/3] NetSpy - decompilez in C#...");
        if (!File.Exists(netspy))
        {
            Console.Error.WriteLine($"  NetSpy negasit: {netspy}");
            return false;
        }

        var netspyDir = Path.GetDirectoryName(netspy)!;
        var targets = Directory.GetFiles(dllDir, "*.dll")
            .Where(f => !IsEngineOrRuntime(Path.GetFileName(f)))
            .ToArray();

        Console.WriteLine($"  {targets.Length} assembly-uri de decompilat");

        var done = 0;
        foreach (var dll in targets)
        {
            var name = Path.GetFileNameWithoutExtension(dll);
            var target = Path.Combine(srcDir, name);
            Directory.CreateDirectory(target);

            var args = new List<string> { netspy, "-o", target, "--no-sln", "--asm-path", dllDir, dll };
            Run("dotnet", args.ToArray(), netspyDir, quiet: true);

            done++;
            if (done % 10 == 0 || done == targets.Length)
                Console.WriteLine($"  {done}/{targets.Length}");
        }

        var files = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories).Length;
        Console.WriteLine($"  -> {files} fisiere .cs");
        return files > 0;
    }

    private static bool Step3ExportAssets(string assetRipper, string gamePath, string assetDir)
    {
        Console.WriteLine("[3/3] AssetRipper - export asset-uri...");
        if (!File.Exists(assetRipper))
        {
            Console.Error.WriteLine($"  AssetRipper negasit: {assetRipper}");
            return false;
        }

        var dir = Path.GetDirectoryName(assetRipper)!;
        using var proc = Start("dotnet", [assetRipper, "--port", AssetRipperPort.ToString(), "--headless"], dir);
        if (proc == null) return false;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromHours(3) };
            var root = $"http://127.0.0.1:{AssetRipperPort}";

            if (!WaitForServer(http, root))
            {
                Console.Error.WriteLine("  AssetRipper nu a pornit");
                return false;
            }

            // Cpp2IL + NetSpy se ocupa de cod, deci AssetRipper exporta DLL-uri in loc de C#.
            // Scripturile raman incarcate, altfel prefab-urile si MonoBehaviour-urile ies rupte.
            Console.WriteLine("  configurez (fara export de cod sursa)...");
            Post(http, $"{root}/Settings/Update", new() { ["ScriptExportMode"] = "DllExportWithoutRenaming" });

            Console.WriteLine("  incarc jocul...");
            Post(http, $"{root}/LoadFolder", new() { ["Path"] = gamePath });

            Console.WriteLine("  export proiect Unity...");
            Post(http, $"{root}/Export/UnityProject", new()
            {
                ["Path"] = assetDir,
                ["CreateSubfolder"] = "false",
            });

            Console.WriteLine("  -> gata");
            return true;
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
        }
    }

    private static bool WaitForServer(HttpClient http, string root)
    {
        for (var i = 0; i < 60; i++)
        {
            try
            {
                using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var r = probe.GetAsync(root).GetAwaiter().GetResult();
                if (r.IsSuccessStatusCode) return true;
            }
            catch { }
            Thread.Sleep(1000);
        }
        return false;
    }

    private static void Post(HttpClient http, string url, Dictionary<string, string> form)
    {
        using var content = new FormUrlEncodedContent(form);
        var response = http.PostAsync(url, content).GetAwaiter().GetResult();
        if ((int)response.StatusCode >= 400)
            Console.Error.WriteLine($"  {url} -> HTTP {(int)response.StatusCode}");
    }

    private static bool Run(string file, string[] args, string? workingDir = null, bool quiet = false)
    {
        using var p = Start(file, args, workingDir, quiet);
        if (p == null) return false;
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    private static Process? Start(string file, string[] args, string? workingDir = null, bool quiet = false)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = quiet,
            RedirectStandardError = quiet,
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try { return Process.Start(psi); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  nu pot porni {file}: {e.Message}");
            return null;
        }
    }
}
