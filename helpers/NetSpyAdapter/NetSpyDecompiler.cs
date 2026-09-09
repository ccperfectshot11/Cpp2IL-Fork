using System;
using System.IO;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Ast;
using NetSpy.Contracts.Decompiler;

namespace NetSpyAdapter
{
    public static class NetSpyDecompiler
    {
        public static void DecompileAssembly(byte[] assemblyBytes, Action<string, string> writeFile)
        {
            if (assemblyBytes == null || assemblyBytes.Length == 0 || writeFile == null)
            {
                return;
            }

            ModuleDefMD module = ModuleDefMD.Load(assemblyBytes);
            try
            {
                DecompilerSettings settings = new DecompilerSettings();
                // Cross-assembly ambiguity is not something FullyQualifyAmbiguousTypeNames catches: with
                // `using UnityEngine;` in scope, a bare `Object` is ambiguous against UnityEngine.Object,
                // and it is written that way 991 times. Qualifying everything is verbose but never wrong.
                settings.FullyQualifyAllTypes = true;

                foreach (TypeDef type in module.Types)
                {
                    if (type == null || type.FullName == "<Module>")
                    {
                        continue;
                    }

                    string code;
                    try
                    {
                        code = DecompileType(module, type, settings);
                    }
                    catch (Exception ex)
                    {
                        code = "// NetSpy decompile failed for " + type.FullName + ": " + ex.Message;
                    }

                    writeFile(BuildRelativePath(type), code);
                }
            }
            finally
            {
                // Free the loaded module so a batch run over 131 assemblies does not accumulate
                // gigabytes of dnlib metadata (the caller GC.Collects between assemblies).
                module.Dispose();
            }
        }

        private static string DecompileType(ModuleDef module, TypeDef type, DecompilerSettings settings)
        {
            DecompilerContext context = new DecompilerContext(settings.SettingsVersion, module)
            {
                Settings = settings,
            };
            AstBuilder astBuilder = new AstBuilder(context);
            astBuilder.AddType(type);
            astBuilder.RunTransformations();

            StringBuilderDecompilerOutput output = new StringBuilderDecompilerOutput();
            astBuilder.GenerateCode(output);
            return SanitizeInvalidConstructs(output.GetText());
        }

        // Some Cpp2IL IL has no valid C# form: an unresolved local address "(ref local)" that
        // NetSpy renders literally as a value, which does not compile. Rewrite these to a valid
        // placeholder so the whole file stays buildable.
        // The lookbehind (?<!\w) ensures we never touch a valid call/ctor argument list like
        // "Foo(ref x)" or "new T(ref x)" (there the '(' is preceded by an identifier char).
        // We only rewrite "(ref x)" used as a standalone value (preceded by an operator, '=', etc.).
        private static readonly Regex RefBareStatement = new Regex(@"(?m)^[ \t]*\(ref \w+\)\s*;[ \t]*\r?\n", RegexOptions.Compiled);
        private static readonly Regex RefArithmetic = new Regex(@"(?<![\w>\])])\(ref \w+\)\s*[-+]\s*\w+", RegexOptions.Compiled);
        private static readonly Regex RefValue = new Regex(@"(?<![\w>\])])\(ref \w+\)", RegexOptions.Compiled);

        // The injected Cpp2IL attributes carry [AttributeUsage(<int>, AllowMultiple = true)] where the
        // AttributeTargets argument decompiles to a bare int. That does not compile (no implicit int->
        // enum conversion), which disables AllowMultiple and turns every legitimate duplicate
        // application (e.g. two [Calls] on one method) into a CS0579 error. Casting the constant back
        // to the enum restores the whole thing. Matches AttributeUsage( or AttributeUsageAttribute(.
        private static readonly Regex AttributeUsageEnum = new Regex(@"(AttributeUsage(?:Attribute)?\(\s*)(\d+)", RegexOptions.Compiled);

        // Some injected Cpp2IL attributes (AttributeAttribute, ...) are injected as bare types with no
        // [AttributeUsage] at all, so they default to AllowMultiple = false. A type carrying several
        // original custom attributes then gets several [Attribute] applications -> CS0579. Give every
        // Cpp2ILInjected attribute class that lacks a usage a permissive one (AllowMultiple = true only
        // ever permits, never breaks a single application).
        private static readonly Regex InjectedAttrClass =
            new Regex(@"(?m)^([ \t]*)(public\s+(?:sealed\s+)?class\s+\w+\s*:\s*Attribute\b)", RegexOptions.Compiled);

        private static string SanitizeInvalidConstructs(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return code;
            }
            // 1) A whole statement that is just "(ref local);" is a no-op garbage statement -> drop it.
            code = RefBareStatement.Replace(code, string.Empty);
            // 2) "(ref local) - 64" / "+ 104" -> unresolved address arithmetic (replace the whole expr).
            code = RefArithmetic.Replace(code, "default");
            // 3) Any remaining "(ref local)" used as a value -> placeholder.
            code = RefValue.Replace(code, "default");
            // 4) [AttributeUsage(64, ...)] -> [AttributeUsage(AttributeTargets.All, ...)]. The bare int
            //    does not compile (disabling AllowMultiple -> CS0579 on duplicate [Calls]). We widen the
            //    target to All rather than cast the exact value: these injected attributes are just
            //    metadata, and a narrow target (e.g. Method-only) turns every misapplied injected
            //    attribute into a CS0592 once it starts being enforced.
            code = AttributeUsageEnum.Replace(code, "${1}AttributeTargets.All");
            // 5) An injected Cpp2ILInjected attribute class with no [AttributeUsage] -> give it a
            //    permissive one so multiple applications on one member don't become CS0579.
            if (code.Contains("namespace Cpp2ILInjected") && code.Contains(": Attribute") && !code.Contains("AttributeUsage"))
                code = InjectedAttrClass.Replace(code, "$1[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]\r\n$1$2", 1);
            return code;
        }

        private static string BuildRelativePath(TypeDef type)
        {
            string ns = type.Namespace?.String ?? string.Empty;
            string name = Sanitize(type.Name?.String ?? "Type");

            if (string.IsNullOrEmpty(ns))
            {
                return name + ".cs";
            }
            return ns.Replace('.', '/') + "/" + name + ".cs";
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "Type";
            }
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }
    }
}
