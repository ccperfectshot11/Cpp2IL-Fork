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
    public bool InvocableSignature;     // every parameter AND the return value is primitive-only, return is not void
    public bool BodySafe;               // own body passed every local check
    public bool ReadsStatics;
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

            // Per module, not shared: the recovered build ships its own System.Xml and friends, so the
            // same type name can mean a different type one DLL over, and a shared cache would answer for
            // the wrong one.
            var safeTypes = new Dictionary<string, bool>(StringComparer.Ordinal);

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
                try { candidate = Examine(module, dll, assemblyName, type, method, body, safeTypes, allowStatics); }
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

        result.Selected = result.All.Where(c => c.IsStatic && c.InvocableSignature && closed.Contains(c.Key)).ToList();
        result.SelectedStaticOnly = result.All.Count(c => c.IsStatic && c.InvocableSignature && closedStaticOnly.Contains(c.Key));

        // Everything body-safe that still did not make it gets a reason too, so the drop table accounts
        // for every method with a body rather than only for the ones that failed their own checks.
        var selected = new HashSet<Candidate>(result.Selected);
        foreach (var dropped in result.All.Where(c => c.BodySafe && !selected.Contains(c)))
        {
            dropped.Reason = !dropped.IsStatic ? "instance method (callable only as a callee)"
                : !dropped.InvocableSignature ? "static but returns void"
                : "calls outside the whitelist";
            result.DropReasons[dropped.Reason] = result.DropReasons.GetValueOrDefault(dropped.Reason) + 1;
        }

        return result;
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

    private static Candidate Examine(ModuleDefinition module, string dll, string assemblyName, TypeDefinition type, MethodDefinition method, CilMethodBody body, Dictionary<string, bool> safeTypes, bool allowStatics)
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

        // An instance method still qualifies as a whitelist entry - its `this` is a managed pointer to a
        // struct the harness owns, so it cannot reach further than a by-value copy would - but it is
        // never invoked directly, so its return type only has to be safe, not useful.
        if (!method.IsStatic && !IsSafeValue(type.ToTypeSignature(), safeTypes, 0, context))
            return Fail(candidate, "instance method on a non-primitive-only type");

        var returnType = signature.ReturnType;
        var returnsVoid = returnType.FullName == "System.Void";
        if (!returnsVoid && !IsSafeValue(returnType, safeTypes, 0, context))
            return Fail(candidate, "return type is not primitive-only");

        // A void static with primitive arguments has nowhere to put an answer, so a signature over it
        // would be a hash of its inputs and nothing else - it would match no matter what the body does.
        candidate.InvocableSignature = method.IsStatic && !returnsVoid;

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

    private static Candidate Fail(Candidate candidate, string reason)
    {
        candidate.Reason = reason;
        return candidate;
    }
}
