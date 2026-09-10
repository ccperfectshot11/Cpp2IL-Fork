using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Cpp2IL.VerifyCheck;

// The recovered assemblies declare the game's type names, and the game shipped a full BCL - so out_ot
// contains an mscorlib.dll that declares System.Int32, System.Object and everything else. Loading that
// into the default context would give the process two System.Int32 types, and every Invoke would fail
// with an argument mismatch that reads exactly like a bug in the recovered code. Its own context, with
// the core assemblies deliberately NOT resolved from the directory, is what keeps the primitives in a
// recovered signature identical to the primitives reflection hands back.
internal sealed class RecoveredAssemblyContext : AssemblyLoadContext
{
    private readonly string _directory;

    public RecoveredAssemblyContext(string directory) : base("cpp2il-recovered", isCollectible: false) => _directory = directory;

    // Only the assemblies that define the primitives themselves are shared with the host. Everything
    // else - including the recovered System.dll, System.Core.dll and so on - comes out of the output
    // directory, because a recovered type must bind to the recovered types it was compiled against.
    private static readonly HashSet<string> HostOwned = new(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib", "netstandard", "System.Runtime", "System.Private.CoreLib",
    };

    // Loading a recovered core library is not merely useless, it is destructive: once an assembly called
    // mscorlib exists inside this context, every LATER reference to mscorlib from any other recovered
    // assembly binds to it instead of falling through to the runtime's facade, and since the recovered
    // System.Object has no parent the whole context dies from that point on. The first run of this
    // harness lost 1,713 methods to exactly that, all of them in assemblies that happened to be loaded
    // after mscorlib.dll.
    public static bool IsHostOwned(string assemblyName) => assemblyName != null && HostOwned.Contains(assemblyName);

    protected override Assembly Load(AssemblyName name)
    {
        if (name.Name == null || HostOwned.Contains(name.Name))
            return null;

        var candidate = Path.Combine(_directory, name.Name + ".dll");

        // Returning null hands the request back to the default context, which is right for a reference
        // the output directory does not contain: better a real framework assembly than a hard failure
        // while resolving a type the fuzzed method may never touch.
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }
}
