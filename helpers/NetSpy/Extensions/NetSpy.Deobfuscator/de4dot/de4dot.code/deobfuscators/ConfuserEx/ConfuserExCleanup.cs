/*
    NetSpy addition: extra ConfuserEx cleanup passes that de4dot's built-in ConfuserEx module
    does not fully handle on modern builds:
      * reference-proxy "thin wrapper" calls  (smethod_X(a,b) => RealMethod(a,b))
      * anti-debug / anti-dump helper methods injected into <Module>

    The proxy-fixing approach mirrors imkk000/confuserex-unpacker-custom (ProxyCalls.cs): a proxy
    is a static method whose whole body just forwards its arguments to one real call and returns,
    so the call site can be rewritten to the real target and the proxy deleted.
*/

using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    public static class ConfuserExCleanup
    {
        /// <summary>
        /// Rewrites ConfuserEx reference-proxy calls to their real target and returns the proxy
        /// methods that are no longer referenced (safe to remove).
        /// </summary>
        public static List<MethodDef> FixProxyCalls(ModuleDef module)
        {
            var proxies = new HashSet<MethodDef>();
            foreach (var type in module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;
                    var instrs = method.Body.Instructions;
                    for (int i = 0; i < instrs.Count; i++)
                    {
                        if (instrs[i].OpCode != OpCodes.Call)
                            continue;
                        if (instrs[i].Operand is not MethodDef target || target == method)
                            continue;
                        if (!IsThinProxy(target, out var realOpCode, out var realOperand))
                            continue;
                        instrs[i].OpCode = realOpCode;
                        instrs[i].Operand = realOperand;
                        proxies.Add(target);
                    }
                }
            }
            // Only delete proxies that no longer have any remaining references in the module.
            var removable = new List<MethodDef>();
            foreach (var proxy in proxies)
                if (!IsReferenced(module, proxy))
                    removable.Add(proxy);
            return removable;
        }

        // A proxy forwards its N parameters straight into one call/callvirt/newobj and returns:
        //   ldarg.0 ; ldarg.1 ; ... ; call/callvirt/newobj Real ; ret   (exactly N+2 instructions)
        static bool IsThinProxy(MethodDef m, out OpCode opCode, out object operand)
        {
            opCode = null;
            operand = null;
            if (m == null || !m.IsStatic || !m.HasBody)
                return false;
            var ins = m.Body.Instructions;
            int n = m.Parameters.Count;
            if (ins.Count != n + 2)
                return false;
            var call = ins[ins.Count - 2];
            if (call.OpCode != OpCodes.Call && call.OpCode != OpCodes.Callvirt && call.OpCode != OpCodes.Newobj)
                return false;
            if (call.Operand is not IMethod)
                return false;
            if (ins[ins.Count - 1].OpCode != OpCodes.Ret)
                return false;
            for (int k = 0; k < n; k++)
                if (!ins[k].IsLdarg())
                    return false;
            opCode = call.OpCode;
            operand = call.Operand;
            return true;
        }

        static bool IsReferenced(ModuleDef module, MethodDef target) =>
            IsReferencedOutside(module, target, null);

        static bool IsReferencedOutside(ModuleDef module, MethodDef target, HashSet<MethodDef> exclude)
        {
            foreach (var type in module.GetTypes())
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;
                    if (exclude != null && exclude.Contains(method))
                        continue;
                    foreach (var instr in method.Body.Instructions)
                        if (instr.Operand == target)
                            return true;
                }
            return false;
        }

        /// <summary>
        /// Finds ConfuserEx anti-debug / anti-dump helper methods injected into &lt;Module&gt;,
        /// no-ops every call to them (including from the module cctor) and returns them for removal.
        /// </summary>
        public static List<MethodDef> RemoveAntiDebugDump(ModuleDef module)
        {
            var toRemove = new List<MethodDef>();
            var moduleType = module.GlobalType;
            if (moduleType == null)
                return toRemove;

            var antis = new HashSet<MethodDef>();
            foreach (var m in moduleType.Methods)
                if (m.HasBody && IsAntiDebugOrDump(m))
                    antis.Add(m);
            if (antis.Count == 0)
                return toRemove;

            foreach (var type in module.GetTypes())
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;
                    foreach (var instr in method.Body.Instructions)
                        if ((instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt) &&
                            instr.Operand is MethodDef md && antis.Contains(md))
                        {
                            instr.OpCode = OpCodes.Nop;
                            instr.Operand = null;
                        }
                }

            // Anti-debug methods spawn a monitoring thread of themselves via ldftn, so they
            // self-reference (and reference each other). Ignore references that originate from
            // within the anti-debug set itself when deciding whether they are still live.
            foreach (var m in antis)
                if (!IsReferencedOutside(module, m, antis))
                    toRemove.Add(m);
            return toRemove;
        }

        // ConfuserEx anti-debug bodies check Debugger.IsAttached / IsLogging and FailFast; anti-dump
        // bodies read the module's HINSTANCE / FullyQualifiedName. Match either signature.
        static bool IsAntiDebugOrDump(MethodDef m)
        {
            bool debuggerCheck = false, failFast = false, hinstance = false, fullyQualified = false;
            foreach (var instr in m.Body.Instructions)
            {
                if (instr.Operand is not IMethod called)
                    continue;
                var f = called.FullName;
                if (f.Contains("System.Diagnostics.Debugger::get_IsAttached") ||
                    f.Contains("System.Diagnostics.Debugger::IsLogging"))
                    debuggerCheck = true;
                else if (f.Contains("System.Environment::FailFast"))
                    failFast = true;
                else if (f.Contains("Marshal::GetHINSTANCE"))
                    hinstance = true;
                else if (f.Contains("Module::get_FullyQualifiedName"))
                    fullyQualified = true;
            }
            return (debuggerCheck && failFast) || (hinstance && fullyQualified);
        }
    }
}
