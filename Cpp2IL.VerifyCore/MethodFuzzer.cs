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
//
// Cheia nu este numele niciuneia dintre parti, ci o FORMA CANONICA spre care converg amandoua. Fiecare
// regula de mai jos este o ingrosare: trimite mai multe siruri vechi intr-unul nou si niciodata un sir
// vechi in doua. De aici iese proprietatea care conteaza cand se umbla la normalizare - doua chei care
// se potriveau inainte se potrivesc si dupa, deci NICIO pereche formata nu se poate pierde. Singurul
// pret posibil sunt perechile GRESITE, cand doua metode diferite ajung pe aceeasi cheie; de aceea cele
// doua indexuri (VerifyMod.IndexMember si RecoveredCode.Index) scot din pereche cheia ciocnita in loc
// sa pastreze prima venita.
public static class MethodKeys
{
    /// <summary>
    /// Forma cheii SI a ce se scrie despre ea, ca sa se vada din afara ca un fisier scris mai demult nu
    /// mai este citibil. Se schimba odata cu orice regula de normalizare care muta cheile, si odata cu
    /// orice camp nou pe care dump-ul il scrie despre o cheie: cine tine chei pe disc - dump-ul de
    /// cerinte al fazei 4, fisierele de semnaturi ale fazei 1 - le compara cu semnul asta si le reface,
    /// in loc sa porneasca jocul degeaba peste un fisier care nu mai raspunde la intrebarea pusa.
    ///
    /// -2: dump-ul deosebeste acum "cheia nu exista in indexul jocului" de "cheia a iesit din pereche
    ///     fiindca doua metode diferite ale jocului au cazut pe ea". Cheile insele nu s-au mutat fata
    ///     de -1, dar raspunsul la "cat ne costa ciocnirile" se citeste numai dintr-un dump nou.
    /// </summary>
    public const string FormatVersion = "w23-mangle-2";

    public static string For(MethodBase method)
    {
        var builder = new StringBuilder();
        builder.Append(Normalise(method.DeclaringType));
        builder.Append("::");
        builder.Append(NormaliseMemberName(method.Name));
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

    // Citit o singura data: Normalise sta pe drumul fierbinte al indexarii din faza 2, unde este chemat
    // de cateva sute de mii de ori. De aceea rezultatul se tine intr-un dictionar - aceleasi cateva zeci
    // de tipuri (System.String, UnityEngine.Vector3, tipul declarant) revin la fiecare metoda.
    private static readonly bool StripGlobalNamespace =
        Environment.GetEnvironmentVariable("CPP2IL_VERIFY_IL2CPP_GLOBAL_NS") != "0";

    private static readonly Dictionary<Type, string> Cache = new Dictionary<Type, string>();

    // Namespace-ul in care Il2CppInterop isi tine invelisurile de tablou. Verificarea se face pe el, nu
    // numai pe numele tipului, ca un tip AL JOCULUI numit din intamplare "Il2CppStructArray" sa nu fie
    // citit drept tablou.
    private const string InteropArrays = "Il2CppInterop.Runtime.InteropTypes.Arrays";

    private static string Normalise(Type type)
    {
        if (type == null)
            return "void";

        lock (Cache)
        {
            if (Cache.TryGetValue(type, out var cached))
                return cached;
        }

        var builder = new StringBuilder();
        Append(builder, type);
        var name = builder.ToString();

        lock (Cache)
        {
            Cache[type] = name;
        }

        return name;
    }

    private static void Append(StringBuilder builder, Type type)
    {
        if (type.IsByRef)
        {
            Append(builder, type.GetElementType());
            builder.Append('&');
            return;
        }

        if (type.IsPointer)
        {
            Append(builder, type.GetElementType());
            builder.Append('*');
            return;
        }

        if (type.IsArray)
        {
            Append(builder, type.GetElementType());
            builder.Append('[');
            builder.Append(',', type.GetArrayRank() - 1);
            builder.Append(']');
            return;
        }

        // Interopul nu are tablouri: un "byte[]" al jocului ajunge Il2CppStructArray<byte>, un "Foo[]"
        // ajunge Il2CppReferenceArray<Foo>, iar un "string[]" ajunge Il2CppStringArray, care nici macar
        // nu este generic. Aici este singurul loc unde DESFACEM ce a facut interopul in loc sa aplicam
        // noi aceeasi stalcire, si asta fiindca aici desfacerea nu este ambigua: invelisul spune el
        // insusi ce avea inauntru, iar namespace-ul spune ca este invelisul interopului si nu un tip al
        // jocului.
        var element = InteropArrayElement(type);
        if (element != null)
        {
            Append(builder, element);
            builder.Append("[]");
            return;
        }

        // Un parametru generic nelegat (T) nu are FullName deloc, doar Name.
        if (type.IsGenericParameter)
        {
            builder.Append(type.Name);
            return;
        }

        // Type.FullName scrie instantierea generica CALIFICATA CU ASSEMBLY: la noi
        // "List`1[[Foo, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null]]".
        // Inauntrul parantezelor sta un nume de tip caruia nimeni nu ii taie prefixul Il2Cpp, fiindca
        // taietura se uita numai la inceputul sirului. Reconstruim instantierea din bucati, ca fiecare
        // argument sa treaca prin aceeasi normalizare ca un tip de nivel intai - si scapam pe drum de
        // assembly, versiune, cultura si cheie publica, care nu spun nimic despre forma.
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            AppendName(builder, type.GetGenericTypeDefinition());
            builder.Append('<');

            var arguments = type.GetGenericArguments();
            for (var i = 0; i < arguments.Length; i++)
            {
                if (i > 0)
                    builder.Append(',');

                Append(builder, arguments[i]);
            }

            builder.Append('>');
            return;
        }

        AppendName(builder, type);
    }

    private static Type InteropArrayElement(Type type)
    {
        if (!string.Equals(type.Namespace, InteropArrays, StringComparison.Ordinal))
            return null;

        if (string.Equals(type.Name, "Il2CppStringArray", StringComparison.Ordinal))
            return typeof(string);

        if (!type.IsGenericType || type.IsGenericTypeDefinition)
            return null;

        if (!string.Equals(type.Name, "Il2CppStructArray`1", StringComparison.Ordinal) &&
            !string.Equals(type.Name, "Il2CppReferenceArray`1", StringComparison.Ordinal))
            return null;

        var arguments = type.GetGenericArguments();
        return arguments.Length == 1 ? arguments[0] : null;
    }

    // Numele unui tip, pe bucati: intai lantul de tipuri declarante, apoi numele propriu. Se construieste
    // asa si nu din FullName fiindca stalcirea trebuie sa cada pe FIECARE nume in parte - aplicata peste
    // FullName intreg ar sterge si punctele care despart namespace-ul, si nu mai ramane nimic de potrivit.
    private static void AppendName(StringBuilder builder, Type type)
    {
        var declaring = type.IsNested ? type.DeclaringType : null;
        if (declaring != null)
        {
            AppendName(builder, declaring);
            builder.Append('+');
            builder.Append(Mangle(type.Name));
            return;
        }

        // Un tip FARA namespace in joc nu ajunge fara namespace in interop: Il2CppInterop il pune in
        // namespace-ul "Il2Cpp", deci SRMath vine ca "Il2Cpp.SRMath". Taind doar cele sase litere ramane
        // ".SRMath" - cu punctul in fata - iar faza 1 scrie "SRMath", asa ca perechea nu se formeaza
        // niciodata si metoda cade tacut in "only in phase1".
        //
        // Masurat pe cele doua fisiere reale: din 247 de metode la care faza 2 nu a raspuns, 185 sunt
        // tipuri fara namespace, si ZERO din cele 978 la care a raspuns sunt. Nu e o coincidenta - este
        // rata de pierdere 100%. In build-ul jocului sunt 1.603 tipuri puse in namespace-ul "Il2Cpp",
        // iar cu punctul taiat toate cele 1.761 de enum-uri recuperate se potrivesc pe nume cu ale
        // jocului, ceea ce confirma forma taieturii. Niciun tip recuperat nu incepe cu "Il2Cpp", deci
        // faza 1 nu se misca deloc.
        var ns = type.Namespace ?? "";
        var name = ns.Length > 0 ? ns + "." + Mangle(type.Name) : Mangle(type.Name);

        if (StripGlobalNamespace && name.StartsWith("Il2Cpp.", StringComparison.Ordinal))
            name = name.Substring("Il2Cpp.".Length);
        else if (name.StartsWith("Il2Cpp", StringComparison.Ordinal))
            name = name.Substring("Il2Cpp".Length);

        builder.Append(name);
    }

    // Il2CppInterop scrie C#, deci nu poate pastra un nume care contine '<', '>' sau '.': le inlocuieste
    // pe toate cu '_'. Citit din tabela de siruri a lui Assembly-CSharp.dll din Il2CppAssemblies, unde
    // tipul "<Start>d__14" se cheama "_Start_d__14", "<>c__DisplayClass0_0" se cheama
    // "__c__DisplayClass0_0", "<PrivateImplementationDetails>" se cheama "_PrivateImplementationDetails_",
    // iar implementarea explicita de interfata "System.Collections.IEnumerator.Reset" se cheama
    // "System_Collections_IEnumerator_Reset". Formele cu paranteze unghiulare SE VAD si ele in fisier,
    // dar numai ca argument al atributului OriginalName, care pastreaza numele dinainte de redenumire -
    // cine le cauta cu un grep peste DLL le gaseste si crede ca tipurile n-au fost redenumite.
    //
    // Stalcim NOI la fel in loc sa desfacem stalcirea lor, fiindca desfacerea ar fi o ghiceala: din
    // "_A_b__1" nu se mai poate sti unde era '<'. Aplicarea, in schimb, este o functie. Pe partea
    // jocului regula este oricum fara efect - acolo nu mai exista niciun '<', '>' sau '.' de inlocuit.
    private static string Mangle(string name)
    {
        if (name == null)
            return "";

        if (name.IndexOf('<') < 0 && name.IndexOf('>') < 0 && name.IndexOf('.') < 0)
            return name;

        var characters = name.ToCharArray();
        for (var i = 0; i < characters.Length; i++)
            if (characters[i] == '<' || characters[i] == '>' || characters[i] == '.')
                characters[i] = '_';

        return new string(characters);
    }

    // Numele unei metode trece prin aceeasi stalcire, cu o singura scutire: ".ctor" si ".cctor" sunt
    // nume pe care si reflectia jocului le da tot asa, deci n-au ce castiga din inlocuire si ar putea
    // doar sa se ciocneasca cu o metoda chemata chiar "_ctor".
    private static string NormaliseMemberName(string name)
    {
        if (name == ".ctor" || name == ".cctor")
            return name;

        return Mangle(name);
    }
}
