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
        public static void DecompileAssembly(byte[] assemblyBytes, Action<string, string> writeFile, string referenceDirectory = null)
        {
            if (assemblyBytes == null || assemblyBytes.Length == 0 || writeFile == null)
            {
                return;
            }

            // Without a resolver, an enum declared in another assembly cannot be resolved and the
            // decompiler prints the bare literal instead of naming it - `Rotate(v, 1)` rather than
            // `Rotate(v, Core.LogChannels.ALL)`. That does not compile, so 640 methods across 1,007 call
            // sites were being counted as recovery failures when the IL was correct and the measurement
            // was wrong. Every reference the recovered build needs sits in the directory it was written
            // to, which is why the caller passes it.
            ModuleContext context = null;

            if (!string.IsNullOrEmpty(referenceDirectory) && Directory.Exists(referenceDirectory))
            {
                AssemblyResolver resolver = new AssemblyResolver { EnableTypeDefCache = true };
                resolver.PreSearchPaths.Add(referenceDirectory);
                context = new ModuleContext(resolver);
                resolver.DefaultModuleContext = context;
            }

            ModuleDefMD module = context == null
                ? ModuleDefMD.Load(assemblyBytes)
                : ModuleDefMD.Load(assemblyBytes, context);
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

        // Regula 3 nu poate ajunge niciodata la forma "(Cast)(ref x)". Lookbehind-ul (?<![\w>\])]) a
        // fost pus ca sa apere un apel adevarat - Foo(ref x), Foo<T>(ref x) - dar ')' nu inchide numai
        // o lista de argumente, inchide si o conversie, asa ca paranteza castului activeaza garda si
        // expresia ramane nerescrisa. Numarat in exportul masurat: 690 de "(ref x)", dintre care 639
        // precedate de un caracter de identificator si 37 de '>' (apeluri reale, pe care nu avem voie sa
        // le atingem), 0 fara niciun prefix - deci regula 3 nu mai rescrie azi nimic - si exact 14
        // precedate de ')'. Cele 14 sunt fix cele 14 erori CS1525 "Invalid expression term 'ref'" si
        // toate au aceeasi forma, "(global::System.IntPtr)(ref x)".
        // De ce nu poate prinde un apel: tiparul cere DOUA grupuri de paranteze lipite, "(Tip)(ref x)",
        // iar paranteza deschisa a castului trebuie sa nu fie ea insasi precedata de [\w>\])]. Un apel
        // are un singur grup, iar la "Foo(a)(ref x)" grupul "(a)" e precedat de 'o', deci garda il sare;
        // la "((Func)d)(ref x)" continutul nu e un nume de tip, deci nu se potriveste deloc.
        // Scriem "default(Tip)", nu "default" simplu ca regula 3, ca sa pastram tipul static pe care il
        // dadea castul: e o expresie primara valida in orice context, fara sa depinda de inferarea
        // tipului tinta. Comparatia ramane una la rulare, nu o constanta, deci nu apare cod inaccesibil.
        private static readonly Regex RefCastValue = new Regex(
            @"(?<![\w>\])])\(((?:global::)?[A-Za-z_][A-Za-z0-9_.]*(?:<[A-Za-z0-9_.,:<> ]*>)?)\)\(ref \w+\)",
            RegexOptions.Compiled);

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

        // Cpp2IL's ConditionalJump pushes its condition and then branches on it. Where the branch target
        // cannot be resolved, IlGenerator rewrites the brtrue to a nop ("Branch target not in ISIL to IL
        // map") but leaves the push, so the condition is orphaned; NetSpy renders a popped value as the
        // expression itself (AstMethodBodyBuilder, `case ILCode.Pop: return arg1;`) and it lands as a
        // statement that is only a name - `flag7;` directly under the `flag7 = this._root == null;` that
        // produced it. That is CS0201, 1,301 errors over 360 methods and the biggest message shape left;
        // all 1,344 of these lines are one of ILSpy's generated bool locals, 98.6% under their own
        // assignment. Dropping the statement cannot change behaviour: an unqualified simple name here is
        // only ever a local or a parameter (NetSpy writes `this.`/the declaring type in front of fields and
        // properties), so there is nothing to evaluate, and the assignment above keeps the comparison the
        // native code really made. The keyword exclusions are load-bearing - `return;`, `break;`,
        // `continue;` and `throw;` are the only other statements with this one-word shape.
        // CORECTIE: propozitia de mai sus era falsa, si asta a rupt 1.172 de proprietati in 266 de
        // fisiere (2.344 din cele 2.416 linii de eroare ale rularii de referinta, toate CS1014 "A get
        // or set accessor expected"). Cand corpurile reale au inceput sa ajunga in C#, get_X/set_X au
        // devenit `return this.<X>k__BackingField;` si `this.<X>k__BackingField = value;`, adica exact
        // tiparul pe care PatternStatementTransform.TransformAutomaticProperties il cauta: el pune
        // property.Getter.Body = null si property.Setter.Body = null, iar CSharpOutputVisitor.VisitAccessor
        // scrie atunci numai cuvantul cheie si `;`. Un accesor care nu are modificator propriu ajunge
        // astfel pe o linie care e fix `<TAB>get;` - aceeasi forma cu a unui local orfan - deci regexul o
        // stergea si intre acolade ramaneau doar atributele [Token]/[Address], ceea ce strica fisierul
        // intreg. Cu stub-uri `return default(X);` tiparul nu se potrivea, accesorul isi pastra corpul
        // si nimic nu se strica; de aceea defectul a aparut abia dupa ce Level3 a inceput sa produca
        // logica reala. Dovada: in exportul masurat nu supravietuise niciun `get;`/`set;` in 2.675 de
        // fisiere, iar amprenta "linie de atribut urmata direct de }" apare de 1.139 ori in 258 de
        // fisiere. Excludem doar get si set: in tot NetSpy exista exact doua atribuiri `Body = null`,
        // ambele in TransformAutomaticProperties, deci add/remove/init nu pot aparea niciodata fara
        // corp si nu au ce cauta in lista - i-am scoate degeaba din curatarea locals-ilor orfani.
        private static readonly bool DropBareLocalStatements = Environment.GetEnvironmentVariable("CPP2IL_BARE_LOCAL") != "0";
        private static readonly Regex BareLocalStatement = new Regex(
            @"(?m)^[ \t]*(?!return\b|break\b|continue\b|throw\b|goto\b|yield\b|get[ \t]*;|set[ \t]*;)[a-z_][A-Za-z0-9_]*[ \t]*;[ \t]*\r?\n",
            RegexOptions.Compiled);

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
            // 3b) "(Cast)(ref local)" - aceeasi valoare, dar sub un cast, forma pe care garda regulii 3
            //     o sare. Vezi comentariul de la RefCastValue pentru numaratoare si pentru motivul
            //     pentru care tiparul nu poate atinge un apel.
            code = RefCastValue.Replace(code, "default(${1})");
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
            // 6) A statement that is only a local's name is the sibling of (1): a value the decompiler
            //    had nowhere to put. Reading a local has no side effects, so drop the whole line.
            if (DropBareLocalStatements)
                code = BareLocalStatement.Replace(code, string.Empty);
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
