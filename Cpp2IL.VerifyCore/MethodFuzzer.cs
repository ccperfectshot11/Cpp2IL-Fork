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

    // Tier 2: the receiver of a struct instance method, generated and hashed like any other input.
    public bool IsInstance;
    public string ReceiverType;
    public bool MutatesReceiver;             // at least one call wrote through the receiver
    public int MutatedCount;

    // Nothing came back, nothing was written through the receiver, nothing was thrown: the digest of
    // such a method is a hash of its INPUTS and of nothing else, so it would agree between the two
    // phases whatever either side actually did. Recorded so the comparison can throw it out rather than
    // count it as agreement.
    public bool NoObservableOutput;
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

        // An instance method on a primitive-only struct is a function of (receiver, arguments), and the
        // receiver is a value the harness can generate exactly as it generates a parameter. Reflection
        // hands a value type instance method a managed pointer INTO the box rather than a copy of it, so
        // a write through the receiver is still there in the same box when the call returns - which is
        // what makes the post-call receiver readable as an output.
        var receiverType = method.IsStatic ? null : method.DeclaringType;

        var result = Run(MethodKeys.For(method), receiverType, parameterTypes, (method as MethodInfo)?.ReturnType, (receiver, args) => method.Invoke(receiver, args), plan);
        result.Type = method.DeclaringType == null ? "" : method.DeclaringType.FullName;
        result.Method = method.Name;
        return result;
    }

    // Static-only entry point, kept so a host that never calls an instance method does not have to say
    // so on every call.
    public static MethodFuzzResult Run(string key, Type[] parameterTypes, Type returnType, Func<object[], object> invoke, FuzzPlan plan)
        => Run(key, null, parameterTypes, returnType, (receiver, args) => invoke(args), plan);

    // Host-agnostic entry point, and the one Phase 2 is meant to use. A primitive-only signature is
    // blittable by construction, so a host that has the address of the real native method can call it
    // through a function pointer and pass the boxed arguments straight through here - no MethodBase, no
    // marshalling, and above all no second copy of the input generation or the hashing.
    //
    // A null receiverType means a static method. Otherwise invoke is handed the boxed receiver and has
    // to leave the state the call produced IN THAT BOX: the bytes are read back out of it afterwards, so
    // a host that called through a native pointer has to copy its buffer back before returning.
    public static MethodFuzzResult Run(string key, Type receiverType, Type[] parameterTypes, Type returnType, Func<object, object[], object> invoke, FuzzPlan plan)
    {
        var result = new MethodFuzzResult { Key = key };

        ValueShape receiverShape = null;
        if (receiverType != null)
        {
            receiverShape = ValueShape.For(receiverType);
            if (receiverShape == null)
            {
                result.Failure = "receiver is not a primitive-only value: " + receiverType.FullName;
                return result;
            }

            result.IsInstance = true;
            result.ReceiverType = receiverType.FullName;
        }

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

        var kinds = Flatten(receiverShape, shapes);
        result.Supported = true;
        result.EdgeIterations = EdgeSweepLength(kinds, plan.MaxEdgeIterations);

        // A method with no inputs is a constant, so ten thousand calls would hash the same value ten
        // thousand times. Two calls still catch a "constant" that is not one.
        result.RandomIterations = kinds.Length == 0 ? Math.Min(plan.RandomIterations, 2) : plan.RandomIterations;

        var watch = Stopwatch.StartNew();
        var first = Sweep(key, receiverShape, shapes, returnShape, kinds, invoke, result, plan);

        // The second pass is not paranoia. A method that reads a static field, a clock, or memory the
        // recovered layout got wrong will return different values on the second run, and a signature
        // from such a method cannot be compared with anything - it has to be reported, not diffed.
        var second = Sweep(key, receiverShape, shapes, returnShape, kinds, invoke, null, plan);

        watch.Stop();

        result.Signature = first.Hex;
        result.SecondPassSignature = second.Hex;
        result.NonDeterministic = first.Hex != second.Hex;
        result.ThrewCount = first.Threw;
        result.AllThrew = first.Threw > 0 && first.Threw == first.Total;
        result.AbortedAfter = first.AbortedAfter;
        result.ConstantOutput = kinds.Length > 0 && first.ConstantOutput;
        result.MutatedCount = first.Mutations;
        result.MutatesReceiver = first.Mutations > 0;
        result.NoObservableOutput = returnShape == null && first.Mutations == 0 && first.Threw == 0;
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
        public int Mutations;
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

    private static SweepOutcome Sweep(string key, ValueShape receiverShape, ValueShape[] shapes, ValueShape returnShape, LeafKind[] kinds, Func<object, object[], object> invoke, MethodFuzzResult report, FuzzPlan plan)
    {
        var outcome = new SweepOutcome();
        var edgeCount = EdgeSweepLength(kinds, plan.MaxEdgeIterations);
        var randomCount = kinds.Length == 0 ? Math.Min(plan.RandomIterations, 2) : plan.RandomIterations;
        var strides = Strides(kinds);
        var random = new DeterministicRandom(DeterministicRandom.SeedFor(plan.Seed, key));
        var leaves = new object[kinds.Length];
        var args = new object[shapes.Length];
        var returnedOnce = false;
        var firstReturn = 0UL;
        var firstReceiver = 0UL;

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

                // A FRESH receiver box every iteration, filled from the same leaf run as the arguments:
                // reusing one would carry the previous call's mutation into the next call's input, and
                // the sweep would then depend on its own history instead of on the seed alone.
                var receiver = receiverShape == null ? null : receiverShape.Materialise(leaves, ref next);
                for (var i = 0; i < shapes.Length; i++)
                    args[i] = shapes[i].Materialise(leaves, ref next);

                // Absorbed by reading the built arguments back, not by hashing the values we generated:
                // if a struct layout is wrong the argument does not hold what was written into it, and
                // the method sees what it holds.
                hash.AbsorbTag(SignatureHash.TagInputs);
                var receiverBefore = 0UL;
                if (receiverShape != null)
                {
                    hash.AbsorbTag(SignatureHash.TagReceiver);
                    hash.RestartRunning();
                    receiverShape.Absorb(receiver, hash);
                    receiverBefore = hash.Running;
                }

                for (var i = 0; i < shapes.Length; i++)
                    shapes[i].Absorb(args[i], hash);

                outcome.Total++;
                object returned = null;
                Exception failure = null;
                try
                {
                    returned = invoke(receiver, args);
                }
                catch (Exception ex)
                {
                    failure = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                }

                if (failure != null)
                {
                    outcome.Threw++;

                    // The type name only. Exception MESSAGES carry addresses, member names and the
                    // current culture, none of which the game host would reproduce, so hashing them
                    // would turn every throwing method into a false mismatch.
                    hash.AbsorbTag(SignatureHash.TagThrew);
                    hash.AbsorbText(failure.GetType().FullName);
                    if (report != null && report.ExceptionKinds.Count < 8 && !report.ExceptionKinds.Contains(failure.GetType().FullName))
                        report.ExceptionKinds.Add(failure.GetType().FullName);
                }
                else
                {
                    hash.AbsorbTag(SignatureHash.TagReturned);
                    hash.RestartRunning();
                    if (returnShape == null)
                        hash.AbsorbTag(SignatureHash.TagVoid);
                    else
                    {
                        hash.AbsorbTag(SignatureHash.TagOutput);
                        returnShape.Absorb(returned, hash);
                    }
                }

                var returnRunning = hash.Running;

                // The receiver AFTER the call, on both paths. A struct method can write through its own
                // receiver, and that write is behaviour exactly as a returned value is - a void
                // Normalize() is ALL mutation, and without this its signature would hash its inputs and
                // nothing else. The throwing path is absorbed too: a body that half-wrote its receiver
                // and then threw did something observable, and leaving it out would hide precisely that.
                var receiverAfter = 0UL;
                if (receiverShape != null)
                {
                    hash.AbsorbTag(SignatureHash.TagReceiverAfter);
                    hash.RestartRunning();
                    receiverShape.Absorb(receiver, hash);
                    receiverAfter = hash.Running;
                    if (receiverAfter != receiverBefore)
                        outcome.Mutations++;
                }

                if (failure != null)
                {
                    // The abort condition depends only on what the method did, so both passes stop at the
                    // same iteration and their digests still describe the same experiment.
                    if (outcome.Threw == outcome.Total && outcome.Total >= FatalProbe && Array.IndexOf(FatalKinds, failure.GetType().FullName) >= 0)
                    {
                        outcome.AbortedAfter = outcome.Total;
                        break;
                    }

                    continue;
                }

                // "Did the answer ever change?", over the returned value - deliberately NOT over the
                // receiver. A pure getter leaves the receiver holding the fuzzed input, so folding that
                // in would make every instance method look like it varied and would switch this check
                // off on exactly the methods Tier 2 adds. Where there is no returned value the receiver
                // IS the answer, so there it is the thing compared.
                if (!returnedOnce)
                {
                    returnedOnce = true;
                    outcome.ConstantOutput = true;
                    firstReturn = returnRunning;
                    firstReceiver = receiverAfter;
                }
                else if (returnShape != null ? returnRunning != firstReturn : receiverAfter != firstReceiver)
                    outcome.ConstantOutput = false;
            }

            outcome.Hex = hash.ToHex(16);
        }

        return outcome;
    }

    private static LeafKind[] Flatten(ValueShape receiver, ValueShape[] shapes)
    {
        var kinds = new List<LeafKind>();

        // The receiver's leaves come first, so a method keeps its own inputs in a fixed order no matter
        // how many arguments are added around them.
        if (receiver != null)
            Collect(receiver, kinds);

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

        // The receiver is an input, so it is named in the key like one. Without it a static and an
        // instance overload taking the same arguments would file under a single identity, and Phase 2
        // could not tell from the key alone that it has a receiver to generate at all.
        var written = 0;
        if (!method.IsStatic)
        {
            builder.Append("this:");
            builder.Append(Normalise(method.DeclaringType));
            written++;
        }

        foreach (var parameter in method.GetParameters())
        {
            if (written++ > 0)
                builder.Append(',');

            builder.Append(Normalise(parameter.ParameterType));
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
