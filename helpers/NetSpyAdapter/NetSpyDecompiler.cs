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
            DecompilerSettings settings = new DecompilerSettings();

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
