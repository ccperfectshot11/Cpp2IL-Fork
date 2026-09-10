using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Cpp2IL.VerifyCore;

// How much of the input space one method gets. Both hosts must run the SAME plan or the two signature
// files describe different experiments and comparing them proves nothing, so the plan is written into
// the JSON header and checked when the files are diffed.
public sealed class FuzzPlan
{
    public ulong Seed = 20260910;
    public int RandomIterations = 10000;

    // The edge sweep is a full cartesian product, so it explodes with arity: three FP arguments are
    // 23^3 = 12167 combinations. Capping it keeps a three-argument method from costing twenty times
    // what a one-argument method does, at the price of the high leaves varying less.
    public int MaxEdgeIterations = 2048;
}

public sealed class MethodFuzzResult
{
    public string Key;                       // host-independent identity, see MethodKeys
    public string Assembly;
    public string Type;
    public string Method;
    public string Token;                     // metadata token, how Phase 1 found it again
    public bool ReadsStatics;                // set by the selector, see the note in Selector.cs

    public bool Supported;
    public string Failure;                   // why it was not run at all

    public int EdgeIterations;
    public int RandomIterations;
    public string Signature;                 // THE number: SHA-256 over every input/output pair
    public string SecondPassSignature;
    public bool NonDeterministic;            // the two passes disagreed - the method is not a function
    public int ThrewCount;
    public bool AllThrew;
    public int AbortedAfter;                 // >0 when the sweep stopped early, see FatalKinds
    public bool ConstantOutput;              // it ran, it never threw, and it ignored its arguments
    public List<string> ExceptionKinds = new List<string>();
    public long ElapsedMs;
}

public static class MethodFuzzer
{
    // Reflection host (Phase 1): the method is a managed MethodBase and Invoke does the work.
    public static MethodFuzzResult Run(MethodBase method, FuzzPlan plan)
    {
        var parameters = method.GetParameters();
        var parameterTypes = new Type[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
            parameterTypes[i] = parameters[i].ParameterType;

        var result = Run(MethodKeys.For(method), parameterTypes, (method as MethodInfo)?.ReturnType, args => method.Invoke(null, args), plan);
        result.Type = method.DeclaringType == null ? "" : method.DeclaringType.FullName;
        result.Method = method.Name;
        return result;
    }

    // Host-agnostic entry point, and the one Phase 2 is meant to use. A primitive-only signature is
    // blittable by construction, so a host that has the address of the real native method can call it
    // through a function pointer and pass the boxed arguments straight through here - no MethodBase, no
    // marshalling, and above all no second copy of the input generation or the hashing.
    public static MethodFuzzResult Run(string key, Type[] parameterTypes, Type returnType, Func<object[], object> invoke, FuzzPlan plan)
    {
        var result = new MethodFuzzResult { Key = key };

        var shapes = new ValueShape[parameterTypes.Length];
        for (var i = 0; i < parameterTypes.Length; i++)
        {
            shapes[i] = ValueShape.For(parameterTypes[i]);
            if (shapes[i] == null)
            {
                result.Failure = "parameter " + i + " is not a primitive-only value: " + parameterTypes[i].FullName;
                return result;
            }
        }

        ValueShape returnShape = null;
        if (returnType != null && returnType != typeof(void))
        {
            returnShape = ValueShape.For(returnType);
            if (returnShape == null)
            {
                result.Failure = "return type is not a primitive-only value: " + returnType.FullName;
                return result;
            }
        }

        var kinds = Flatten(shapes);
        result.Supported = true;
        result.EdgeIterations = EdgeSweepLength(kinds, plan.MaxEdgeIterations);

        // A method with no inputs is a constant, so ten thousand calls would hash the same value ten
        // thousand times. Two calls still catch a "constant" that is not one.
        result.RandomIterations = kinds.Length == 0 ? Math.Min(plan.RandomIterations, 2) : plan.RandomIterations;

        var watch = Stopwatch.StartNew();
        var first = Sweep(key, shapes, returnShape, kinds, invoke, result, plan);

        // The second pass is not paranoia. A method that reads a static field, a clock, or memory the
        // recovered layout got wrong will return different values on the second run, and a signature
        // from such a method cannot be compared with anything - it has to be reported, not diffed.
        var second = Sweep(key, shapes, returnShape, kinds, invoke, null, plan);

        watch.Stop();

        result.Signature = first.Hex;
        result.SecondPassSignature = second.Hex;
        result.NonDeterministic = first.Hex != second.Hex;
        result.ThrewCount = first.Threw;
        result.AllThrew = first.Threw > 0 && first.Threw == first.Total;
        result.AbortedAfter = first.AbortedAfter;
        result.ConstantOutput = kinds.Length > 0 && first.ConstantOutput;
        result.ElapsedMs = watch.ElapsedMilliseconds;
        return result;
    }

    private struct SweepOutcome
    {
        public string Hex;
        public int Threw;
        public int Total;
        public int AbortedAfter;
        public bool ConstantOutput;
    }

    // Exceptions that are a property of the METHOD, never of the arguments. InvalidProgramException is
    // the JIT refusing the recovered IL; the load failures are a type or member the recovered metadata
    // promised and cannot deliver; a failed class constructor stays failed for the life of the process.
    // Ten thousand more calls cannot change any of them, and each one costs a full exception throw - the
    // first run of this harness spent five of its six minutes doing exactly that.
    private static readonly string[] FatalKinds =
    [
        "System.InvalidProgramException", "System.TypeLoadException", "System.TypeInitializationException",
        "System.MissingMethodException", "System.MissingFieldException", "System.BadImageFormatException",
    ];

    // Enough calls to be sure it is not one unlucky input, few enough to be free.
    private const int FatalProbe = 32;

    private static SweepOutcome Sweep(string key, ValueShape[] shapes, ValueShape returnShape, LeafKind[] kinds, Func<object[], object> invoke, MethodFuzzResult report, FuzzPlan plan)
    {
        var outcome = new SweepOutcome();
        var edgeCount = EdgeSweepLength(kinds, plan.MaxEdgeIterations);
        var randomCount = kinds.Length == 0 ? Math.Min(plan.RandomIterations, 2) : plan.RandomIterations;
        var strides = Strides(kinds);
        var random = new DeterministicRandom(DeterministicRandom.SeedFor(plan.Seed, key));
        var leaves = new object[kinds.Length];
        var args = new object[shapes.Length];
        var returnedOnce = false;
        var firstOutput = 0UL;

        using (var hash = new SignatureHash())
        {
            // The plan is hashed first so a file produced with different settings can never be mistaken
            // for a mismatch in the code under test - it comes out as a different signature everywhere.
            hash.AbsorbText(key);
            hash.AbsorbInt32(edgeCount);
            hash.AbsorbInt32(randomCount);

            for (var iteration = 0; iteration < edgeCount + randomCount; iteration++)
            {
                if (iteration < edgeCount)
                    for (var leaf = 0; leaf < kinds.Length; leaf++)
                        leaves[leaf] = FuzzInputs.EdgeValue(kinds[leaf], (iteration / strides[leaf]) % FuzzInputs.EdgeCount(kinds[leaf]));
                else
                    for (var leaf = 0; leaf < kinds.Length; leaf++)
                        leaves[leaf] = FuzzInputs.RandomValue(kinds[leaf], ref random);

                var next = 0;
                for (var i = 0; i < shapes.Length; i++)
                    args[i] = shapes[i].Materialise(leaves, ref next);

                // Absorbed by reading the built arguments back, not by hashing the values we generated:
                // if a struct layout is wrong the argument does not hold what was written into it, and
                // the method sees what it holds.
                hash.AbsorbTag(SignatureHash.TagInputs);
                for (var i = 0; i < shapes.Length; i++)
                    shapes[i].Absorb(args[i], hash);

                outcome.Total++;
                object returned;
                try
                {
                    returned = invoke(args);
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    outcome.Threw++;

                    // The type name only. Exception MESSAGES carry addresses, member names and the
                    // current culture, none of which the game host would reproduce, so hashing them
                    // would turn every throwing method into a false mismatch.
                    hash.AbsorbTag(SignatureHash.TagThrew);
                    hash.AbsorbText(inner.GetType().FullName);
                    if (report != null && report.ExceptionKinds.Count < 8 && !report.ExceptionKinds.Contains(inner.GetType().FullName))
                        report.ExceptionKinds.Add(inner.GetType().FullName);

                    // The abort condition depends only on what the method did, so both passes stop at the
                    // same iteration and their digests still describe the same experiment.
                    if (outcome.Threw == outcome.Total && outcome.Total >= FatalProbe && Array.IndexOf(FatalKinds, inner.GetType().FullName) >= 0)
                    {
                        outcome.AbortedAfter = outcome.Total;
                        break;
                    }

                    continue;
                }

                hash.AbsorbTag(SignatureHash.TagReturned);
                if (returnShape == null)
                    hash.AbsorbTag(SignatureHash.TagVoid);
                else
                {
                    hash.AbsorbTag(SignatureHash.TagOutput);
                    hash.RestartRunning();
                    returnShape.Absorb(returned, hash);
                    if (returnedOnce && hash.Running != firstOutput)
                        outcome.ConstantOutput = false;
                    else if (!returnedOnce)
                    {
                        returnedOnce = true;
                        outcome.ConstantOutput = true;
                        firstOutput = hash.Running;
                    }
                }
            }

            outcome.Hex = hash.ToHex(16);
        }

        return outcome;
    }

    private static LeafKind[] Flatten(ValueShape[] shapes)
    {
        var kinds = new List<LeafKind>();
        foreach (var shape in shapes)
            Collect(shape, kinds);

        return kinds.ToArray();
    }

    private static void Collect(ValueShape shape, List<LeafKind> into)
    {
        if (shape.IsLeaf)
        {
            into.Add(shape.Kind);
            return;
        }

        foreach (var child in shape.Children)
            Collect(child, into);
    }

    private static int[] Strides(LeafKind[] kinds)
    {
        var strides = new int[kinds.Length];
        var stride = 1;
        for (var i = 0; i < kinds.Length; i++)
        {
            strides[i] = stride;
            stride = SaturatingMultiply(stride, FuzzInputs.EdgeCount(kinds[i]));
        }

        return strides;
    }

    private static int EdgeSweepLength(LeafKind[] kinds, int cap)
    {
        var product = 1;
        foreach (var kind in kinds)
            product = SaturatingMultiply(product, FuzzInputs.EdgeCount(kind));

        return Math.Min(product, cap);
    }

    private static int SaturatingMultiply(int a, int b) => a > int.MaxValue / Math.Max(b, 1) ? int.MaxValue : a * b;
}

// The identity a signature is filed under. It must be computable on both hosts and identical there, so
// it is built from names and shapes only - never from a metadata token, which Cpp2IL and Il2CppInterop
// hand out independently, and never from the assembly name, which Il2CppInterop prefixes with Il2Cpp.
public static class MethodKeys
{
    public static string For(MethodBase method)
    {
        var builder = new StringBuilder();
        builder.Append(Normalise(method.DeclaringType));
        builder.Append("::");
        builder.Append(method.Name);
        builder.Append('(');
        var parameters = method.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
        {
            if (i > 0)
                builder.Append(',');

            builder.Append(Normalise(parameters[i].ParameterType));
        }

        builder.Append(')');
        builder.Append("->");
        builder.Append(Normalise((method as MethodInfo)?.ReturnType));
        return builder.ToString();
    }

    private static string Normalise(Type type)
    {
        if (type == null)
            return "void";

        var name = type.FullName ?? type.Name;

        // Il2CppInterop escapes namespaces that would collide with the BCL by prefixing them, so the
        // game's own System.Object arrives as Il2CppSystem.Object. Stripping the prefix is what lets the
        // same method have the same key in both hosts.
        if (name.StartsWith("Il2Cpp", StringComparison.Ordinal))
            name = name.Substring("Il2Cpp".Length);

        return name;
    }
}
