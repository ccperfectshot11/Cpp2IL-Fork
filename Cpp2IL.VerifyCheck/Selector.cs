using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Cpp2IL.VerifyCheck;

// One recovered method, plus everything the static pass learned about it.
internal sealed class Candidate
{
    public string Key;                  // "<assembly>|0x06000123" - survives the fact that AsmResolver hands
                                        // out a different MethodDefinition instance per resolver cache
    public MethodDefinition Method;
    public string DllPath;
    public string AssemblyName;
    public string TypeName;
    public string MethodName;
    public uint Token;
    public bool IsStatic;
    public bool InvocableSignature;     // it can be CALLED: every input and the return value is primitive-only,
                                        // and there is somewhere for an answer to go
    public bool BodySafe;               // own body passed every local check
    public bool ReadsStatics;
    public bool ReadsStaticsTransitively;   // it, or something it calls, reads a static field
    public string ReceiverTypeName;     // Tier 2: the struct an instance method is invoked on
    public bool ReturnsVoid;
    public List<string> Callees = new();
    public string Reason = "";          // first check that said no, for the drop-reason table
}

internal sealed class SelectionResult
{
    public List<Candidate> All = new();
    public List<Candidate> Selected = new();          // invocable, whitelist closed over body-safe methods
    public int SelectedStaticOnly;                    // same, but the whitelist is static methods only
    public Dictionary<string, long> DropReasons = new();
    public long MethodsWithBody;
    public long DllsScanned;
    public long DllsUnreadable;
    public long DllsStubbed;                 // Cpp2IL never analysed these - see IsStubbedModule

    // Tier 2's other half, measured but not implemented: instance methods on CLASSES whose instance
    // fields are all primitive-only and which have a parameterless constructor. Counting them is what
    // says whether a heap receiver would be worth the risk it carries - see the note above Examine.
    public long ClassReceiverCandidates;
}

// Finds the methods that can be fuzzed at all.
//
// The bar is deliberately paranoid, because the alternative to paranoia here is a process that dies on an
// access violation halfway through a run: a selected method is invoked ten thousand times with hostile
// arguments, and a recovered body that reaches outside its own arguments will eventually be handed the
// bit pattern that makes it fault. Everything the method can reach has to be primitives and other
// selected methods, and nothing else.
internal static class Selector
{
    // The same list Cpp2IL.CompileCheck matches on, restated here rather than shared, because that tool
    // reads decompiled C# text and this one reads CIL - and duplicated with a warning is better than a
    // reference that drags Roslyn and NetSpy into a project that only needs metadata. Keep in sync with
    // Cpp2IL.CompileCheck/Program.cs::MarkerMessages.
    private static readonly string[] MarkerMessages =
    [
        "Unmanaged memory load", "Method not found @", "Indirect call:", "Indirect jump:",
        "Invalid instruction:", "Unknown instruction:", "Not implemented instruction:",
        "Unknown call target operand:", "Store into unknown operand:", "Stack shift:",
        "Non static method called without", "Phi opcodes should not exist",
    ];

    private static readonly HashSet<string> Primitives = new(StringComparer.Ordinal)
    {
        "System.Boolean", "System.Char", "System.SByte", "System.Byte", "System.Int16", "System.UInt16",
        "System.Int32", "System.UInt32", "System.Int64", "System.UInt64", "System.Single", "System.Double",
    };

    // Opcodes that can reach memory the harness does not control, or transfer control somewhere it cannot
    // see. calli and ldftn hand execution to an address; localloc/cpblk/initblk write through raw
    // pointers; newarr and newobj on a class allocate a heap graph the Phase 2 host could never
    // reproduce identically. A body containing any of them is not self-contained, whatever its signature
    // says.
    private static readonly HashSet<CilCode> ForbiddenCodes =
    [
        CilCode.Calli, CilCode.Jmp, CilCode.Ldftn, CilCode.Ldvirtftn, CilCode.Localloc,
        CilCode.Cpblk, CilCode.Initblk, CilCode.Newarr, CilCode.Arglist, CilCode.Mkrefany,
        CilCode.Refanyval, CilCode.Ldsflda, CilCode.Stsfld,
    ];

    public static SelectionResult Select(string dllDir, string filter, bool allowStatics)
    {
        var result = new SelectionResult();
        var dlls = Directory.GetFiles(dllDir, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(p => filter == null || Path.GetFileName(p).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(p => p)
            .ToArray();

        foreach (var dll in dlls)
        {
            ModuleDefinition module;
            try { module = ModuleDefinition.FromFile(dll); }
            catch { result.DllsUnreadable++; continue; }

            result.DllsScanned++;
            var assemblyName = module.Assembly?.Name?.Value ?? Path.GetFileNameWithoutExtension(dll);

            // Cpp2IL does not analyse these at all - AsmResolverDllOutputFormatIlRecovery.FillMethodBody
            // replaces every one of their methods with a stub - so `Mathf.Clamp` comes out as
            // `ldc.r4 0; ret`. Fuzzing a stub finds exactly what one would expect and reports it as a
            // method whose logic is missing, which is true and says nothing: 676 of the 757 methods this
            // tool once called "constant output" were stubs. They are not recovered code and do not belong
            // in a measurement of how well recovery works.
            if (IsStubbedModule(assemblyName))
            {
                result.DllsStubbed++;
                continue;
            }

            // Per module, not shared: the recovered build ships its own System.Xml and friends, so the
            // same type name can mean a different type one DLL over, and a shared cache would answer for
            // the wrong one.
            var safeTypes = new Dictionary<string, bool>(StringComparer.Ordinal);
            var classReceivers = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (var type in module.GetAllTypes())
            foreach (var method in type.Methods)
            {
                if (method.CilMethodBody is not { } body)
                    continue;

                result.MethodsWithBody++;

                // Resolution walks into whatever the recovered metadata points at, and that metadata was
                // generated from a native binary - a reference can be circular, self-contradictory, or
                // name an assembly that is already in the context under a different identity. One method
                // that cannot be analysed must not end a scan of a hundred and fifty DLLs.
                Candidate candidate;
                try { candidate = Examine(module, dll, assemblyName, type, method, body, safeTypes, classReceivers, allowStatics, result); }
                catch (Exception ex) { candidate = new Candidate { Key = assemblyName + "|analysis-error", TypeName = type.FullName, MethodName = method.Name?.Value ?? "", Reason = "analysis threw: " + ex.GetType().Name }; }

                result.All.Add(candidate);
                if (!candidate.BodySafe)
                    result.DropReasons[candidate.Reason] = result.DropReasons.GetValueOrDefault(candidate.Reason) + 1;
            }
        }

        // Greatest fixed point, not least: start with everything that passed its own checks and keep
        // removing whatever calls something no longer in the set. Building the set upwards from the
        // call-free methods instead would throw away every recursive and mutually recursive method,
        // and in a fixed-point maths library that is a lot of them.
        var closed = Close(result.All.Where(c => c.BodySafe));
        var closedStaticOnly = Close(result.All.Where(c => c.BodySafe && c.IsStatic));

        PropagateStatics(result.All);

        result.Selected = result.All.Where(c => c.InvocableSignature && closed.Contains(c.Key)).ToList();
        result.SelectedStaticOnly = result.All.Count(c => c.IsStatic && c.InvocableSignature && closedStaticOnly.Contains(c.Key));

        // Everything body-safe that still did not make it gets a reason too, so the drop table accounts
        // for every method with a body rather than only for the ones that failed their own checks.
        var selected = new HashSet<Candidate>(result.Selected);
        foreach (var dropped in result.All.Where(c => c.BodySafe && !selected.Contains(c)))
        {
            dropped.Reason = !closed.Contains(dropped.Key) ? "calls outside the whitelist" : "static but returns void";
            result.DropReasons[dropped.Reason] = result.DropReasons.GetValueOrDefault(dropped.Reason) + 1;
        }

        return result;
    }

    // A static read taints the CALLERS too. Without this a method whose own body is clean but which calls
    // FP.get_Value - and therefore depends on whatever the class constructor put in FPLut - is filed as
    // pure, and Phase 2 would compare it as if the two hosts' tables did not also have to match. The flag
    // exists to say which comparisons rest on a cctor, and one that stops at the first frame does not.
    private static void PropagateStatics(List<Candidate> all)
    {
        var byKey = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        foreach (var candidate in all)
        {
            candidate.ReadsStaticsTransitively = candidate.ReadsStatics;

            // Indexer, not Add: the analysis-error path files every failure in a module under one key.
            byKey[candidate.Key] = candidate;
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var candidate in all)
            {
                if (candidate.ReadsStaticsTransitively)
                    continue;

                foreach (var callee in candidate.Callees)
                {
                    if (!byKey.TryGetValue(callee, out var target) || !target.ReadsStaticsTransitively)
                        continue;

                    candidate.ReadsStaticsTransitively = true;
                    changed = true;
                    break;
                }
            }
        }
    }

    private static HashSet<string> Close(IEnumerable<Candidate> seed)
    {
        var byKey = seed.ToDictionary(c => c.Key, c => c);
        var live = new HashSet<string>(byKey.Keys, StringComparer.Ordinal);

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var key in live.ToArray())
            {
                foreach (var callee in byKey[key].Callees)
                {
                    if (live.Contains(callee))
                        continue;

                    live.Remove(key);
                    changed = true;
                    break;
                }
            }
        }

        return live;
    }

    private static Candidate Examine(ModuleDefinition module, string dll, string assemblyName, TypeDefinition type, MethodDefinition method, CilMethodBody body, Dictionary<string, bool> safeTypes, Dictionary<string, bool> classReceivers, bool allowStatics, SelectionResult result)
    {
        // AsmResolver 6 resolves references through the context that read the module, so cross-assembly
        // callees come back as definitions from the sibling DLLs in the same directory.
        var context = module.RuntimeContext;
        var token = method.MetadataToken.ToUInt32();
        var candidate = new Candidate
        {
            Key = assemblyName + "|0x" + token.ToString("X8"),
            Method = method,
            DllPath = dll,
            AssemblyName = assemblyName,
            TypeName = type.FullName,
            MethodName = method.Name?.Value ?? "",
            Token = token,
            IsStatic = method.IsStatic,
        };

        if (type.GenericParameters.Count > 0 || method.GenericParameters.Count > 0)
            return Fail(candidate, "generic");

        // A .cctor is not a function of its arguments - it is the thing that gives the statics their
        // values, and calling it twice is not the same as calling it once.
        if (method.IsConstructor && method.IsStatic)
            return Fail(candidate, "static constructor");

        var signature = method.Signature;
        if (signature == null)
            return Fail(candidate, "no signature");

        foreach (var parameterType in signature.ParameterTypes)
            if (!IsSafeValue(parameterType, safeTypes, 0, context))
                return Fail(candidate, "parameter is not primitive-only");

        // Tier 2. The receiver of an instance method on a primitive-only struct is one more value the
        // harness can generate from the same seed, so such a method is INVOKED rather than merely
        // tolerated as a callee. Any other receiver - a class, or a struct that reaches a reference -
        // would need a heap graph the Phase 2 host could not build identically, and stays out.
        if (!method.IsStatic)
        {
            if (!IsSafeValue(type.ToTypeSignature(), safeTypes, 0, context))
            {
                if (EligibleClassReceiver(type, safeTypes, classReceivers, context))
                    result.ClassReceiverCandidates++;

                return Fail(candidate, type.IsValueType ? "instance method on a non-primitive-only struct" : "instance method on a class");
            }

            candidate.ReceiverTypeName = type.FullName;
        }

        var returnType = signature.ReturnType;
        var returnsVoid = returnType.FullName == "System.Void";
        candidate.ReturnsVoid = returnsVoid;
        if (!returnsVoid && !IsSafeValue(returnType, safeTypes, 0, context))
            return Fail(candidate, "return type is not primitive-only");

        // A void STATIC has nowhere to put an answer, so a signature over one would hash its inputs and
        // nothing else and would agree between the two phases whatever the body did. A void INSTANCE
        // method does have somewhere: its receiver, which the fuzzer reads back after the call - a
        // Normalize() that returns nothing is all mutation. Whether it really writes there is a run-time
        // measurement rather than something to guess at here, and MethodFuzzer flags the ones that never
        // did as noObservableOutput so the comparison can drop them.
        candidate.InvocableSignature = !method.IsStatic || !returnsVoid;

        foreach (var local in body.LocalVariables)
            if (local.VariableType is PointerTypeSignature or FunctionPointerTypeSignature)
                return Fail(candidate, "pointer local");

        var instructions = body.Instructions;
        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            var code = instruction.OpCode.Code;

            if (ForbiddenCodes.Contains(code))
                return Fail(candidate, "opcode " + instruction.OpCode.Mnemonic);

            if (code is CilCode.Ldsfld)
            {
                if (!allowStatics)
                    return Fail(candidate, "reads a static field");

                // Not fatal, but it has to be recorded: a static read makes the result depend on whatever
                // ran the class constructor, and in Phase 2 that is the GAME's cctor, over the game's
                // lookup tables. FPMath is full of these (FPLut), so refusing them outright would throw
                // away the most interesting methods in the build - flagging them keeps them comparable
                // while making clear which comparisons rest on the tables matching too.
                candidate.ReadsStatics = true;
            }

            if (code is not (CilCode.Call or CilCode.Callvirt or CilCode.Newobj))
                continue;

            var callee = instruction.Operand as IMethodDescriptor;
            var calleeName = callee?.Name?.Value ?? "";

            // IlGenerator's own marker call, and the fallback it emits when the helper type was not
            // injected: the same message text goes to Console.WriteLine instead.
            if (calleeName is "NoteDecompilerIssue")
                return Fail(candidate, "decompiler marker");

            if (calleeName.StartsWith("NativeMethod_0x", StringComparison.Ordinal))
                return Fail(candidate, "unrecovered native method");

            if (calleeName is "WriteLine" && PrecedingMarkerString(instructions, i))
                return Fail(candidate, "decompiler marker");

            MethodDefinition resolved;
            try { resolved = callee?.Resolve(context); }
            catch { resolved = null; }

            if (resolved == null)
                return Fail(candidate, "unresolvable callee");

            var calleeAssembly = resolved.DeclaringType?.DeclaringModule?.Assembly?.Name?.Value ?? "";
            candidate.Callees.Add(calleeAssembly + "|0x" + resolved.MetadataToken.ToUInt32().ToString("X8"));
        }

        // A marker string can also survive on its own, when the issue was noted next to a call the
        // decompiler then dropped. Cheap to check, and missing one means fuzzing a body that has a hole
        // in it and reading the resulting signature as meaningful.
        foreach (var instruction in instructions)
            if (instruction.OpCode.Code == CilCode.Ldstr && instruction.Operand is string text && IsMarkerMessage(text))
                return Fail(candidate, "decompiler marker");

        candidate.BodySafe = true;
        return candidate;
    }

    private static bool PrecedingMarkerString(IList<CilInstruction> instructions, int callIndex)
    {
        for (var j = callIndex - 1; j >= 0 && j >= callIndex - 3; j--)
            if (instructions[j].OpCode.Code == CilCode.Ldstr && instructions[j].Operand is string text)
                return IsMarkerMessage(text);

        return false;
    }

    private static bool IsMarkerMessage(string text)
    {
        foreach (var message in MarkerMessages)
            if (text.IndexOf(message, StringComparison.Ordinal) >= 0)
                return true;

        return false;
    }

    // Primitive, or a struct made of them all the way down. Deliberately mirrors ValueShape.For in
    // Cpp2IL.VerifyCore: this decides what gets selected, that decides what can be generated, and if
    // they ever disagree a method is selected and then reported as unsupported at run time.
    private static bool IsSafeValue(TypeSignature signature, Dictionary<string, bool> cache, int depth, RuntimeContext context)
    {
        if (signature == null || depth > 8)
            return false;

        if (signature is CorLibTypeSignature)
            return Primitives.Contains(signature.FullName);

        if (signature is not TypeDefOrRefSignature)
            return false;

        var full = signature.FullName;
        if (cache.TryGetValue(full, out var known))
            return known;

        // Assume-safe while recursing: a struct that reaches itself is a recovered layout bug, and the
        // honest answer for it is "no", but the assumption keeps the recursion from running away before
        // it can give one.
        cache[full] = false;

        TypeDefinition definition;
        try { definition = signature.Resolve(context); }
        catch { return false; }

        if (definition == null || !definition.IsValueType || definition.IsEnum || definition.GenericParameters.Count > 0)
            return false;

        foreach (var field in definition.Fields)
        {
            if (field.IsStatic)
                continue;

            if (!IsSafeValue(field.Signature?.FieldType, cache, depth + 1, context))
                return false;
        }

        cache[full] = true;
        return true;
    }

    // Measurement only, deliberately not a selection path. Tier 2's other half would fuzz instance
    // methods on CLASSES, and this counts the ones that would even be eligible: a parameterless
    // constructor to allocate with, no base class to spread the fields over, and every instance field
    // primitive-only. It stops there because a heap receiver is not the same problem as a struct one -
    // it has to be built by RUNNING a recovered constructor, which is itself unverified code, and it
    // gives the method an object identity that the two hosts have no way to agree on. Knowing the size
    // of the prize is worth more than guessing at it.
    private static bool EligibleClassReceiver(TypeDefinition type, Dictionary<string, bool> cache, Dictionary<string, bool> classReceivers, RuntimeContext context)
    {
        var full = type.FullName;
        if (classReceivers.TryGetValue(full, out var known))
            return known;

        classReceivers[full] = false;
        if (type.IsValueType || type.IsAbstract || type.IsInterface || type.GenericParameters.Count > 0 || type.BaseType?.FullName != "System.Object")
            return false;

        var allocatable = false;
        foreach (var constructor in type.Methods)
            if (constructor.IsConstructor && !constructor.IsStatic && constructor.Signature?.ParameterTypes.Count == 0)
                allocatable = true;

        if (!allocatable)
            return false;

        foreach (var field in type.Fields)
            if (!field.IsStatic && !IsSafeValue(field.Signature?.FieldType, cache, 0, context))
                return false;

        classReceivers[full] = true;
        return true;
    }

    private static Candidate Fail(Candidate candidate, string reason)
    {
        candidate.Reason = reason;
        return candidate;
    }

    // The same list AsmResolverDllOutputFormatIlRecovery.FillMethodBody skips. Kept in step with it by
    // hand: they are in different projects, and a mismatch would silently measure stubs again.
    private static bool IsStubbedModule(string assemblyName) =>
        assemblyName.StartsWith("UnityEngine.", StringComparison.Ordinal)
        || assemblyName.StartsWith("Unity.", StringComparison.Ordinal)
        || assemblyName.StartsWith("System.", StringComparison.Ordinal)
        || assemblyName == "System"
        || assemblyName.StartsWith("mscorlib", StringComparison.Ordinal);

}
