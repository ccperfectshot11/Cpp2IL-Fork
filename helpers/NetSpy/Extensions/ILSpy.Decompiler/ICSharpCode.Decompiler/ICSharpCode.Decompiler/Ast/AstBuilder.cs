// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using NetSpy.Contracts.Decompiler;
using NetSpy.Contracts.Text;
using ICSharpCode.Decompiler.Ast.Transforms;
using ICSharpCode.Decompiler.ILAst;
using ICSharpCode.NRefactory.CSharp;
using ICSharpCode.NRefactory.Utils;

namespace ICSharpCode.Decompiler.Ast {
	using Ast = ICSharpCode.NRefactory.CSharp;
	using VarianceModifier = ICSharpCode.NRefactory.TypeSystem.VarianceModifier;

	[Flags]
	public enum ConvertTypeOptions
	{
		None = 0,
		IncludeNamespace = 1,
		IncludeTypeParameterDefinitions = 2,
		DoNotUsePrimitiveTypeNames = 4,
		DoNotIncludeEnclosingType = 8,
	}

	public enum DecompiledBodyKind {
		/// <summary>
		/// Decompile the body
		/// </summary>
		Full,

		/// <summary>
		/// Create an empty body, but add extra statements if necessary in order for the code to compile.
		/// </summary>
		Empty,

		/// <summary>
		/// Don't use a body
		/// </summary>
		None,
	}

	enum MethodKind {
		Method,
		Property,
		Event,
	}

	public sealed class AstBuilder
	{
		public DecompilerContext Context {
			get { return context; }
		}
		readonly DecompilerContext context;
		SyntaxTree syntaxTree;
		readonly Dictionary<string, NamespaceDeclaration> astNamespaces = new Dictionary<string, NamespaceDeclaration>();
		bool transformationsHaveRun;
		readonly StringBuilder stringBuilder;// PERF: prevent extra created strings
		readonly char[] commentBuffer;// PERF: prevent extra created strings
		readonly List<Task<AsyncMethodBodyResult>> methodBodyTasks = new List<Task<AsyncMethodBodyResult>>();
		readonly List<AsyncMethodBodyDecompilationState> asyncMethodBodyDecompilationStates = new List<AsyncMethodBodyDecompilationState>();
		internal AutoPropertyProvider AutoPropertyProvider { get; } = new AutoPropertyProvider();
		readonly List<Comment> comments = new List<Comment>();

		struct AsyncMethodBodyResult {
			public readonly EntityDeclaration MethodNode;
			public readonly MethodDef Method;
			public readonly BlockStatement Body;
			public readonly MethodDebugInfoBuilder Builder;
			public readonly FieldToVariableMap VariableMap;
			public readonly bool CurrentMethodIsAsync;
			public readonly bool CurrentMethodIsYieldReturn;

			public AsyncMethodBodyResult(EntityDeclaration methodNode, MethodDef method, BlockStatement body, MethodDebugInfoBuilder builder, FieldToVariableMap variableMap, bool currentMethodIsAsync, bool currentMethodIsYieldReturn) {
				this.MethodNode = methodNode;
				this.Method = method;
				this.Body = body;
				this.Builder = builder;
				this.VariableMap = variableMap;
				this.CurrentMethodIsAsync = currentMethodIsAsync;
				this.CurrentMethodIsYieldReturn = currentMethodIsYieldReturn;
			}
		}
		sealed class AsyncMethodBodyDecompilationState {
			public readonly StringBuilder StringBuilder = new StringBuilder();
		}

		AsyncMethodBodyDecompilationState GetAsyncMethodBodyDecompilationState() {
			lock (asyncMethodBodyDecompilationStates) {
				if (asyncMethodBodyDecompilationStates.Count > 0) {
					var state = asyncMethodBodyDecompilationStates[asyncMethodBodyDecompilationStates.Count - 1];
					asyncMethodBodyDecompilationStates.RemoveAt(asyncMethodBodyDecompilationStates.Count - 1);
					return state;
				}
			}
			return new AsyncMethodBodyDecompilationState();
		}

		void Return(AsyncMethodBodyDecompilationState state) {
			lock (asyncMethodBodyDecompilationStates)
				asyncMethodBodyDecompilationStates.Add(state);
		}

		// "0x" + hexChars(uint)
		const int COMMENT_BUFFER_LENGTH = 2 + 8;

		public Func<AstBuilder, MethodDef, DecompiledBodyKind> GetDecompiledBodyKind { get; set; }

		public AstBuilder(DecompilerContext context)
		{
			if (context == null)
				throw new ArgumentNullException("context");
			this.context = context;
			this.stringBuilder = new StringBuilder();
			this.commentBuffer = new char[COMMENT_BUFFER_LENGTH];
			this.syntaxTree = new SyntaxTree();
			this.transformationsHaveRun = false;
			this.GetDecompiledBodyKind = null;
		}

		public void Reset()
		{
			this.GetDecompiledBodyKind = null;
			this.syntaxTree = new SyntaxTree();
			this.transformationsHaveRun = false;
			this.astNamespaces.Clear();
			this.stringBuilder.Clear();
			this.context.Reset();
			this.AutoPropertyProvider.Reset();
			this.methodBodyTasks.Clear();
		}

		void WaitForBodies() {
			if (methodBodyTasks.Count == 0)
				return;
			try {
				for (int i = 0; i < methodBodyTasks.Count; i++) {
					var result = methodBodyTasks[i].GetAwaiter().GetResult();
					context.CancellationToken.ThrowIfCancellationRequested();
					if (result.CurrentMethodIsAsync)
						result.MethodNode.Modifiers |= Modifiers.Async;
					result.MethodNode.SetChildByRole(Roles.Body, result.Body);
					result.MethodNode.AddAnnotation(result.Builder);
					result.MethodNode.AddAnnotation(result.VariableMap);
					ConvertAttributes(result.MethodNode, result.Method, result.CurrentMethodIsAsync, result.CurrentMethodIsYieldReturn);

					comments.Clear();
					comments.AddRange(result.MethodNode.GetChildrenByRole(Roles.Comment));
					for (int j = comments.Count - 1; j >= 0; j--) {
						var c = comments[j];
						c.Remove();
						result.MethodNode.InsertChildAfter(null, c, Roles.Comment);
					}
				}
			}
			finally {
				methodBodyTasks.Clear();
			}
		}

		// Cpp2IL: a nested closure type (<>c__DisplayClassN_M, <>c) is hidden below because DelegateConstruction
		// is expected to fold it away - the closure local becomes ordinary locals and the lambda bodies move back
		// into the parent method, leaving nothing to declare. On Cpp2IL bodies that fold almost never fires: the
		// block transform bails unless the closure local is used for nothing but field access, and a recovered
		// body carries dead locals and unrecovered stores that break the shape. Measured on the emitted DLLs, 432
		// of the 483 display classes are still written by name in the output and not one of the 622 closure types
		// is declared, so every use resolves against nothing - CS0426, 2,053 errors over 867 methods. Emitting the
		// declaration makes both sides agree without touching metadata: IdentifierEscaper already maps '<' and '>'
		// to '_' at every use and does the same in the declaration, so the printed names match.
		//
		// The members stay hidden, and that is the point. The recompile denominator is the number of method
		// declarations in the decompiled C#, so emitting the 885 lambdas, 622 constructors and 139 static
		// constructors these types carry would add them to it and make every percentage incomparable with the
		// project's earlier numbers - the same inflation (16,676 -> 18,390) that made renaming the types harmful.
		// The lambdas are folded into their parent method anyway, so declaring them would also duplicate them.
		// Fields stay visible: they are what the surviving uses actually reference.
		//
		// Safe because closure types are structurally trivial - measured over both Assembly-CSharp DLLs: none
		// implements an interface (so hiding the members cannot produce CS0535), none has properties, events or
		// nested types, all derive from Object, all 622 constructors are parameterless so the implicit default
		// constructor stands in for the hidden one, all are nested public, and no escaped name collides with a
		// sibling nested type or with a member of the parent.
		//
		// State machines (<X>d__N) cannot be declared this way - they implement IEnumerator/IAsyncStateMachine, so
		// declaring them with hidden members trades CS0426 for CS0535. They get abstract member stubs instead, see
		// StateMachineStubs below.
		static readonly bool ClosureDeclarations = Environment.GetEnvironmentVariable("CPP2IL_CLOSURE_DECL") != "0";

		// Cpp2IL: the same hole as ClosureDeclarations above, one level worse. An iterator's state machine
		// (<X>d__N) is hidden because YieldReturnDecompiler is expected to fold it back into the yield-return
		// method it came from; that fold starts by matching the `newobj <X>d__N::.ctor(int)` in the parent, and
		// over both Assembly-CSharp DLLs the recovered bodies contain exactly zero of them - il2cpp's object_new
		// plus ctor pair comes back as a plain null. The 424 parent methods that touch a state machine reach it
		// only through 1,115 stfld and 435 initobj, so the fold cannot fire on a single one of them, and MoveNext
		// is nowhere near the pattern either (goto IL_xxxx, unrecovered-instruction markers). The type is
		// therefore hidden while `<X>d__N loc = null; loc.__4__this = this; return loc;` is still printed:
		// CS0426, 427 methods, 335 of them with no other complaint.
		//
		// Declaring it the way closures are declared - type visible, members hidden - does not work here, because
		// a state machine implements IEnumerator/IEnumerator<T>/IDisposable: hidden members mean CS0535, which is
		// a class-level error, so the whole file including the parent's own methods goes structurally broken.
		// Measured: STRICT 11,363 -> 10,019. Dropping the interface list instead makes `return loc;` a CS0029 and
		// costs 191. Declaring the members for real satisfies the interfaces but puts their 1,414 declarations in
		// the recompile denominator (16,677 -> 18,091), the same inflation that made renaming the closure types
		// harmful, and most of those declarations are trivia (an empty Dispose, a Reset that throws, two Current
		// getters) that would compile for free and flatter the percentage.
		//
		// So the members are emitted as abstract declarations: they satisfy the interfaces, they carry no body,
		// and a body is exactly what the denominator counts - CompileCheck measures method declarations that have
		// one. Nothing recovered is added to either side of the ratio, and nothing recovered is lost: the fields
		// stay, and they are what the parent actually writes to (__1__state and __4__this on nearly every use,
		// plus the captured parameters - delay, url, sceneName ...). `abstract` contradicts the `sealed` in the
		// metadata, but no use site can tell: with zero newobj recovered there is nothing left constructing one.
		//
		// Only the canonical iterator shape is stubbed. IEnumerator<object> collapses onto IEnumerator's own
		// Current, so one `object Current` implements both; an IEnumerator<T> with a real T, or an IEnumerable<T>
		// pair of GetEnumerator overloads, would need two members differing only in return type, which C# cannot
		// express implicitly - those stay hidden. Async state machines stay hidden too: they are structs, so they
		// cannot be abstract, and declaring them drags in dnlib's synthesized [StructLayout], whose attribute type
		// this stripped reference set does not have - 42 class-level CS0234 that block 407 methods on their own.
		// Measured: STRICT 11,363 (68,1%) -> 11,671 (70,0%), denominator 16,677 unchanged, class-level errors 143
		// unchanged, methods in structurally-broken types 447 unchanged. CS0426 drops from 427 methods to 21, the
		// remainder being the async and generic-argument shapes left hidden on purpose.
		static readonly bool StateMachineStubs = Environment.GetEnvironmentVariable("CPP2IL_SM_DECL") != "0";

		static bool IsStubbableIterator(TypeDef type)
		{
			if (!StateMachineStubs || type.DeclaringType == null || DnlibExtensions.IsValueType(type) || !type.IsCompilerGenerated())
				return false;
			bool hasEnumerator = false;
			for (int i = 0; i < type.Interfaces.Count; i++) {
				var iface = type.Interfaces[i].Interface;
				if (iface == null)
					return false;
				switch (iface.FullName) {
				case "System.Collections.IEnumerator":
					hasEnumerator = true;
					break;
				case "System.Collections.Generic.IEnumerator`1<System.Object>":
				case "System.IDisposable":
					break;
				default:
					return false;
				}
			}
			return hasEnumerator;
		}

		static void AddIteratorStubs(TypeDeclaration astType)
		{
			var current = new PropertyDeclaration();
			current.Modifiers = Modifiers.Public | Modifiers.Abstract;
			current.ReturnType = new PrimitiveType("object");
			current.NameToken = Identifier.Create("Current");
			current.Getter = new Accessor();
			astType.Members.Add(current);
			astType.Members.Add(StubMethod("MoveNext", new PrimitiveType("bool")));
			astType.Members.Add(StubMethod("Reset", new PrimitiveType("void")));
			astType.Members.Add(StubMethod("Dispose", new PrimitiveType("void")));
		}

		static MethodDeclaration StubMethod(string name, AstType returnType)
		{
			var astMethod = new MethodDeclaration();
			astMethod.Modifiers = Modifiers.Public | Modifiers.Abstract;
			astMethod.ReturnType = returnType;
			astMethod.NameToken = Identifier.Create(name);
			return astMethod;
		}

		// [TupleElementNames] has no C# syntax at all - writing it is CS8138 - so the three state machine fields
		// that carry one would each turn their whole file structurally broken the moment the type is declared.
		// Dropping it loses the element names only; the field keeps the ValueTuple type it is actually used as.
		static void RemoveUnwritableAttributes(EntityDeclaration decl)
		{
			foreach (var section in decl.Attributes.ToArray()) {
				foreach (var attr in section.Attributes.ToArray()) {
					var attrType = attr.Type.Annotation<ITypeDefOrRef>();
					if (attrType != null && attrType.FullName == "System.Runtime.CompilerServices.TupleElementNamesAttribute")
						attr.Remove();
				}
				if (section.Attributes.Count == 0)
					section.Remove();
			}
		}

		public static bool MemberIsHidden(IMemberRef member, DecompilerSettings settings)
		{
			MethodDef method = member as MethodDef;
			if (method != null) {
				if (method.IsGetter || method.IsSetter || method.IsAddOn || method.IsRemoveOn)
					return true;
				if (settings.ForceShowAllMembers)
					return false;
				// Cpp2IL: the closure type itself is now declared (see ClosureDeclarations) but its members are not,
				// so nothing new lands in the decompiled output. Constructors and generated-name lambdas only - the
				// one closure member that is neither is a lambda whose name IL2CPP stripped, and the decompiler does
				// print a method group reference to it, so hiding it too would only trade CS0426 for CS1061.
				if (ClosureDeclarations && settings.AnonymousMethods && method.DeclaringType != null &&
					IsClosureType(method.DeclaringType) && (method.IsConstructor || method.HasGeneratedName()))
					return true;
				// Cpp2IL: a stubbed iterator keeps its fields and its four abstract members (see StateMachineStubs);
				// its real methods - MoveNext, the explicit Dispose/Reset/Current, the ctor - stay out of the output
				// so that no body of theirs ever lands in the recompile denominator.
				if (method.DeclaringType != null && IsStubbableIterator(method.DeclaringType))
					return true;
				if (settings.AnonymousMethods) {
					if (method.Name.StartsWith("_Lambda$__") && method.IsCompilerGenerated())
						return true;
					if (method.HasGeneratedName() && method.IsCompilerGenerated())
						return !method.Name.Contains(">g__");
				}
				return false;
			}

			TypeDef type = member as TypeDef;
			if (type != null) {
				if (settings.ForceShowAllMembers)
					return false;
				if (type.DeclaringType != null) {
					if (settings.AnonymousMethods && IsClosureType(type) && !ClosureDeclarations)
						return true;
					if (settings.YieldReturn && YieldReturnDecompiler.IsCompilerGeneratorEnumerator(type) && !IsStubbableIterator(type))
						return true;
					if (settings.AsyncAwait && AsyncDecompiler.IsCompilerGeneratedStateMachine(type))
						return true;
					if (type.IsDynamicCallSiteContainerType())
						return true;
				} else if (type.IsCompilerGenerated()) {
					if (type.Name.StartsWith("<PrivateImplementationDetails>", StringComparison.Ordinal))
						return true;
					if (type.IsAnonymousType())
						return true;
				}
				return false;
			}

			PropertyDef prop = member as PropertyDef;
			if (prop != null) {
				if (settings.ForceShowAllMembers)
					return false;
				// Cpp2IL: Current is replaced by the abstract stub, so the real one has to go. A state machine has no
				// other property, and nothing else reaches this branch, so no other type changes shape because of it.
				return prop.DeclaringType != null && IsStubbableIterator(prop.DeclaringType);
			}

			FieldDef field = member as FieldDef;
			if (field != null) {
				if (settings.ForceShowAllMembers)
					return false;
				if (field.IsCompilerGenerated()) {
					if (settings.AnonymousMethods && IsAnonymousMethodCacheField(field))
						return true;
					if (settings.AutomaticProperties && IsAutomaticPropertyBackingField(field))
						return true;
					if (settings.SwitchStatementOnString && IsSwitchOnStringCache(field))
						return true;
				}
				// event-fields are not [CompilerGenerated]
				if (settings.AutomaticEvents) {
					string fieldName = field.Name;
					for (int i = 0; i < field.DeclaringType.Events.Count; i++) {
						if (IsEventBackingFieldName(fieldName, field.DeclaringType.Events[i].Name))
							return true;
					}
				}
				return false;
			}

			return false;
		}

		internal static bool IsEventBackingFieldName(string fieldName, string eventName) {
			if (fieldName == eventName)
				return true;

			const string VB_PATTERN = "Event";
			if (fieldName.Length == VB_PATTERN.Length + eventName.Length && fieldName.StartsWith(eventName, StringComparison.Ordinal) && fieldName.EndsWith(VB_PATTERN, StringComparison.Ordinal))
				return true;

			return false;
		}

		static bool IsSwitchOnStringCache(FieldDef field)
		{
			return field.Name.StartsWith("<>f__switch", StringComparison.Ordinal);
		}

		static bool IsAutomaticPropertyBackingField(FieldDef field)
		{
			string name = field.Name;
			if (string.IsNullOrEmpty(name))
				return false;
			// VB's auto prop backing fields are named "_" + PropertyName
			if (name[0] == '_') {
				for (int i = 0; i < field.DeclaringType.Properties.Count; i++) {
					string propName = field.DeclaringType.Properties[i].Name;
					if (propName.Length == name.Length - 1) {
						bool same = true;
						for (int j = 0; j < propName.Length; j++) {
							if (name[j + 1] != propName[j]) {
								same = false;
								break;
							}
						}
						if (same)
							return true;
					}
				}
			}
			return field.HasGeneratedName() && field.Name.EndsWith("BackingField", StringComparison.Ordinal);
		}

		internal static bool IsAnonymousMethodCacheField(FieldDef field)
		{
			return field.Name.StartsWith("CS$<>", StringComparison.Ordinal) || field.Name.StartsWith("<>f__am", StringComparison.Ordinal) || field.Name.StartsWith("<>f__mg", StringComparison.Ordinal);
		}

		static bool IsClosureType(TypeDef type)
		{
			if (!type.IsCompilerGenerated())
				return false;
			if (type.Name.StartsWith("_Closure$__"))
				return true;
			return type.HasGeneratedName() && (type.Name == "<>c" || type.Name.StartsWith("<>c__") || type.Name.Contains("DisplayClass") || type.Name.Contains("AnonStorey"));
		}

		/// <summary>
		/// Runs the C# transformations on the compilation unit.
		/// </summary>
		public void RunTransformations()
		{
			RunTransformations(null);
		}

		public void RunTransformations(Predicate<IAstTransform> transformAbortCondition)
		{
			WaitForBodies();
			TransformationPipeline.RunTransformationsUntil(syntaxTree, transformAbortCondition, context);
			transformationsHaveRun = true;
		}

		/// <summary>
		/// Gets the abstract source tree.
		/// </summary>
		public SyntaxTree SyntaxTree {
			get { return syntaxTree; }
		}

		/// <summary>
		/// Generates C# code from the abstract source tree.
		/// </summary>
		/// <remarks>This method adds ParenthesizedExpressions into the AST, and will run transformations if <see cref="RunTransformations"/> was not called explicitly</remarks>
		public void GenerateCode(IDecompilerOutput output)
		{
			if (!transformationsHaveRun)
				RunTransformations();

			syntaxTree.AcceptVisitor(new InsertParenthesesVisitor { InsertParenthesesForReadability = context.Settings.InsertParenthesesForReadability });
			GenericGrammarAmbiguityVisitor.ResolveAmbiguities(syntaxTree);
			var outputFormatter = new TextTokenWriter(output, context) ;
			var formattingPolicy = context.Settings.CSharpFormattingOptions;
			syntaxTree.AcceptVisitor(new CSharpOutputVisitor(outputFormatter, formattingPolicy, context.CancellationToken));
		}

		public void AddAssembly(AssemblyDef assemblyDefinition, bool onlyAssemblyLevel = false)
		{
			AddAssembly(assemblyDefinition.ManifestModule, onlyAssemblyLevel, true, true);
		}

		public void AddAssembly(ModuleDef moduleDefinition, bool onlyAssemblyLevel, bool decompileAsm, bool decompileMod)
		{
			if (decompileAsm && moduleDefinition.Assembly != null)
				ConvertCustomAttributes(Context.MetadataTextColorProvider, syntaxTree, moduleDefinition.Assembly, context.Settings, stringBuilder, "assembly");
			if (decompileMod)
				ConvertCustomAttributes(Context.MetadataTextColorProvider, syntaxTree, moduleDefinition, context.Settings, stringBuilder, "module");

			if (decompileMod && !onlyAssemblyLevel) {
				for (int i = 0; i < moduleDefinition.Types.Count; i++) {
					var typeDef = moduleDefinition.Types[i];
					// Skip the <Module> class
					if (typeDef.IsGlobalModuleType) continue;
					// Skip any hidden types
					if (AstBuilder.MemberIsHidden(typeDef, context.Settings))
						continue;

					AddType(typeDef);
				}
			}
		}

		NamespaceDeclaration GetCodeNamespace(string name, IAssembly asm)
		{
			if (string.IsNullOrEmpty(name)) {
				return null;
			}
			if (astNamespaces.TryGetValue(name, out var namespaceDeclaration)) {
				return namespaceDeclaration;
			} else {
				// Create the namespace
				NamespaceDeclaration astNamespace = new NamespaceDeclaration(name, asm);
				syntaxTree.Members.Add(astNamespace);
				astNamespaces[name] = astNamespace;
				return astNamespace;
			}
		}

		char ToHexChar(int val) {
			Debug.Assert(0 <= val && val <= 0x0F);
			if (0 <= val && val <= 9)
				return (char)('0' + val);
			return (char)('A' + val - 10);
		}

		string ToHex(uint value) {
			commentBuffer[0] = '0';
			commentBuffer[1] = 'x';
			int j = 2;
			for (int i = 0; i < 4; i++) {
				commentBuffer[j++] = ToHexChar((int)(value >> 28) & 0x0F);
				commentBuffer[j++] = ToHexChar((int)(value >> 24) & 0x0F);
				value <<= 8;
			}
			return new string(commentBuffer, 0, j);
		}

		void AddComment(AstNode node, IMemberDef member, string text = null)
		{
			if (!this.context.Settings.ShowTokenAndRvaComments)
				return;
			uint rva;
			long fileOffset;
			member.GetRVA(out rva, out fileOffset);

			var creator = new CommentReferencesCreator(stringBuilder);
			creator.AddText(" ");
			if (text != null) {
				creator.AddText("(");
				creator.AddText(text);
				creator.AddText(") ");
			}
			creator.AddText("Token: ");
			creator.AddReference(ToHex(member.MDToken.Raw), new TokenReference(member));
			creator.AddText(" RID: ");
			creator.AddText(member.MDToken.Rid.ToString());
			if (rva != 0) {
				var mod = member.Module;
				var filename = mod == null ? null : mod.Location;
				creator.AddText(" RVA: ");
				creator.AddReference(ToHex(rva), new AddressReference(filename, true, rva, 0));
				creator.AddText(" File Offset: ");
				creator.AddReference(ToHex((uint)fileOffset), new AddressReference(filename, false, (ulong)fileOffset, 0));
			}

			var cmt = new Comment(creator.Text);
			cmt.References = creator.CommentReferences;
			node.InsertChildAfter(null, cmt, Roles.Comment);
		}

		public void AddType(TypeDef typeDef)
		{
			var astType = CreateType(typeDef);
			NamespaceDeclaration astNS = GetCodeNamespace(typeDef.Namespace, typeDef.DefinitionAssembly);
			if (astNS != null) {
				astNS.Members.Add(astType);
			} else {
				syntaxTree.Members.Add(astType);
			}
		}

		public void AddMethod(MethodDef method)
		{
			AstNode node = method.IsConstructor ? (AstNode)CreateConstructor(method) : CreateMethod(method);
			syntaxTree.Members.Add(node);
		}

		public void AddProperty(PropertyDef property)
		{
			syntaxTree.Members.Add(CreateProperty(property));
		}

		public void AddField(FieldDef field)
		{
			syntaxTree.Members.Add(CreateField(field));
		}

		public void AddEvent(EventDef ev)
		{
			syntaxTree.Members.Add(CreateEvent(ev));
		}

		/// <summary>
		/// Creates the AST for a type definition.
		/// </summary>
		/// <param name="typeDef"></param>
		/// <returns>TypeDeclaration or DelegateDeclaration.</returns>
		public EntityDeclaration CreateType(TypeDef typeDef)
		{
			// create type
			TypeDef oldCurrentType = context.CurrentType;
			context.CurrentType = typeDef;
			TypeDeclaration astType = new TypeDeclaration();
			ConvertAttributes(astType, typeDef);
			astType.AddAnnotation(typeDef);
			astType.Modifiers = ConvertModifiers(typeDef);
			astType.NameToken = Identifier.Create(NRefactory.TypeSystem.ReflectionHelper.SplitTypeParameterCountFromReflectionName(typeDef.Name)).WithAnnotation(typeDef);

			if (typeDef.IsEnum) {  // NB: Enum is value type
				astType.ClassType = ClassType.Enum;
				astType.Modifiers &= ~Modifiers.Sealed;
			} else if (DnlibExtensions.IsValueType(typeDef)) {
				astType.ClassType = ClassType.Struct;
				astType.Modifiers &= ~(Modifiers.Sealed | Modifiers.Abstract | Modifiers.Static);
				if (DnlibExtensions.HasIsReadOnlyAttribute(typeDef))
					astType.Modifiers |= Modifiers.Readonly;
				if (DnlibExtensions.HasIsByRefLikeAttribute(typeDef))
					astType.Modifiers |= Modifiers.Ref;
			}
			else if (typeDef.IsInterface) {
				astType.ClassType = ClassType.Interface;
				astType.Modifiers &= ~Modifiers.Abstract;
			} else {
				astType.ClassType = ClassType.Class;
			}

			IList<GenericParam> genericParameters = typeDef.GenericParameters;
			if (typeDef.DeclaringType != null && typeDef.DeclaringType.HasGenericParameters) {
				int parentGenericCount = typeDef.DeclaringType.GenericParameters.Count;
				int genericParametersCount = genericParameters.Count;

				var newGenericParameters = new List<GenericParam>(Math.Max(0, genericParametersCount - parentGenericCount));
				for (int i = parentGenericCount; i < genericParametersCount; i++)
					newGenericParameters.Add(genericParameters[i]);

				genericParameters = newGenericParameters;
			}
			astType.TypeParameters.AddRange(MakeTypeParameters(genericParameters));
			astType.Constraints.AddRange(MakeConstraints(genericParameters));

			EntityDeclaration result = astType;
			if (typeDef.IsEnum) {
				long expectedEnumMemberValue = 0;
				bool forcePrintingInitializers = IsFlagsEnum(typeDef);
				var enumType = typeDef.GetEnumUnderlyingType();
				for (int i = 0; i < typeDef.Fields.Count; i++) {
					var field = typeDef.Fields[i];
					if (!field.IsStatic) {
						// the value__ field
						if (!new SigComparer().Equals(field.FieldType, typeDef.Module.CorLibTypes.Int32)) {
							astType.AddChild(ConvertType(field.FieldType, stringBuilder), Roles.BaseType);
						}
					} else {
						EnumMemberDeclaration enumMember = new EnumMemberDeclaration();
						ConvertCustomAttributes(Context.MetadataTextColorProvider, enumMember, field, context.Settings, stringBuilder);
						enumMember.AddAnnotation(field);
						enumMember.NameToken = Identifier.Create(field.Name).WithAnnotation(field);
						TryGetConstant(field, out var constant);
						TypeCode c = constant == null ? TypeCode.Empty : Type.GetTypeCode(constant.GetType());
						if (c < TypeCode.Char || c > TypeCode.Decimal)
							continue;
						long memberValue = (long)CSharpPrimitiveCast.Cast(TypeCode.Int64, constant, false);
						if (forcePrintingInitializers || memberValue != expectedEnumMemberValue) {
							enumMember.AddChild(new PrimitiveExpression(ConvertConstant(enumType, constant)), EnumMemberDeclaration.InitializerRole);
						}
						expectedEnumMemberValue = memberValue + 1;
						astType.AddChild(enumMember, Roles.TypeMemberRole);
						AddComment(enumMember, field);
					}
				}
			} else if (IsNormalDelegate(typeDef)) {
				DelegateDeclaration dd = new DelegateDeclaration();
				dd.Modifiers = astType.Modifiers & ~Modifiers.Sealed;
				dd.NameToken = (Identifier)astType.NameToken.Clone();
				dd.AddAnnotation(typeDef);
				astType.Attributes.MoveTo(dd.Attributes);
				astType.TypeParameters.MoveTo(dd.TypeParameters);
				astType.Constraints.MoveTo(dd.Constraints);
				for (int i = 0; i < typeDef.Methods.Count; i++) {
					var m = typeDef.Methods[i];
					if (m.Name == "Invoke") {
						dd.ReturnType = ConvertType(m.ReturnType, stringBuilder, m.Parameters.ReturnParameter.ParamDef);
						dd.Parameters.AddRange(MakeParameters(Context.MetadataTextColorProvider, m, context.Settings, stringBuilder));
						ConvertAttributes(dd, m.Parameters.ReturnParameter);
						AddComment(dd, m, "Invoke");
					}
				}
				AddComment(dd, typeDef);
				result = dd;
			} else {
				// Base type
				if (typeDef.BaseType != null && !DnlibExtensions.IsValueType(typeDef) && !typeDef.BaseType.IsSystemObject()) {
					astType.AddChild(ConvertType(typeDef.BaseType, stringBuilder), Roles.BaseType);
				}
				var interfaceImpls = GetInterfaceImpls(typeDef);
				for (int i = 0; i < interfaceImpls.Count; i++)
					astType.AddChild(ConvertType(interfaceImpls[i].Interface, stringBuilder), Roles.BaseType);

				if (IsStubbableIterator(typeDef)) {
					astType.Modifiers = (astType.Modifiers & ~Modifiers.Sealed) | Modifiers.Abstract;
					AddIteratorStubs(astType);
				}

				AddTypeMembers(astType, typeDef);

				if (astType.Members.OfType<IndexerDeclaration>().Any(idx => idx.PrivateImplementationType.IsNull)) {
					// Remove the [DefaultMember] attribute if the class contains indexers
					foreach (AttributeSection section in astType.Attributes) {
						foreach (Ast.Attribute attr in section.Attributes) {
							ITypeDefOrRef tr = attr.Type.Annotation<ITypeDefOrRef>();
							if (tr != null && tr.Compare(systemReflectionString, defaultMemberAttributeString)) {
								attr.Remove();
							}
						}
						if (section.Attributes.Count == 0)
							section.Remove();
					}
				}
			}

			AddComment(astType, typeDef);
			context.CurrentType = oldCurrentType;
			return result;
		}
		static readonly UTF8String systemReflectionString = new UTF8String("System.Reflection");
		static readonly UTF8String defaultMemberAttributeString = new UTF8String("DefaultMemberAttribute");
		static readonly UTF8String systemString = new UTF8String("System");
		static readonly UTF8String multicastDelegateString = new UTF8String("MulticastDelegate");

		bool IsNormalDelegate(TypeDef td)
		{
			if (!td.BaseType.Compare(systemString, multicastDelegateString))
				return false;

			if (td.HasFields)
				return false;
			if (td.HasProperties)
				return false;
			if (td.HasEvents)
				return false;
			if (td.Methods.Any(m => m.Body != null))
				return false;

			return true;
		}

		#region Create TypeOf Expression
		/// <summary>
		/// Creates a typeof-expression for the specified type.
		/// </summary>
		public static TypeOfExpression CreateTypeOfExpression(ITypeDefOrRef type, StringBuilder sb)
		{
			return new TypeOfExpression(AddEmptyTypeArgumentsForUnboundGenerics(ConvertType(type, sb)));
		}

		static AstType AddEmptyTypeArgumentsForUnboundGenerics(AstType type)
		{
			ITypeDefOrRef typeRef = type.Annotation<ITypeDefOrRef>();
			if (typeRef == null)
				return type;
			TypeDef typeDef = typeRef.ResolveTypeDef(); // need to resolve to figure out the number of type parameters
			if (typeDef == null || !typeDef.HasGenericParameters)
				return type;
			SimpleType sType = type as SimpleType;
			MemberType mType = type as MemberType;
			if (sType != null) {
				while (typeDef.GenericParameters.Count > sType.TypeArguments.Count) {
					sType.TypeArguments.Add(new SimpleType("").WithAnnotation(BoxedTextColor.TypeGenericParameter).WithAnnotation(SimpleType.DummyTypeGenericParam));
				}
			}

			if (mType != null) {
				AddEmptyTypeArgumentsForUnboundGenerics(mType.Target);

				int outerTypeParamCount = typeDef.DeclaringType == null ? 0 : typeDef.DeclaringType.GenericParameters.Count;

				while (typeDef.GenericParameters.Count - outerTypeParamCount > mType.TypeArguments.Count) {
					mType.TypeArguments.Add(new SimpleType("").WithAnnotation(BoxedTextColor.TypeGenericParameter).WithAnnotation(SimpleType.DummyTypeGenericParam));
				}
			}

			return type;
		}
		#endregion

		#region Convert Type Reference
		/// <summary>
		/// Converts a type reference.
		/// </summary>
		/// <param name="type">The type reference that should be converted into
		/// a type system type reference.</param>
		/// <param name="typeAttributes">Attributes associated with the type reference.
		/// This is used to support the 'dynamic' type.</param>
		public static AstType ConvertType(ITypeDefOrRef type, StringBuilder sb, IHasCustomAttribute typeAttributes = null, ConvertTypeOptions options = ConvertTypeOptions.None)
		{
			int typeIndex = 0;
			return ConvertType(type, typeAttributes, ref typeIndex, options, 0, sb);
		}

		/// <summary>
		/// Converts a type reference.
		/// </summary>
		/// <param name="type">The type reference that should be converted into
		/// a type system type reference.</param>
		/// <param name="typeAttributes">Attributes associated with the type reference.
		/// This is used to support the 'dynamic' type.</param>
		public static AstType ConvertType(TypeSig type, StringBuilder sb, IHasCustomAttribute typeAttributes = null, ConvertTypeOptions options = ConvertTypeOptions.None)
		{
			int typeIndex = 0;
			return ConvertType(type, typeAttributes, ref typeIndex, options, 0, sb);
		}

		const int MAX_CONVERTTYPE_DEPTH = 50;
		static AstType ConvertType(TypeSig type, IHasCustomAttribute typeAttributes, ref int typeIndex, ConvertTypeOptions options, int depth, StringBuilder sb)
		{
			if (depth++ > MAX_CONVERTTYPE_DEPTH)
				return AstType.Null;
			type = type.RemovePinned();
			if (type == null) {
				return AstType.Null;
			}

			if (type is ByRefSig byRefSig) {
				typeIndex++;
				return ConvertType(byRefSig.Next, typeAttributes, ref typeIndex, options, depth, sb).MakeRefType();
			} else if (type is PtrSig ptrSig) {
				typeIndex++;
				return ConvertType(ptrSig.Next, typeAttributes, ref typeIndex, options, depth, sb).MakePointerType();
			} else if (type is ArraySigBase arraySig) {
				typeIndex++;
				return ConvertType(arraySig.Next, typeAttributes, ref typeIndex, options, depth, sb).MakeArrayType((int)arraySig.Rank);
			} else if (type is GenericInstSig gType) {
				if (gType.GenericType != null && gType.GenericArguments.Count == 1 && gType.GenericType.IsSystemNullable()) {
					typeIndex++;
					return ConvertType(gType.GenericArguments[0], typeAttributes, ref typeIndex, options, depth, sb).MakeNullableType();
				}
				AstType baseType = ConvertType(gType.GenericType?.TypeDefOrRef, typeAttributes, ref typeIndex, options & ~ConvertTypeOptions.IncludeTypeParameterDefinitions, depth, sb);
				List<AstType> typeArguments = new List<AstType>(gType.GenericArguments.Count);
				for (int i = 0; i < gType.GenericArguments.Count; i++) {
					typeIndex++;
					typeArguments.Add(ConvertType(gType.GenericArguments[i], typeAttributes, ref typeIndex, options, depth, sb));
				}
				ApplyTypeArgumentsTo(baseType, typeArguments);
				return baseType;
			} else if (type is GenericSig sig) {
				var simpleType = new SimpleType(sig.GetName(sb)).WithAnnotation(sig.GenericParam).WithAnnotation(sig);
				simpleType.IdentifierToken.WithAnnotation(sig.GenericParam).WithAnnotation(sig);
				return simpleType;
			} else if (type is TypeDefOrRefSig typeDefOrRefSig) {
				return ConvertType(typeDefOrRefSig.TypeDefOrRef, typeAttributes, ref typeIndex, options, depth, sb);
			} else if (type is ModifierSig modifierSig) {
				typeIndex++;
				return ConvertType(modifierSig.Next, typeAttributes, ref typeIndex, options, depth, sb);
			} else if (type is FnPtrSig fnPtrSig) {
				var mSig = fnPtrSig.MethodSig;

				var returnType = mSig.GetRetType().RemovePinned();
				var customCallConvs = new List<ITypeDefOrRef>();
				while (returnType is ModifierSig modReturn) {
					if (modReturn.Modifier.Name.StartsWith("CallConv", StringComparison.Ordinal) && modReturn.Modifier.Namespace == "System.Runtime.CompilerServices"){
						returnType = modReturn.Next.RemovePinned();
						customCallConvs.Add(modReturn.Modifier);
					}
					else
						break;
				}

				var astType = new FunctionPointerAstType();

				if (mSig.IsUnmanaged) {
					astType.HasUnmanagedCallingConvention = true;
				}
				else if (!mSig.IsDefault) {
					string callconvName = (mSig.CallingConvention & CallingConvention.Mask) switch {
						CallingConvention.C => "Cdecl",
						CallingConvention.StdCall => "Stdcall",
						CallingConvention.ThisCall => "Thiscall",
						CallingConvention.FastCall => "Fastcall",
						CallingConvention.VarArg => "Varargs",
						_ => mSig.CallingConvention.ToString()
					};
					astType.HasUnmanagedCallingConvention = true;
					astType.CallingConventions.Add(new PrimitiveType(callconvName));
				}

				foreach (var customCallConv in customCallConvs) {
					AstType callConvSyntax;
					if (customCallConv.Name.StartsWith("CallConv", StringComparison.Ordinal) && customCallConv.Name.Length > 8) {
						callConvSyntax = new PrimitiveType(customCallConv.Name.Substring(8)).WithAnnotation(customCallConv);
					}
					else {
						int _ = 0;
						callConvSyntax = ConvertType(customCallConv, null, ref _, options, depth, sb);
					}
					astType.CallingConventions.Add(callConvSyntax);
				}

				typeIndex++;
				astType.ReturnType = ConvertType(mSig.GetRetType(), typeAttributes, ref typeIndex, options, depth, sb);

				for (int i = 0; i < mSig.Params.Count; i++) {
					var originalParamType = mSig.Params[i].RemovePinned();
					var paramType = originalParamType;
					var kind = ParameterModifier.None;
					if (paramType is CModReqdSig modreq) {
						sb.Clear();
						string modifier = FullNameFactory.FullName(modreq.Modifier, false, null, sb);
						if (modifier == "System.Runtime.InteropServices.InAttribute") {
							kind = ParameterModifier.In;
							paramType = modreq.Next;
						}
						else if (modifier == "System.Runtime.InteropServices.OutAttribute") {
							kind = ParameterModifier.Out;
							paramType = modreq.Next;
						}
					}
					if (paramType is ByRefSig) {
						if (kind == ParameterModifier.None)
							kind = ParameterModifier.Ref;
					}
					else {
						kind = ParameterModifier.None;
					}

					typeIndex++;
					var paramDecl = new ParameterDeclaration {
						Type = ConvertType(originalParamType, typeAttributes, ref typeIndex, options, depth, sb),
						ParameterModifier = kind
					};
					if (paramType is ByRefSig && kind != ParameterModifier.None)
						UndoRefSpecifier(paramDecl.Type);

					astType.Parameters.Add(paramDecl);
				}

				return astType;
			} else
				return ConvertType(type.ToTypeDefOrRef(), typeAttributes, ref typeIndex, options, depth, sb);
		}

		static AstType ConvertType(ITypeDefOrRef type, IHasCustomAttribute typeAttributes, ref int typeIndex, ConvertTypeOptions options, int depth, StringBuilder sb)
		{
			if (depth++ > MAX_CONVERTTYPE_DEPTH || type == null)
				return AstType.Null;

			var ts = type as TypeSpec;
			if (ts != null && !(ts.TypeSig is FnPtrSig fnPtrSig && fnPtrSig.MethodSig is null))
				return ConvertType(ts.TypeSig, typeAttributes, ref typeIndex, options, depth, sb);

			if (type.DeclaringType != null && (options & ConvertTypeOptions.DoNotIncludeEnclosingType) == 0) {
				// The enclosing type is emitted as the Target of a MemberType, i.e. in type-name position.
				// A C# namespace_or_type_name must begin with an identifier, so a predefined-type keyword is
				// never legal there. Without this flag System.Decimal/DecCalc came out as "decimal.DecCalc":
				// the parser reads that as a member-access expression (predefined_type '.' identifier, which
				// is legal in expression position) and then demands a ';' before the declared name, so a
				// local declaration became CS1002 and "default(decimal.DecCalc)" became CS1026 + CS1513.
				// Suppressing the keyword substitution for the enclosing chain only yields
				// "System.Decimal.DecCalc", which parses in both positions. Generic arguments are converted
				// in the TypeSig overload with the unmodified options, so "List<int>" keeps its keyword.
				AstType typeRef = ConvertType(type.DeclaringType, typeAttributes, ref typeIndex, (options & ~ConvertTypeOptions.IncludeTypeParameterDefinitions) | ConvertTypeOptions.DoNotUsePrimitiveTypeNames, depth, sb);
				string namepart = ICSharpCode.NRefactory.TypeSystem.ReflectionHelper.SplitTypeParameterCountFromReflectionName(type.Name);
				MemberType memberType = new MemberType { Target = typeRef, MemberNameToken = Identifier.Create(namepart).WithAnnotation(type) };
				memberType.AddAnnotation(type);
				if ((options & ConvertTypeOptions.IncludeTypeParameterDefinitions) == ConvertTypeOptions.IncludeTypeParameterDefinitions) {
					AddTypeParameterDefininitionsTo(type, memberType);
				}
				return memberType;
			} else {
				string ns = type.GetNamespace(sb) ?? string.Empty;
				string name = type.GetName(sb);
				if (ts != null)
					name = DnlibExtensions.GetFnPtrName(ts.TypeSig as FnPtrSig);
				if (name == null)
					throw new InvalidOperationException("type.Name returned null. Type: " + type);

				if (name == "Object" && ns == "System" && HasDynamicAttribute(typeAttributes, typeIndex)) {
					return new PrimitiveType("dynamic");
				} else {
					if (ns == "System") {
						if ((options & ConvertTypeOptions.DoNotUsePrimitiveTypeNames)
							!= ConvertTypeOptions.DoNotUsePrimitiveTypeNames) {
							switch (name) {
								case "SByte":
									return new PrimitiveType("sbyte").WithAnnotation(type);
								case "Int16":
									return new PrimitiveType("short").WithAnnotation(type);
								case "Int32":
									return new PrimitiveType("int").WithAnnotation(type);
								case "Int64":
									return new PrimitiveType("long").WithAnnotation(type);
								case "Byte":
									return new PrimitiveType("byte").WithAnnotation(type);
								case "UInt16":
									return new PrimitiveType("ushort").WithAnnotation(type);
								case "UInt32":
									return new PrimitiveType("uint").WithAnnotation(type);
								case "UInt64":
									return new PrimitiveType("ulong").WithAnnotation(type);
								case "String":
									return new PrimitiveType("string").WithAnnotation(type);
								case "Single":
									return new PrimitiveType("float").WithAnnotation(type);
								case "Double":
									return new PrimitiveType("double").WithAnnotation(type);
								case "Decimal":
									return new PrimitiveType("decimal").WithAnnotation(type);
								case "Char":
									return new PrimitiveType("char").WithAnnotation(type);
								case "Boolean":
									return new PrimitiveType("bool").WithAnnotation(type);
								case "Void":
									return new PrimitiveType("void").WithAnnotation(type);
								case "Object":
									return new PrimitiveType("object").WithAnnotation(type);
							}
						}
					}

					name = ICSharpCode.NRefactory.TypeSystem.ReflectionHelper.SplitTypeParameterCountFromReflectionName(name);

					AstType astType;
					if ((options & ConvertTypeOptions.IncludeNamespace) == ConvertTypeOptions.IncludeNamespace && ns.Length > 0) {
						string[] parts = ns.Split('.');
						var nsAsm = type.DefinitionAssembly;
						sb.Clear();
						sb.Append(parts[0]);
						SimpleType simpleType;
						AstType nsType = simpleType = new SimpleType(parts[0]).WithAnnotation(BoxedTextColor.Namespace);
						simpleType.IdentifierToken.WithAnnotation(BoxedTextColor.Namespace).WithAnnotation(new NamespaceReference(nsAsm, parts[0]));
						for (int i = 1; i < parts.Length; i++) {
							sb.Append('.');
							sb.Append(parts[i]);
							var nsPart = sb.ToString();
							nsType = new MemberType { Target = nsType, MemberNameToken = Identifier.Create(parts[i]).WithAnnotation(BoxedTextColor.Namespace).WithAnnotation(new NamespaceReference(nsAsm, nsPart)) }.WithAnnotation(BoxedTextColor.Namespace);
						}
						astType = new MemberType { Target = nsType, MemberNameToken = Identifier.Create(name).WithAnnotation(type) };
					} else {
						astType = new SimpleType(name);
					}
					astType.AddAnnotation(type);

					if ((options & ConvertTypeOptions.IncludeTypeParameterDefinitions) == ConvertTypeOptions.IncludeTypeParameterDefinitions) {
						AddTypeParameterDefininitionsTo(type, astType);
					}
					return astType;
				}
			}
		}

		static void AddTypeParameterDefininitionsTo(ITypeDefOrRef type, AstType astType)
		{
			TypeDef typeDef = type.ResolveTypeDef();
			if (typeDef != null && typeDef.HasGenericParameters) {
				List<AstType> typeArguments = new List<AstType>(typeDef.GenericParameters.Count);
				for (int i = 0; i < typeDef.GenericParameters.Count; i++) {
					var gp = typeDef.GenericParameters[i];
					typeArguments.Add(new SimpleType(gp.Name).WithAnnotation(gp));
				}
				ApplyTypeArgumentsTo(astType, typeArguments);
			}
		}

		static void ApplyTypeArgumentsTo(AstType baseType, List<AstType> typeArguments)
		{
			SimpleType st = baseType as SimpleType;
			if (st != null) {
				st.TypeArguments.AddRange(typeArguments);
			}
			MemberType mt = baseType as MemberType;
			if (mt != null) {
				ITypeDefOrRef type = mt.Annotation<ITypeDefOrRef>();
				if (type != null) {
					int typeParameterCount;
					var td = type.ResolveTypeDef();
					if (td is not null) {
						if (td.DeclaringType is not null && td.DeclaringType.HasGenericParameters)
							typeParameterCount = td.GenericParameters.Count - td.DeclaringType.GenericParameters.Count;
						else
							typeParameterCount = td.GenericParameters.Count;
					}
					else {
						// Fallback to type.Name for unresolved type references since they do not store generic parameter information
						typeParameterCount = GetTypeParameterCountFromReflectionName(type.Name);
					}
					if (typeParameterCount > typeArguments.Count)
						typeParameterCount = typeArguments.Count;
					mt.TypeArguments.AddRange(typeArguments.GetRange(typeArguments.Count - typeParameterCount, typeParameterCount));
					typeArguments.RemoveRange(typeArguments.Count - typeParameterCount, typeParameterCount);
					if (typeArguments.Count > 0)
						ApplyTypeArgumentsTo(mt.Target, typeArguments);
				} else {
					mt.TypeArguments.AddRange(typeArguments);
				}
			}
		}

		static int GetTypeParameterCountFromReflectionName(string reflectionName)
		{
			int pos = reflectionName.LastIndexOf('`');
			if (pos < 0)
				return 0;
			if (int.TryParse(reflectionName.Substring(pos + 1), out var typeParameterCount))
				return typeParameterCount;
			return 0;
		}

		static readonly UTF8String systemRuntimeCompilerServicesString = new UTF8String("System.Runtime.CompilerServices");
		static readonly UTF8String dynamicAttributeString = new UTF8String("DynamicAttribute");
		static bool HasDynamicAttribute(IHasCustomAttribute attributeProvider, int typeIndex)
		{
			if (attributeProvider == null)
				return false;
			for (int i = 0; i < attributeProvider.CustomAttributes.Count; i++) {
				var a = attributeProvider.CustomAttributes[i];
				if (a.AttributeType.Compare(systemRuntimeCompilerServicesString, dynamicAttributeString)) {
					if (a.ConstructorArguments.Count == 1) {
						IList<CAArgument> values = a.ConstructorArguments[0].Value as IList<CAArgument>;
						if (values != null && typeIndex < values.Count && values[typeIndex].Value is bool)
							return (bool)values[typeIndex].Value;
					}
					return true;
				}
			}
			return false;
		}
		#endregion

		#region ConvertModifiers
		// Vizibilitatea scrisa direct din metadate, fara nicio corectie. E scoasa separat ca sa se poata
		// intreba si despre tipul de baza sau despre o interfata fara sa reintram in logica de largire de
		// mai jos: asa nu exista recursie, oricat de ciudat ar fi lantul de mostenire din metadate.
		Modifiers RawTypeVisibility(TypeDef typeDef)
		{
			if (typeDef.IsNestedPrivate)
				return context.Settings.MemberAddPrivateModifier ? Modifiers.Private : Modifiers.None;
			if (typeDef.IsNotPublic)
				return context.Settings.TypeAddInternalModifier ? Modifiers.Internal : Modifiers.None;
			if (typeDef.IsNestedAssembly || typeDef.IsNestedFamilyAndAssembly)
				return Modifiers.Internal;
			if (typeDef.IsNestedFamily)
				return Modifiers.Protected;
			if (typeDef.IsNestedFamilyOrAssembly)
				return Modifiers.Protected | Modifiers.Internal;
			if (typeDef.IsPublic || typeDef.IsNestedPublic)
				return Modifiers.Public;
			return Modifiers.None;
		}

		Modifiers ConvertModifiers(TypeDef typeDef)
		{
			Modifiers modifiers = RawTypeVisibility(typeDef);

			// CS0052: un camp nu poate fi mai accesibil decat tipul lui. In IL regula asta nu exista, deci
			// metadatele o incalca linistit. In `_PrivateImplementationDetails_` - clasa pe care o
			// genereaza compilatorul pentru datele initiale de array - tipurile imbricate
			// ValueTypeNPrivateSealed0..4 sunt NestedPrivate, iar cele 14 campuri cu 'hasfieldrva' care le
			// folosesc sunt internal. Asta da cele 14 CS0052 din exportul masurat.
			// Largim tipul imbricat in loc sa restrangem campul, din doua motive: largirea numai permite
			// mai mult, nu poate lua un acces care exista deja; iar campurile chiar sunt folosite din alt
			// fisier (UGCUtils.cs se refera la
			// _PrivateImplementationDetails_.D74C23FF...FieldHandle), deci a le face private ar strica
			// referinta aia.
			if (typeDef.IsNested && typeDef.DeclaringType != null) {
				Modifiers needed = MostAccessibleFieldUsing(typeDef);
				if (AccessibilityRank(needed) > AccessibilityRank(modifiers) && CanWidenNestedType(typeDef, needed))
					modifiers = needed;
			}

			if (typeDef.IsAbstract && typeDef.IsSealed)
				modifiers |= Modifiers.Static;
			else if (typeDef.IsAbstract)
				modifiers |= Modifiers.Abstract;
			else if (typeDef.IsSealed)
				modifiers |= Modifiers.Sealed;

			return modifiers;
		}

		// Cea mai permisiva vizibilitate intalnita la campurile tipului care il declara pe nestedType si
		// care il folosesc pe nestedType in tipul lor. Ne uitam numai la campurile tipului declarant: in
		// afara lui un tip imbricat privat nici nu e vizibil, deci acolo CS0052 nu poate aparea din cauza
		// lui - eventual apare CS0122 "inaccesibil", care e alta problema si nu se repara prin largire.
		Modifiers MostAccessibleFieldUsing(TypeDef nestedType)
		{
			var declaring = nestedType.DeclaringType;
			Modifiers best = Modifiers.None;
			int bestRank = -1;
			for (int i = 0; i < declaring.Fields.Count; i++) {
				var fieldDef = declaring.Fields[i];
				if (!SignatureUsesType(fieldDef.FieldType, nestedType, 0))
					continue;
				var visibility = ConvertModifiers(fieldDef) & Modifiers.VisibilityMask;
				int rank = AccessibilityRank(visibility);
				if (rank > bestRank) {
					bestRank = rank;
					best = visibility;
				}
			}
			return best;
		}

		// Largirea unui tip imbricat poate naste o eroare noua daca baza lui sau o interfata implementata
		// ramane mai putin accesibila decat el (CS0060 / CS0061). Cand se intampla asta nu largim deloc:
		// e mai bine sa ramanem cu un CS0052 stiut decat sa mutam eroarea in alta parte.
		// Comparam cu vizibilitatea BRUTA a bazei, nu cu cea eventual largita, ca sa nu existe recursie;
		// asta ne face doar mai prudenti, niciodata mai indrazneti. Cand baza e un TypeSpec sau un tip din
		// alt modul nu avem ce compara si lasam largirea sa treaca: acolo accesibilitatea nu depinde de
		// ce scriem noi in fisierul asta.
		bool CanWidenNestedType(TypeDef typeDef, Modifiers target)
		{
			// Un membru protected intr-o structura e CS0666, deci nu largim niciodata spre protected acolo.
			if ((target & Modifiers.Protected) != 0 && typeDef.DeclaringType.IsValueType)
				return false;

			int targetRank = AccessibilityRank(target);
			var baseType = typeDef.BaseType as TypeDef;
			if (baseType != null && AccessibilityRank(RawTypeVisibility(baseType)) < targetRank)
				return false;
			for (int i = 0; i < typeDef.Interfaces.Count; i++) {
				var iface = typeDef.Interfaces[i].Interface as TypeDef;
				if (iface != null && AccessibilityRank(RawTypeVisibility(iface)) < targetRank)
					return false;
			}
			return true;
		}

		// Tipul apare in semnatura direct, sub array/pointer/byref/modificatori, sau ca argument generic.
		// Adancimea e plafonata fiindca metadatele recuperate de Cpp2IL nu sunt garantat bine formate.
		static bool SignatureUsesType(TypeSig sig, TypeDef target, int depth)
		{
			if (sig == null || target == null || depth > 8)
				return false;
			sig = sig.RemovePinnedAndModifiers();
			if (sig == null)
				return false;
			if (sig is GenericInstSig genericInst) {
				if (SignatureUsesType(genericInst.GenericType, target, depth + 1))
					return true;
				for (int i = 0; i < genericInst.GenericArguments.Count; i++) {
					if (SignatureUsesType(genericInst.GenericArguments[i], target, depth + 1))
						return true;
				}
				return false;
			}
			if (sig is TypeDefOrRefSig typeDefOrRef)
				return typeDefOrRef.TypeDef == target;
			if (sig is NonLeafSig nonLeaf)
				return SignatureUsesType(nonLeaf.Next, target, depth + 1);
			return false;
		}

		Modifiers ConvertModifiers(FieldDef fieldDef)
		{
			Modifiers modifiers = Modifiers.None;
			if (fieldDef.IsPrivate) {
				if (context.Settings.MemberAddPrivateModifier)
					modifiers |= Modifiers.Private;
			} else if (fieldDef.IsAssembly)
				modifiers |= Modifiers.Internal;
			else if (fieldDef.IsFamily)
				modifiers |= Modifiers.Protected;
			else if (fieldDef.IsFamilyOrAssembly)
				modifiers |= Modifiers.Protected | Modifiers.Internal;
			else if (fieldDef.IsPublic)
				modifiers |= Modifiers.Public;
			else if (fieldDef.IsFamilyAndAssembly)
				modifiers |= Modifiers.Private | Modifiers.Protected;

			if (fieldDef.IsLiteral) {
				modifiers |= Modifiers.Const;
			} else {
				if (fieldDef.IsStatic)
					modifiers |= Modifiers.Static;

				if (fieldDef.IsInitOnly)
					modifiers |= Modifiers.Readonly;
			}

			CModReqdSig modreq = fieldDef.FieldType as CModReqdSig;
			if (modreq != null && modreq.Modifier != null && modreq.Modifier.Compare(systemRuntimeCompilerServicesString, isVolatileString))
				modifiers |= Modifiers.Volatile;

			return modifiers;
		}
		static readonly UTF8String isVolatileString = new UTF8String("IsVolatile");

		Modifiers ConvertModifiers(MethodDef methodDef, bool canBeReadOnlyMember)
		{
			if (methodDef == null)
				return Modifiers.None;
			Modifiers modifiers = Modifiers.None;
			if (methodDef.IsPrivate) {
				if (context.Settings.MemberAddPrivateModifier)
					modifiers |= Modifiers.Private;
			}
			else if (methodDef.IsAssembly)
				modifiers |= Modifiers.Internal;
			else if (methodDef.IsFamily)
				modifiers |= Modifiers.Protected;
			else if (methodDef.IsFamilyOrAssembly)
				modifiers |= Modifiers.Protected | Modifiers.Internal;
			else if (methodDef.IsPublic)
				modifiers |= Modifiers.Public;
			else if (methodDef.IsFamilyAndAssembly)
				modifiers |= Modifiers.Private | Modifiers.Protected;

			if (methodDef.IsStatic)
				modifiers |= Modifiers.Static;

			if (methodDef.IsAbstract) {
				modifiers |= Modifiers.Abstract;
				if (!methodDef.IsNewSlot)
					modifiers |= GetOverrideModifierOrDefault(methodDef, Modifiers.None);
			} else if (methodDef.IsFinal) {
				if (!methodDef.IsNewSlot) {
					modifiers |= Modifiers.Sealed | GetOverrideModifierOrDefault(methodDef, Modifiers.None);
				}
			} else if (methodDef.IsVirtual) {
				var virtualModifier = methodDef.DeclaringType.IsSealed ? Modifiers.None : Modifiers.Virtual;
				if (methodDef.IsNewSlot)
					modifiers |= virtualModifier;
				else
					modifiers |= GetOverrideModifierOrDefault(methodDef, virtualModifier);
			}
			if (!methodDef.HasBody && !methodDef.IsAbstract)
				modifiers |= Modifiers.Extern;
			if (canBeReadOnlyMember && IsReadonlyMember(methodDef))
				modifiers |= Modifiers.ReadonlyMember;

			return modifiers;
		}

		bool IsReadonlyMember(MethodDef methodDef)
		{
			return methodDef != null && !methodDef.IsStatic && DnlibExtensions.HasIsReadOnlyAttribute(methodDef);
		}

		// mcs doesn't set IsNewSlot if it doesn't override anything so verify that
		// it's a method override.
		static Modifiers GetOverrideModifierOrDefault(MethodDef method, Modifiers defaultValue) {
			var baseType = method.DeclaringType.BaseType;
			var name = method.Name;
			int paramCount = method.MethodSig.GetParamCount();
			while (baseType != null) {
				var type = baseType.Resolve();
				// If we failed to resolve it, assume it's a method override
				if (type == null)
					return Modifiers.Override;
				for (int i = 0; i < type.Methods.Count; i++) {
					var m = type.Methods[i];
					// This method doesn't handle generic base classes so assume it matches if name
					// and param count matches.
					if (m.IsVirtual && m.Name == name && m.MethodSig.GetParamCount() == paramCount)
						return Modifiers.Override;
				}
				baseType = type.BaseType;
			}
			return defaultValue;
		}

		#endregion

		IList<InterfaceImpl> GetInterfaceImpls(TypeDef type)
		{
			if (context.Settings.UseSourceCodeOrder)
				return type.Interfaces;// These are already sorted by MD token
			return type.GetInterfaceImpls(context.Settings.SortMembers);
		}

		IList<TypeDef> GetNestedTypes(TypeDef type)
		{
			if (context.Settings.UseSourceCodeOrder)
				return type.NestedTypes;// These are already sorted by MD token
			return type.GetNestedTypes(context.Settings.SortMembers);
		}

		IList<FieldDef> GetFields(TypeDef type)
		{
			if (context.Settings.UseSourceCodeOrder)
				return type.Fields;// These are already sorted by MD token
			return type.GetFields(context.Settings.SortMembers);
		}

		void AddTypeMembers(TypeDeclaration astType, TypeDef typeDef)
		{
			bool hasShownMethods = false;
			foreach (var d in this.context.Settings.DecompilationObjects) {
				switch (d) {
				case DecompilationObject.NestedTypes:
					var nestedTypes = GetNestedTypes(typeDef);
					for (int i = 0; i < nestedTypes.Count; i++) {
						var nestedTypeDef = nestedTypes[i];
						if (MemberIsHidden(nestedTypeDef, context.Settings))
							continue;
						var nestedType = CreateType(nestedTypeDef);
						SetNewModifier(nestedType);
						astType.AddChild(nestedType, Roles.TypeMemberRole);
					}
					break;

				case DecompilationObject.Fields:
					var fieldDefs = GetFields(typeDef);
					for (int i = 0; i < fieldDefs.Count; i++) {
						var fieldDef = fieldDefs[i];
						if (MemberIsHidden(fieldDef, context.Settings)) continue;
						astType.AddChild(CreateField(fieldDef), Roles.TypeMemberRole);
					}
					break;

				case DecompilationObject.Events:
					if (hasShownMethods)
						break;
					if (context.Settings.UseSourceCodeOrder || !typeDef.CanSortMethods()) {
						ShowAllMethods(astType, typeDef);
						hasShownMethods = true;
						break;
					}
					var eventDefs = typeDef.GetEvents(context.Settings.SortMembers);
					for (int i = 0; i < eventDefs.Count; i++) {
						var eventDef = eventDefs[i];
						if (eventDef.AddMethod == null && eventDef.RemoveMethod == null)
							continue;
						astType.AddChild(CreateEvent(eventDef), Roles.TypeMemberRole);
					}
					break;

				case DecompilationObject.Properties:
					if (hasShownMethods)
						break;
					if (context.Settings.UseSourceCodeOrder || !typeDef.CanSortMethods()) {
						ShowAllMethods(astType, typeDef);
						hasShownMethods = true;
						break;
					}
					var propertyDefs = typeDef.GetProperties(context.Settings.SortMembers);
					for (int i = 0; i < propertyDefs.Count; i++) {
						var propDef = propertyDefs[i];
						if (propDef.GetMethod == null && propDef.SetMethod == null)
							continue;
						if (MemberIsHidden(propDef, context.Settings)) continue;
						astType.Members.Add(CreateProperty(propDef));
					}
					break;

				case DecompilationObject.Methods:
					if (hasShownMethods)
						break;
					if (context.Settings.UseSourceCodeOrder || !typeDef.CanSortMethods()) {
						ShowAllMethods(astType, typeDef);
						hasShownMethods = true;
						break;
					}
					var methodDefs = typeDef.GetMethods(context.Settings.SortMembers);
					for (int i = 0; i < methodDefs.Count; i++) {
						var methodDef = methodDefs[i];
						if (MemberIsHidden(methodDef, context.Settings)) continue;

						if (methodDef.IsConstructor)
							astType.Members.Add(CreateConstructor(methodDef));
						else
							astType.Members.Add(CreateMethod(methodDef));
					}
					break;

				default: throw new InvalidOperationException();
				}
			}
		}

		void ShowAllMethods(TypeDeclaration astType, TypeDef type)
		{
			foreach (var def in type.GetNonSortedMethodsPropertiesEvents()) {
				var md = def as MethodDef;
				if (md != null) {
					if (MemberIsHidden(md, context.Settings))
						continue;
					if (md.IsConstructor)
						astType.Members.Add(CreateConstructor(md));
					else
						astType.Members.Add(CreateMethod(md));
					continue;
				}

				var pd = def as PropertyDef;
				if (pd != null) {
					if (MemberIsHidden(pd, context.Settings))
						continue;
					if (pd.GetMethod is not null || pd.SetMethod is not null)
						astType.Members.Add(CreateProperty(pd));

					// Methods marked as 'other' are not supported in C#
					for (int i = 0; i < pd.OtherMethods.Count; i++) {
						var otherMethod = pd.OtherMethods[i];
						if (otherMethod.DeclaringType != pd.DeclaringType)
							continue;
						astType.Members.Add(CreateMethod(otherMethod));
					}

					continue;
				}

				var ed = def as EventDef;
				if (ed != null) {
					if (ed.AddMethod is not null || ed.RemoveMethod is not null)
						astType.Members.Add(CreateEvent(ed));

					// Methods marked as 'fire' are not supported in C#
					if (ed.InvokeMethod is not null && ed.InvokeMethod.DeclaringType == ed.DeclaringType)
						astType.Members.Add(CreateMethod(ed.InvokeMethod));

					// Methods marked as 'other' are not supported in C#
					for (int i = 0; i < ed.OtherMethods.Count; i++) {
						var otherMethod = ed.OtherMethods[i];
						if (otherMethod.DeclaringType != ed.DeclaringType)
							continue;
						astType.Members.Add(CreateMethod(otherMethod));
					}

					continue;
				}

				Debug.Fail("Shouldn't be here");
			}
		}

		EntityDeclaration CreateMethod(MethodDef methodDef)
		{
			MethodDeclaration astMethod = new MethodDeclaration();
			EntityDeclaration returnValue = astMethod;
			astMethod.AddAnnotation(methodDef);
			astMethod.ReturnType = ConvertType(methodDef.ReturnType, stringBuilder, methodDef.Parameters.ReturnParameter.ParamDef);
			string name = methodDef.Name;
			if (IsExplicitInterfaceImplementation(methodDef)) {
				var lastDot = name.LastIndexOf('.');
				if (lastDot >= 0)
					name = name.Substring(lastDot + 1);
			}
			astMethod.NameToken = Identifier.Create(name).WithAnnotation(methodDef);
			astMethod.TypeParameters.AddRange(MakeTypeParameters(methodDef.GenericParameters));
			astMethod.Parameters.AddRange(MakeParameters(Context.MetadataTextColorProvider, methodDef, context.Settings, stringBuilder));
			bool createMethodBody = false;
			// constraints for override and explicit interface implementation methods are inherited from the base method, so they cannot be specified directly
			if (!methodDef.IsVirtual || (methodDef.IsNewSlot && !methodDef.IsPrivate)) astMethod.Constraints.AddRange(MakeConstraints(methodDef.GenericParameters));
			if (!methodDef.DeclaringType.IsInterface) {
				if (IsExplicitInterfaceImplementation(methodDef)) {
					var methDecl = methodDef.Overrides.First().MethodDeclaration;
					astMethod.PrivateImplementationType = ConvertType(methDecl == null ? null : methDecl.DeclaringType, stringBuilder);
					if (IsReadonlyMember(methodDef))
						astMethod.Modifiers |= Modifiers.ReadonlyMember;
				} else {
					astMethod.Modifiers = ConvertModifiers(methodDef, true);
					if (methodDef.IsVirtual == methodDef.IsNewSlot)
						SetNewModifier(astMethod);
				}
				createMethodBody = true;
			} else if (methodDef.IsStatic) {
				// decompile static method in interface
				astMethod.Modifiers = ConvertModifiers(methodDef, true);
				createMethodBody = true;
			} else {
				createMethodBody = true;
			}

			OperatorDeclaration op = null;
			OperatorType? opType = null;
			if (methodDef.IsSpecialName && !methodDef.HasGenericParameters) {
				opType = OperatorDeclaration.GetOperatorType(methodDef.Name);
				if (opType != null)
					op = new OperatorDeclaration();
			}

			if (createMethodBody) {
				if (op != null)
					AddMethodBody(op, out _, methodDef, astMethod.Parameters, false, MethodKind.Method);
				else
					AddMethodBody(astMethod, out returnValue, methodDef, astMethod.Parameters, false, MethodKind.Method);
			}
			else {
				ClearCurrentMethodState();
				ConvertAttributes(astMethod, methodDef);
			}
			if (astMethod.Parameters.Count > 0) {
				if (methodDef.IsDefined(systemRuntimeCompilerServicesString, extensionAttributeString))
					astMethod.Parameters.First().ParameterModifier = ParameterModifier.This;
			}

			// Convert MethodDeclaration to OperatorDeclaration if possible
			if (op != null) {
				op.CopyAnnotationsFrom(astMethod);
				op.ReturnType = astMethod.ReturnType.Detach();
				op.OperatorType = opType.Value;
				op.Modifiers = astMethod.Modifiers;
				astMethod.Parameters.MoveTo(op.Parameters);
				astMethod.Attributes.MoveTo(op.Attributes);
				AddComment(op, methodDef);
				return op;
			}
			if (DnlibExtensions.HasIsReadOnlyAttribute(methodDef.Parameters.ReturnParameter.ParamDef))
				astMethod.Modifiers |= Modifiers.Readonly;
			AddComment(returnValue, methodDef);

			if (methodDef.IsOther)
				returnValue.InsertChildAfter(null, new Comment(" Note: this method is marked as 'other'."), Roles.Comment);
			else if (methodDef.IsFire)
				returnValue.InsertChildAfter(null, new Comment(" Note: this method is marked as 'fire'."), Roles.Comment);

			return returnValue;
		}

		bool IsExplicitInterfaceImplementation(MethodDef methodDef)
		{
			return methodDef != null && methodDef.HasOverrides && methodDef.IsPrivate;
		}

		IEnumerable<TypeParameterDeclaration> MakeTypeParameters(IList<GenericParam> genericParameters) {
			for (int i = 0; i < genericParameters.Count; i++) {
				var gp = genericParameters[i];
				TypeParameterDeclaration tp = new TypeParameterDeclaration();
				tp.AddAnnotation(gp);
				tp.NameToken = Identifier.Create(gp.Name).WithAnnotation(Context.MetadataTextColorProvider.GetColor(gp));
				if (gp.IsContravariant)
					tp.Variance = VarianceModifier.Contravariant;
				else if (gp.IsCovariant)
					tp.Variance = VarianceModifier.Covariant;
				ConvertCustomAttributes(Context.MetadataTextColorProvider, tp, gp, context.Settings, stringBuilder);
				yield return tp;
			}
		}

		IEnumerable<Constraint> MakeConstraints(IList<GenericParam> genericParameters) {
			for (int i = 0; i < genericParameters.Count; i++) {
				var gp = genericParameters[i];
				Constraint c = new Constraint();
				c.TypeParameter = new SimpleType(gp.Name).WithAnnotation(gp);
				c.TypeParameter.IdentifierToken.WithAnnotation(gp);
				// class/struct must be first
				if (gp.HasReferenceTypeConstraint)
					c.BaseTypes.Add(new PrimitiveType("class"));
				if (gp.HasNotNullableValueTypeConstraint)
					c.BaseTypes.Add(new PrimitiveType("struct"));

				for (int j = 0; j < gp.GenericParamConstraints.Count; j++) {
					var constraintType = gp.GenericParamConstraints[j];
					if (constraintType.Constraint == null)
						continue;
					if (gp.HasNotNullableValueTypeConstraint && constraintType.Constraint.Compare(systemString, valueTypeString))
						continue;
					c.BaseTypes.Add(ConvertType(constraintType.Constraint, stringBuilder));
				}

				if (gp.HasDefaultConstructorConstraint && !gp.HasNotNullableValueTypeConstraint)
					c.BaseTypes.Add(new PrimitiveType("new")); // new() must be last
				if (c.BaseTypes.Any())
					yield return c;
			}
		}
		static readonly UTF8String valueTypeString = new UTF8String("ValueType");

		ConstructorDeclaration CreateConstructor(MethodDef methodDef)
		{
			ConstructorDeclaration astMethod = new ConstructorDeclaration();
			astMethod.AddAnnotation(methodDef);
			astMethod.Modifiers = ConvertModifiers(methodDef, false);
			if (methodDef.IsStatic) {
				// don't show visibility for static ctors
				astMethod.Modifiers &= ~Modifiers.VisibilityMask;
			}
			astMethod.NameToken = Identifier.Create(NRefactory.TypeSystem.ReflectionHelper.SplitTypeParameterCountFromReflectionName(methodDef.DeclaringType.Name)).WithAnnotation(methodDef.DeclaringType);
			astMethod.Parameters.AddRange(MakeParameters(Context.MetadataTextColorProvider, methodDef, context.Settings, stringBuilder));
			AddMethodBody(astMethod, out _, methodDef, astMethod.Parameters, false, MethodKind.Method);
			if (methodDef.IsStatic && methodDef.DeclaringType.IsBeforeFieldInit) {
				astMethod.InsertChildAfter(null, new Comment(" Note: this type is marked as 'beforefieldinit'."), Roles.Comment);
			}
			AddComment(astMethod, methodDef);
			return astMethod;
		}

		// Nu mai e chemata de nicaieri: CreateProperty, singurul ei client, foloseste acum rangurile de
		// mai jos. O lasam in fisier fiindca e cod din amonte si o metoda privata nefolosita nu produce
		// niciun diagnostic de compilator.
		Modifiers FixUpVisibility(Modifiers m)
		{
			Modifiers v = m & Modifiers.VisibilityMask;
			// If any of the modifiers is public, use that
			if ((v & Modifiers.Public) == Modifiers.Public)
				return Modifiers.Public | (m & ~Modifiers.VisibilityMask);
			// If both modifiers are private, no need to fix anything
			if (v == Modifiers.Private || v == (Modifiers.Private | Modifiers.Protected))
				return m;
			// Otherwise, use the other modifiers (internal and/or protected)
			return m & ~Modifiers.Private;
		}

		// Vizibilitatea unei proprietati nu se poate calcula prin SAU pe biti, asa cum face
		// FixUpVisibility(getterModifiers | setterModifiers). Un `protected get` da bitul Protected, un
		// `private set` da bitul Private, iar SAU-ul lor e exact acelasi tipar de biti ca al unui SINGUR
		// accesor FamANDAssem, adica `private protected`. FixUpVisibility are o ramura care lasa
		// Private|Protected neatins, tocmai pentru cazul FamANDAssem, deci proprietatea iesea declarata
		// `private protected` si apoi ambele accesorii primeau modificator, fiindca niciunul nu se
		// potrivea cu vizibilitatea proprietatii. Rezultatul, de sapte ori in exportul masurat:
		// `private protected T P { protected get; private set; }` - in acelasi timp CS0274 (modificator
		// pe amandoua accesoriile) si CS0273 (`protected` nu e mai restrictiv decat `private protected`).
		// Regula din C# e alta: proprietatea are vizibilitatea celui mai permisiv accesor, si cel mult
		// un accesor - cel strict mai restrictiv - isi pune modificator propriu. Rangurile de mai jos
		// exprima ordinea aia, deci alegem un accesor intreg, nu o reuniune de biti fara sens.
		static int AccessibilityRank(Modifiers m)
		{
			Modifiers v = m & Modifiers.VisibilityMask;
			if ((v & Modifiers.Public) != 0)
				return 5;
			if (v == (Modifiers.Protected | Modifiers.Internal))
				return 4;
			if (v == Modifiers.Protected)
				return 3;
			if (v == Modifiers.Internal)
				return 2;
			if (v == (Modifiers.Private | Modifiers.Protected))
				return 1;
			// Private, si tot aici cade si Modifiers.None, adica un accesor privat cand
			// MemberAddPrivateModifier e oprit si modificatorul nu se mai scrie deloc.
			return 0;
		}

		// `protected` si `internal` nu sunt comparabile intre ele: niciunul nu include pe celalalt, asa
		// ca niciunul nu e "mai restrictiv" in sensul cerut de C#. Perechea aia nu se poate scrie in C#
		// (ar cere modificator pe amandoua accesoriile), asa ca o tratam ca necomparabila: accesoriul
		// ramane fara modificator si se largeste tacit la vizibilitatea proprietatii. Preferam largirea
		// in locul unui CS0274 garantat; cazul nu apare in exportul masurat (toate cele sapte perechi
		// sunt protected/private, comparabile).
		static bool IsMoreRestrictive(int accessorRank, int propertyRank)
		{
			if (accessorRank == 2 && propertyRank == 3)
				return false;
			if (accessorRank == 3 && propertyRank == 2)
				return false;
			return accessorRank < propertyRank;
		}

		// Alege intregul set de biti de vizibilitate al accesorului mai permisiv. Cand unul dintre
		// accesorii lipseste, ii dam rangul -1 ca sa castige mereu celalalt.
		static Modifiers MostAccessible(Modifiers a, int rankA, Modifiers b, int rankB)
		{
			return rankA >= rankB ? (a & Modifiers.VisibilityMask) : (b & Modifiers.VisibilityMask);
		}

		EntityDeclaration CreateProperty(PropertyDef propDef)
		{
			PropertyDeclaration astProp = new PropertyDeclaration();
			astProp.AddAnnotation(propDef);
			var accessor = propDef.GetMethod ?? propDef.SetMethod;
			Modifiers getterModifiers = Modifiers.None;
			Modifiers setterModifiers = Modifiers.None;
			// -1 inseamna "accesorul nu exista", ca sa castige mereu celalalt la comparatia de rang.
			int getterRank = -1;
			int setterRank = -1;
			string name = propDef.Name;
			if (IsExplicitInterfaceImplementation(accessor)) {
				var methDecl = accessor.Overrides.First().MethodDeclaration;
				astProp.PrivateImplementationType = ConvertType(methDecl == null ? null : methDecl.DeclaringType, stringBuilder);
				var lastDot = name.LastIndexOf('.');
				if (lastDot >= 0)
					name = name.Substring(lastDot + 1);
			} else if (!propDef.DeclaringType.IsInterface) {
				getterModifiers = ConvertModifiers(propDef.GetMethod, true);
				setterModifiers = ConvertModifiers(propDef.SetMethod, true);
				if (propDef.GetMethod != null)
					getterRank = AccessibilityRank(getterModifiers);
				if (propDef.SetMethod != null)
					setterRank = AccessibilityRank(setterModifiers);
				// Bitii care nu tin de vizibilitate (static, virtual, override, extern, readonly...) se
				// aduna in continuare din ambele accesorii; numai vizibilitatea se alege, nu se aduna.
				astProp.Modifiers = ((getterModifiers | setterModifiers) & ~Modifiers.VisibilityMask)
					| MostAccessible(getterModifiers, getterRank, setterModifiers, setterRank);
				try {
					if (accessor != null && accessor.IsVirtual && !accessor.IsNewSlot && (propDef.GetMethod == null || propDef.SetMethod == null)) {
						foreach (var basePropDef in TypesHierarchyHelpers.FindBaseProperties(propDef)) {
							if (basePropDef.GetMethod != null && basePropDef.SetMethod != null) {
								// Aceeasi capcana ca mai sus: reuniunea de biti a celor doua accesorii ale
								// proprietatii de baza putea da Private|Protected fara ca vreun accesor sa fie
								// FamANDAssem, si proprietatea derivata iesea `private protected` cu un accesor
								// `protected` deasupra - CS0273. Aici proprietatea are un singur accesor, deci
								// CS0274 nu poate aparea, dar CS0273 da.
								var baseGetterModifiers = ConvertModifiers(basePropDef.GetMethod, true);
								var baseSetterModifiers = ConvertModifiers(basePropDef.SetMethod, true);
								var propVisibility = MostAccessible(baseGetterModifiers, AccessibilityRank(baseGetterModifiers),
									baseSetterModifiers, AccessibilityRank(baseSetterModifiers));
								astProp.Modifiers = (astProp.Modifiers & ~Modifiers.VisibilityMask) | propVisibility;
								break;
							} else {
								var baseAcc = basePropDef.GetMethod ?? basePropDef.SetMethod;
								if (baseAcc != null && baseAcc.IsNewSlot)
									break;
							}
						}
					}
				} catch (ResolveException) {
					// TODO: add some kind of notification (a comment?) about possible problems with decompiled code due to unresolved references.
				}
			}
			// Pe ramura de interfata si pe cea de implementare explicita accesorii nu primesc modificatori
			// deloc, deci raman pe rangul lui Modifiers.None. -1 ramane rezervat accesorului care lipseste.
			if (propDef.GetMethod != null && getterRank < 0)
				getterRank = AccessibilityRank(getterModifiers);
			if (propDef.SetMethod != null && setterRank < 0)
				setterRank = AccessibilityRank(setterModifiers);

			astProp.NameToken = Identifier.Create(name).WithAnnotation(propDef);
			astProp.ReturnType = ConvertType(propDef.PropertySig.GetRetType(), stringBuilder, propDef);

			// Se ia din astProp.Modifiers, nu din max(getterRank, setterRank), fiindca blocul de mai sus
			// poate sa fi inlocuit vizibilitatea cu cea a proprietatii de baza.
			int propertyRank = AccessibilityRank(astProp.Modifiers);

			if (propDef.GetMethod != null) {
				astProp.Getter = new Accessor();
				AddMethodBody(astProp.Getter, out _, propDef.GetMethod, null, false, MethodKind.Property);
				astProp.Getter.AddAnnotation(propDef.GetMethod);
				astProp.Getter.Modifiers = getterModifiers & Modifiers.ReadonlyMember;

				// Numai accesoriul strict mai restrictiv primeste modificator. Comparatia veche era pe
				// egalitate de biti, si atunci amandoua accesoriile puteau primi modificator in acelasi
				// timp (CS0274). Cu ranguri, cel putin unul dintre accesorii are rangul proprietatii,
				// deci cel mult unul singur poate fi strict mai mic.
				if (IsMoreRestrictive(getterRank, propertyRank))
					astProp.Getter.Modifiers = getterModifiers & (Modifiers.VisibilityMask | Modifiers.ReadonlyMember);
			}
			if (propDef.SetMethod != null) {
				astProp.Setter = new Accessor();
				AddMethodBody(astProp.Setter, out _, propDef.SetMethod, null, true, MethodKind.Property);
				astProp.Setter.AddAnnotation(propDef.SetMethod);
				astProp.Setter.Modifiers = setterModifiers & Modifiers.ReadonlyMember;
				Parameter lastParam = propDef.SetMethod.Parameters.SkipNonNormal().LastOrDefault();
				if (lastParam != null) {
					ConvertCustomAttributes(Context.MetadataTextColorProvider, astProp.Setter, lastParam.ParamDef, context.Settings, stringBuilder, "param");
				}

				if (IsMoreRestrictive(setterRank, propertyRank))
					astProp.Setter.Modifiers = setterModifiers & (Modifiers.VisibilityMask | Modifiers.ReadonlyMember);
			}
			astProp.Modifiers &= ~Modifiers.ReadonlyMember;
			if (astProp.Setter.IsNull && !astProp.Getter.IsNull && (astProp.Getter.Modifiers & Modifiers.ReadonlyMember) != 0) {
				astProp.Getter.Modifiers &= ~Modifiers.ReadonlyMember;
				astProp.Modifiers |= Modifiers.ReadonlyMember;
			}
			else if (!astProp.Setter.IsNull && astProp.Getter.IsNull && (astProp.Setter.Modifiers & Modifiers.ReadonlyMember) != 0) {
				astProp.Setter.Modifiers &= ~Modifiers.ReadonlyMember;
				astProp.Modifiers |= Modifiers.ReadonlyMember;
			}
			else if (!astProp.Setter.IsNull && !astProp.Getter.IsNull && (astProp.Getter.Modifiers & Modifiers.ReadonlyMember) != 0 && (astProp.Setter.Modifiers & Modifiers.ReadonlyMember) != 0) {
				astProp.Getter.Modifiers &= ~Modifiers.ReadonlyMember;
				astProp.Setter.Modifiers &= ~Modifiers.ReadonlyMember;
				astProp.Modifiers |= Modifiers.ReadonlyMember;
			}
			ConvertCustomAttributes(Context.MetadataTextColorProvider, astProp, propDef, context.Settings, stringBuilder);

			EntityDeclaration member = astProp;
			if(propDef.IsIndexer())
				member = ConvertPropertyToIndexer(astProp, propDef);
			if(accessor != null && !accessor.HasOverrides && accessor.DeclaringType != null && !accessor.DeclaringType.IsInterface)
				if (accessor.IsVirtual == accessor.IsNewSlot)
					SetNewModifier(member);
			if (accessor is not null && DnlibExtensions.HasIsReadOnlyAttribute(accessor.Parameters.ReturnParameter.ParamDef))
				astProp.Modifiers |= Modifiers.Readonly;
			if (propDef.SetMethod != null)
				AddComment(astProp, propDef.SetMethod, "set");
			if (propDef.GetMethod != null)
				AddComment(astProp, propDef.GetMethod, "get");
			AddComment(member, propDef);
			return member;
		}

		IndexerDeclaration ConvertPropertyToIndexer(PropertyDeclaration astProp, PropertyDef propDef)
		{
			var astIndexer = new IndexerDeclaration();
			astIndexer.CopyAnnotationsFrom(astProp);
			astProp.Attributes.MoveTo(astIndexer.Attributes);
			astIndexer.Modifiers = astProp.Modifiers;
			astIndexer.PrivateImplementationType = astProp.PrivateImplementationType.Detach();
			astIndexer.ReturnType = astProp.ReturnType.Detach();
			astIndexer.Getter = astProp.Getter.Detach();
			astIndexer.Setter = astProp.Setter.Detach();
			astIndexer.Parameters.AddRange(MakeParameters(Context.MetadataTextColorProvider, propDef.GetParameters().ToList(), context.Settings, stringBuilder));
			return astIndexer;
		}

		EntityDeclaration CreateEvent(EventDef eventDef)
		{
			if ((eventDef.AddMethod != null && eventDef.AddMethod.IsAbstract) || (eventDef.AddMethod?.Body == null && eventDef.RemoveMethod?.Body == null && eventDef.InvokeMethod?.Body == null)) {
				EventDeclaration astEvent = new EventDeclaration();
				ConvertCustomAttributes(Context.MetadataTextColorProvider, astEvent, eventDef, context.Settings, stringBuilder);
				astEvent.AddAnnotation(eventDef);
				MethodDef accessor = eventDef.AddMethod ?? eventDef.RemoveMethod;
				string name = eventDef.Name;
				if (IsExplicitInterfaceImplementation(accessor)) {
					var lastDot = name.LastIndexOf('.');
					if (lastDot >= 0)
						name = name.Substring(lastDot + 1);
				}
				astEvent.Variables.Add(new VariableInitializer(eventDef, name));
				astEvent.ReturnType = ConvertType(eventDef.EventType, stringBuilder, eventDef);
				if (!eventDef.DeclaringType.IsInterface)
					astEvent.Modifiers = ConvertModifiers(eventDef.AddMethod, true);
				if (eventDef.RemoveMethod != null)
					AddComment(astEvent, eventDef.RemoveMethod, "remove");
				if (eventDef.AddMethod != null)
					AddComment(astEvent, eventDef.AddMethod, "add");
				AddComment(astEvent, eventDef);
				return astEvent;
			} else {
				CustomEventDeclaration astEvent = new CustomEventDeclaration();
				ConvertCustomAttributes(Context.MetadataTextColorProvider, astEvent, eventDef, context.Settings, stringBuilder);
				astEvent.AddAnnotation(eventDef);
				MethodDef accessor = eventDef.AddMethod ?? eventDef.RemoveMethod;
				string name = eventDef.Name;
				if (IsExplicitInterfaceImplementation(accessor)) {
					var lastDot = name.LastIndexOf('.');
					if (lastDot >= 0)
						name = name.Substring(lastDot + 1);
				}
				astEvent.NameToken = Identifier.Create(name).WithAnnotation(eventDef);
				astEvent.ReturnType = ConvertType(eventDef.EventType, stringBuilder, eventDef);
				if (eventDef.AddMethod == null || !IsExplicitInterfaceImplementation(eventDef.AddMethod))
					astEvent.Modifiers = ConvertModifiers(eventDef.AddMethod, true);
				else {
					var methDecl = eventDef.AddMethod.Overrides.First().MethodDeclaration;
					astEvent.PrivateImplementationType = ConvertType(methDecl == null ? null : methDecl.DeclaringType, stringBuilder);
				}

				if (eventDef.AddMethod != null) {
					astEvent.AddAccessor = new Accessor().WithAnnotation(eventDef.AddMethod);
					AddMethodBody(astEvent.AddAccessor, out _, eventDef.AddMethod, null, true, MethodKind.Event);
				}
				if (eventDef.RemoveMethod != null) {
					astEvent.RemoveAccessor = new Accessor().WithAnnotation(eventDef.RemoveMethod);
					AddMethodBody(astEvent.RemoveAccessor, out _, eventDef.RemoveMethod, null, true, MethodKind.Event);
				}
				if (accessor != null && accessor.IsVirtual == accessor.IsNewSlot) {
					SetNewModifier(astEvent);
				}
				if (eventDef.RemoveMethod != null)
					AddComment(astEvent, eventDef.RemoveMethod, "remove");
				if (eventDef.AddMethod != null)
					AddComment(astEvent, eventDef.AddMethod, "add");
				AddComment(astEvent, eventDef);
				return astEvent;
			}
		}

		static MethodBaseSig GetMethodBaseSig(ITypeDefOrRef type, MethodBaseSig msig, IList<TypeSig> methodGenArgs = null)
		{
			IList<TypeSig> typeGenArgs = null;
			var ts = type as TypeSpec;
			if (ts != null) {
				var genSig = ts.TypeSig.ToGenericInstSig();
				if (genSig != null)
					typeGenArgs = genSig.GenericArguments;
			}
			if (typeGenArgs == null && methodGenArgs == null)
				return msig;
			return GenericArgumentResolver.Resolve(msig, typeGenArgs, methodGenArgs);
		}

		void ClearCurrentMethodState() {
			context.CurrentMethodIsAsync = false;
			context.CurrentMethodIsYieldReturn = false;
		}

		// NetSpy: a member with a body can't be 'extern' (CS0179). Clear the modifier on the
		// node and, for property/event accessors, on the owning declaration too.
		static void ClearExternIfHasBody(EntityDeclaration node) {
			if (node is null)
				return;
			node.Modifiers &= ~Modifiers.Extern;
			if (node.Parent is EntityDeclaration owner)
				owner.Modifiers &= ~Modifiers.Extern;
		}

		void AddMethodBody(EntityDeclaration methodNode, out EntityDeclaration updatedNode, MethodDef method, IEnumerable<ParameterDeclaration> parameters, bool valueParameterIsKeyword, MethodKind methodKind) {
			updatedNode = methodNode;
			ClearCurrentMethodState();

			// `protected override void Finalize()` nu se poate scrie asa in C# (CS0249): destructorul se
			// scrie `~Tip()`. Conversia exista de mult in dnSpy, dar statea DOAR pe ramura
			// DecompiledBodyKind.Stub, adica numai pe metodele carora nu li se decompileaza corpul. In
			// exportul masurat toate corpurile vin pe ramura Full, deci conversia nu s-a aplicat
			// niciodata: 0 destructori in 2.675 de fisiere si 3 `override void Finalize` ramase, fiecare
			// cu CS0249. Mutata aici, inainte de switch, se aplica pe orice fel de corp. Tot aici trebuie
			// sa stea si din cauza ramurii asincrone din Full: aceea captureaza methodNode intr-un
			// closure, deci o conversie de dupa ar lipi corpul pe nodul vechi, deja detasat.
			if (methodKind == MethodKind.Method && IsFinalizeOverride(method)) {
				var dd = new DestructorDeclaration();
				dd.AddAnnotation(method);
				methodNode.Attributes.MoveTo(dd.Attributes);
				// Un destructor nu accepta niciun modificator de accesibilitate si nici
				// virtual/override/sealed (CS0106). Masca veche scotea doar Protected si Override, deci
				// un `protected sealed override void Finalize()` ar fi iesit `sealed ~Tip()`. Pastram
				// numai cele doua modificari care sunt legale pe un destructor.
				dd.Modifiers = methodNode.Modifiers & (Modifiers.Extern | Modifiers.Unsafe);
				dd.NameToken = Identifier.Create(NRefactory.TypeSystem.ReflectionHelper.SplitTypeParameterCountFromReflectionName(method.DeclaringType.Name));
				updatedNode = dd;
				methodNode = dd;
			}

			if (method.Body == null) {
				ConvertAttributes(methodNode, method);
				return;
			}

			BlockStatement bs;
			MethodDebugInfoBuilder builder3;
			var bodyKind = GetDecompiledBodyKind?.Invoke(this, method) ?? DecompiledBodyKind.Full;
			// In order for auto events to be optimized from custom to auto events, they must have bodies.
			// DecompileTypeMethodsTransform has a fix to remove the hidden custom events' bodies.
			if (bodyKind == DecompiledBodyKind.Empty && methodKind == MethodKind.Event)
				bodyKind = DecompiledBodyKind.Full;
			switch (bodyKind) {
			case DecompiledBodyKind.Full:
				try {
					if (context.AsyncMethodBodyDecompilation) {
						parameters = parameters?.ToArray();
						var context = this.context.Clone();
						var bodyTask = Task.Run(() => {
							if (context.CancellationToken.IsCancellationRequested)
								return default(AsyncMethodBodyResult);
							var asyncState = GetAsyncMethodBodyDecompilationState();
							var stringBuilder = asyncState.StringBuilder;
							var autoPropertyProvider = new AutoPropertyProvider();
							BlockStatement body;
							MethodDebugInfoBuilder builder2;
							try {
								body = AstMethodBodyBuilder.CreateMethodBody(method, context, autoPropertyProvider, parameters, valueParameterIsKeyword, stringBuilder, out builder2);
							}
							catch (OperationCanceledException) {
								throw;
							}
							catch (Exception ex) {
								CreateBadMethod(context, method, ex, stringBuilder, out body, out builder2);
							}
							Return(asyncState);
							return new AsyncMethodBodyResult(methodNode, method, body, builder2, context.variableMap, context.CurrentMethodIsAsync, context.CurrentMethodIsYieldReturn);
						}, context.CancellationToken);
						methodBodyTasks.Add(bodyTask);
					}
					else {
						var body = AstMethodBodyBuilder.CreateMethodBody(method, context, AutoPropertyProvider, parameters, valueParameterIsKeyword, stringBuilder, out var builder);
						if (context.CurrentMethodIsAsync)
							methodNode.Modifiers |= Modifiers.Async;
						methodNode.SetChildByRole(Roles.Body, body);
						methodNode.AddAnnotation(builder);
						methodNode.AddAnnotation(context.variableMap);
						ConvertAttributes(methodNode, method);
						// NetSpy: a member that has a body can't be 'extern' (CS0179). This happens
						// with Il2Cpp stubs where the method reports no managed body yet a body is
						// still emitted. Clear extern on the accessor and its owning property/event.
						ClearExternIfHasBody(methodNode);
					}
					return;
				}
				catch (OperationCanceledException) {
					throw;
				}
				catch (Exception ex) {
					CreateBadMethod(context, method, ex, stringBuilder, out bs, out builder3);
				}
				methodNode.SetChildByRole(Roles.Body, bs);
				methodNode.AddAnnotation(builder3);
				ConvertAttributes(methodNode, method);
				return;

			case DecompiledBodyKind.Empty:
				bs = new BlockStatement();
				if (method.IsInstanceConstructor) {
					var baseCtor = GetBaseConstructorForEmptyBody(method);
					if (baseCtor != null) {
						var methodSig = GetMethodBaseSig(method.DeclaringType.BaseType, baseCtor.MethodSig);
						var args = new List<Expression>(methodSig.Params.Count);
						for (var i = 0; i < baseCtor.Parameters.Count; i++) {
							var parameter = baseCtor.Parameters[i];
							if (parameter.IsHiddenThisParameter)
								continue;
							args.Add(new DefaultValueExpression(ConvertType(methodSig.Params[parameter.MethodSigIndex], stringBuilder, parameter.ParamDef)));
						}
						var stmt = new ExpressionStatement(new InvocationExpression(new MemberReferenceExpression(new BaseReferenceExpression(), method.Name), args));
						bs.Statements.Add(stmt);
					}
					if (method.DeclaringType.IsValueType && !method.DeclaringType.IsEnum) {
						for (int i = 0; i < method.DeclaringType.Fields.Count; i++) {
							var field = method.DeclaringType.Fields[i];
							if (field.IsStatic)
								continue;
							var defVal = new DefaultValueExpression(ConvertType(field.FieldType, stringBuilder, field));
							var stmt = new ExpressionStatement(new AssignmentExpression(new MemberReferenceExpression(new ThisReferenceExpression(), field.Name), defVal));
							bs.Statements.Add(stmt);
						}
					}
				}
				if (parameters != null) {
					foreach (var p in parameters) {
						if (p.ParameterModifier != ParameterModifier.Out)
							continue;
						var parameter = p.Annotation<Parameter>();
						var astType = ConvertType(parameter.Type, stringBuilder, parameter.ParamDef);
						UndoRefSpecifier(astType);
						var defVal = new DefaultValueExpression(astType);
						var stmt = new ExpressionStatement(new AssignmentExpression(new IdentifierExpression(p.Name), defVal));
						bs.Statements.Add(stmt);
					}
				}
				var returnElementType = method.ReturnType.RemovePinnedAndModifiers().GetElementType();
				if (returnElementType != ElementType.Void) {
					if (returnElementType == ElementType.ByRef) {
						var @throw = new ThrowStatement(new NullReferenceExpression());
						bs.Statements.Add(@throw);
					}
					else {
						var ret = new ReturnStatement(new DefaultValueExpression(ConvertType(method.ReturnType, stringBuilder, method.Parameters.ReturnParameter.ParamDef)));
						bs.Statements.Add(ret);
					}
				}
				// Conversia Finalize -> destructor s-a mutat inaintea switch-ului, ca sa prinda si corpurile
				// decompilate (ramura Full), nu numai stub-urile.
				methodNode.SetChildByRole(Roles.Body, bs);
				ConvertAttributes(methodNode, method);
				return;

			case DecompiledBodyKind.None:
				ConvertAttributes(methodNode, method);
				return;

			default:
				throw new InvalidOperationException();
			}
		}
		static readonly UTF8String name_Finalize = new UTF8String("Finalize");

		// Exact cazul pe care Roslyn il respinge cu CS0249: o metoda care SUPRASCRIE object.Finalize.
		// Nu prinde un `Finalize` nou introdus (IsNewSlot): pe acela C# il accepta si il semnaleaza doar
		// cu avertismentul CS0465, iar transformarea lui in destructor ar schimba intelesul codului si
		// ar putea intra in coliziune cu destructorul real al tipului.
		// Nu prinde nici structurile si interfetele: `~S()` intr-o structura e CS0575, adica am schimba
		// doar codul erorii, nu am repara nimic.
		static bool IsFinalizeOverride(MethodDef method)
		{
			if (method == null || method.DeclaringType == null || method.MethodSig == null)
				return false;
			if (method.Name != name_Finalize)
				return false;
			if (!method.IsVirtual || method.IsNewSlot || method.IsStatic)
				return false;
			if (method.DeclaringType.IsValueType || method.DeclaringType.IsInterface)
				return false;
			if (method.MethodSig.GetParamCount() != 0 || method.HasGenericParameters)
				return false;
			return method.ReturnType.RemovePinnedAndModifiers().GetElementType() == ElementType.Void;
		}

		public static void CreateBadMethod(DecompilerContext context, MethodDef method, Exception ex, StringBuilder sb, out BlockStatement bs, out MethodDebugInfoBuilder builder) {
			sb.Clear();
			sb.AppendLine();
			sb.Append("An exception occurred when decompiling this method (");
			sb.Append(method.MDToken.ToString());
			sb.AppendLine(")");
			sb.AppendLine();
			sb.Append(ex);
			sb.AppendLine();

			bs = new BlockStatement();
			var emptyStmt = new EmptyStatement();
			if (method.Body != null)
				emptyStmt.AddAnnotation(new List<ILSpan>(1) { new ILSpan(0, (uint)method.Body.GetCodeSize()) });
			bs.Statements.Add(emptyStmt);
			bs.InsertChildAfter(null, new Comment(sb.ToString(), CommentType.MultiLine), Roles.Comment);
			builder = new MethodDebugInfoBuilder(context.SettingsVersion, StateMachineKind.None, method, null, method.Body?.Variables.Select(a => new SourceLocal(a, CreateLocalName(a), a.Type, SourceVariableFlags.None)).ToArray(), null, null);
		}

		static string CreateLocalName(Local local) {
			var name = local.Name;
			if (!string.IsNullOrEmpty(name))
				return name;
			return "V_" + local.Index.ToString();
		}

		static MethodDef GetBaseConstructorForEmptyBody(MethodDef method) {
			var baseType = method.DeclaringType.BaseType.ResolveTypeDef();
			if (baseType == null)
				return null;
			return GetAccessibleConstructorForEmptyBody(baseType, method.DeclaringType);
		}

		static MethodDef GetAccessibleConstructorForEmptyBody(TypeDef baseType, TypeDef type) {
			var list = new List<MethodDef>(baseType.FindConstructors());
			if (list.Count == 0)
				return null;
			bool isAssem = baseType.Module.Assembly == type.Module.Assembly || type.Module.Assembly.IsFriendAssemblyOf(baseType.Module.Assembly);
			list.Sort((a, b) => {
				int c = GetAccessForEmptyBody(a, isAssem) - GetAccessForEmptyBody(b, isAssem);
				if (c != 0)
					return c;
				// Don't prefer ref/out ctors
				c = GetParamTypeOrderForEmtpyBody(a) - GetParamTypeOrderForEmtpyBody(b);
				if (c != 0)
					return c;
				return a.Parameters.Count - b.Parameters.Count;
			});
			return list[0];
		}

		static int GetParamTypeOrderForEmtpyBody(MethodDef m) =>
			m.MethodSig.Params.Any(a => a.RemovePinnedAndModifiers() is ByRefSig) ? 1 : 0;

		static int GetAccessForEmptyBody(MethodDef m, bool isAssem) {
			switch (m.Access) {
			case MethodAttributes.Public:			return 0;
			case MethodAttributes.FamORAssem:		return 0;
			case MethodAttributes.Family:			return 0;
			case MethodAttributes.Assembly:			return isAssem ? 0 : 1;
			case MethodAttributes.FamANDAssem:		return isAssem ? 0 : 1;
			case MethodAttributes.Private:			return 2;
			case MethodAttributes.PrivateScope:		return 3;
			default:								return 3;
			}
		}

		static bool HasConstant(IHasConstant hc, out CustomAttribute constantAttribute) {
			constantAttribute = null;
			if (hc.Constant != null)
				return true;
			StringBuilder sb = null;
			for (int i = 0; i < hc.CustomAttributes.Count; i++) {
				var ca = hc.CustomAttributes[i];
				var type = ca.AttributeType;
				while (type != null) {
					sb = sb is null ? new StringBuilder() : sb.Clear();
					var fullName = FullNameFactory.FullName(type, false, null, sb);
					if (fullName == "System.Runtime.CompilerServices.CustomConstantAttribute" ||
						fullName == "System.Runtime.CompilerServices.DecimalConstantAttribute") {
						constantAttribute = ca;
						return true;
					}
					type = type.GetBaseType();
				}
			}
			return false;
		}

		static bool TryGetConstant(IHasConstant hc, out object constant) {
			if (!HasConstant(hc, out var constantAttribute)) {
				constant = null;
				return false;
			}

			if (hc.Constant != null) {
				constant = hc.Constant.Value;
				return true;
			}

			if (constantAttribute != null) {
				if (constantAttribute.TypeFullName == "System.Runtime.CompilerServices.DecimalConstantAttribute") {
					if (TryGetDecimalConstantAttributeValue(constantAttribute, out var decimalValue)) {
						constant = decimalValue;
						return true;
					}
				}
			}

			constant = null;
			return false;
		}

		static bool TryGetDecimalConstantAttributeValue(CustomAttribute ca, out decimal value) {
			value = 0;
			if (ca.ConstructorArguments.Count != 5)
				return false;
			if (!(ca.ConstructorArguments[0].Value is byte scale))
				return false;
			if (!(ca.ConstructorArguments[1].Value is byte sign))
				return false;
			int hi, mid, low;
			if (ca.ConstructorArguments[2].Value is int) {
				if (!(ca.ConstructorArguments[2].Value is int))
					return false;
				if (!(ca.ConstructorArguments[3].Value is int))
					return false;
				if (!(ca.ConstructorArguments[4].Value is int))
					return false;
				hi = (int)ca.ConstructorArguments[2].Value;
				mid = (int)ca.ConstructorArguments[3].Value;
				low = (int)ca.ConstructorArguments[4].Value;
			}
			else if (ca.ConstructorArguments[2].Value is uint) {
				if (!(ca.ConstructorArguments[2].Value is uint))
					return false;
				if (!(ca.ConstructorArguments[3].Value is uint))
					return false;
				if (!(ca.ConstructorArguments[4].Value is uint))
					return false;
				hi = (int)(uint)ca.ConstructorArguments[2].Value;
				mid = (int)(uint)ca.ConstructorArguments[3].Value;
				low = (int)(uint)ca.ConstructorArguments[4].Value;
			}
			else
				return false;
			try {
				value = new decimal(low, mid, hi, sign > 0, scale);
				return true;
			}
			catch (ArgumentOutOfRangeException) {
				return false;
			}
		}

		FieldDeclaration CreateField(FieldDef fieldDef)
		{
			FieldDeclaration astField = new FieldDeclaration();
			astField.AddAnnotation(fieldDef);
			VariableInitializer initializer = new VariableInitializer(fieldDef, fieldDef.Name);
			astField.AddChild(initializer, Roles.Variable);
			astField.ReturnType = ConvertType(fieldDef.FieldType, stringBuilder, fieldDef);
			astField.Modifiers = ConvertModifiers(fieldDef);
			if (TryGetConstant(fieldDef, out var constant)) {
				initializer.Initializer = CreateExpressionForConstant(constant, fieldDef.FieldType, stringBuilder, fieldDef.DeclaringType.IsEnum);
				// Same unresolved-enum hole as in the attribute arguments: with no assembly resolver an enum
				// declared elsewhere does not Resolve(), so MakePrimitive gives up on the member names and leaves
				// a bare int - "const BindingFlags DefaultBindingFlags = 60;" is CS0266 on a field, which is a
				// class-level error and takes the type's 36 methods with it. Only const is cast: C# allows const
				// on primitives, string and enums only, so an unresolved const type IS an enum and the cast is a
				// language rule rather than a guess. A plain field with a constant has no such guarantee - Cpp2IL
				// recovers "Vector3 _targetOffset = 0" too, where a cast would just trade CS0029 for CS0030.
				if (UnresolvedEnumCasts && fieldDef.IsLiteral && !fieldDef.DeclaringType.IsEnum &&
					initializer.Initializer is PrimitiveExpression && IsUnresolvedEnumLikeType(fieldDef.FieldType))
					initializer.Initializer = new CastExpression(ConvertType(fieldDef.FieldType, stringBuilder, fieldDef), initializer.Initializer.Detach());
			}
			ConvertAttributes(Context.MetadataTextColorProvider, astField, fieldDef, context.Settings, stringBuilder);
			if (fieldDef.DeclaringType != null && IsStubbableIterator(fieldDef.DeclaringType))
				RemoveUnwritableAttributes(astField);
			SetNewModifier(astField);

			if (fieldDef.HasFieldRVA) {
				var c = GetInitialValueConstant(fieldDef);
				string commentText = c is not null
					? $" Note: this field is marked with 'hasfieldrva' and has an initial value of '{c}'."
					: " Note: this field is marked with 'hasfieldrva'.";
				astField.InsertChildAfter(null, new Comment(commentText), Roles.Comment);
			}

			AddComment(astField, fieldDef);
			return astField;
		}

		static object GetInitialValueConstant(FieldDef fld) {
			byte[] initVal = fld.InitialValue;
			if (initVal is null)
				return null;
			switch (fld.FieldType.RemovePinnedAndModifiers().GetElementType()) {
			case ElementType.Boolean when initVal.Length == 1:
				return initVal[0] != 0;
			case ElementType.Char when initVal.Length == 2:
				return BitConverter.ToChar(initVal, 0);
			case ElementType.I1 when initVal.Length == 1:
				return (sbyte)initVal[0];
			case ElementType.U1 when initVal.Length == 1:
				return initVal[0];
			case ElementType.I2 when initVal.Length == 2:
				return BitConverter.ToInt16(initVal, 0);
			case ElementType.U2 when initVal.Length == 2:
				return BitConverter.ToUInt16(initVal, 0);
			case ElementType.I4 when initVal.Length == 4:
				return BitConverter.ToInt32(initVal, 0);
			case ElementType.U4 when initVal.Length == 4:
				return BitConverter.ToUInt32(initVal, 0);
			case ElementType.I8 when initVal.Length == 8:
				return BitConverter.ToInt64(initVal, 0);
			case ElementType.U8 when initVal.Length == 8:
				return BitConverter.ToUInt64(initVal, 0);
			case ElementType.R4 when initVal.Length == 4:
				return BitConverter.ToSingle(initVal, 0);
			case ElementType.R8 when initVal.Length == 8:
				return BitConverter.ToDouble(initVal, 0);
			default:
				return null;
			}
		}

		static object ConvertConstant(TypeSig type, object constant)
		{
			if (type == null || constant == null)
				return constant;
			TypeCode c = Type.GetTypeCode(constant.GetType());
			if (c < TypeCode.Char || c > TypeCode.Double)
				return constant;

			c = ToTypeCode(type);
			if (c >= TypeCode.Char && c <= TypeCode.Double)
				return CSharpPrimitiveCast.Cast(c, constant, false);
			return constant;
		}

		static TypeCode ToTypeCode(TypeSig type)
		{
			switch (type.GetElementType()) {
			case ElementType.Boolean: return TypeCode.Boolean;
			case ElementType.Char: return TypeCode.Char;
			case ElementType.I1: return TypeCode.SByte;
			case ElementType.U1: return TypeCode.Byte;
			case ElementType.I2: return TypeCode.Int16;
			case ElementType.U2: return TypeCode.UInt16;
			case ElementType.I4: return TypeCode.Int32;
			case ElementType.U4: return TypeCode.UInt32;
			case ElementType.I8: return TypeCode.Int64;
			case ElementType.U8: return TypeCode.UInt64;
			case ElementType.R4: return TypeCode.Single;
			case ElementType.R8: return TypeCode.Double;
			case ElementType.String: return TypeCode.String;
			case ElementType.Object: return TypeCode.Object;
			}
			return TypeCode.Empty;
		}

		static readonly bool UnresolvedEnumCasts = Environment.GetEnvironmentVariable("CPP2IL_ENUM_CAST") != "0";

		// NetSpy: true when an integer default parameter's type is (or is likely) an enum whose
		// definition can't be resolved, so a bare integer literal would be an invalid enum default.
		static bool IsUnresolvedEnumLikeType(TypeSig type) {
			if (type is null || type.IsPrimitive)
				return false;
			var fn = type.FullName;
			if (fn == "System.String" || fn == "System.Decimal" || fn == "System.Object" || fn == "System.IntPtr" || fn == "System.UIntPtr")
				return false;
			try {
				var td = type.ToTypeDefOrRef()?.ResolveTypeDef();
				if (td is not null && !td.IsEnum)
					return false;
			}
			catch {
			}
			return true;
		}

		static Expression CreateExpressionForConstant(object constant, TypeSig type, StringBuilder sb, bool isEnumMemberDeclaration = false)
		{
			constant = ConvertConstant(type, constant);
			if (constant == null) {
				if (!DnlibExtensions.IsValueType(type) && !(type is GenericSig))
					return new NullReferenceExpression();
				var gis = type as GenericInstSig;
				if (gis == null || !gis.GenericType.IsSystemNullable())
					return new DefaultValueExpression(ConvertType(type, sb));
				return new NullReferenceExpression();
			} else {
				TypeCode c = Type.GetTypeCode(constant.GetType());
				if (c >= TypeCode.SByte && c <= TypeCode.UInt64 && !isEnumMemberDeclaration) {
					return MakePrimitive((long)CSharpPrimitiveCast.Cast(TypeCode.Int64, constant, false), type.ToTypeDefOrRef(), sb);
				} else {
					return new PrimitiveExpression(constant);
				}
			}
		}

		public static IEnumerable<ParameterDeclaration> MakeParameters(MetadataTextColorProvider metadataTextColorProvider, MethodDef method, DecompilerSettings settings, StringBuilder sb, bool isLambda = false)
		{
			var parameters = MakeParameters(metadataTextColorProvider, method.Parameters, settings, sb, isLambda);
			if (method.CallingConvention == dnlib.DotNet.CallingConvention.VarArg ||
				method.CallingConvention == dnlib.DotNet.CallingConvention.NativeVarArg) {
				var pd = new ParameterDeclaration {
					Type = new PrimitiveType("__arglist"),
					NameToken = Identifier.Create("").WithAnnotation(BoxedTextColor.Parameter)
				};
				return parameters.Concat(new[] { pd });
			} else {
				return parameters;
			}
		}

		internal static void UndoRefSpecifier(AstType type) {
			if (type is ComposedType ct && ct.HasRefSpecifier)
				ct.HasRefSpecifier = false;
		}

		static IEnumerable<ParameterDeclaration> MakeParameters(MetadataTextColorProvider metadataTextColorProvider, IList<Parameter> paramCol, DecompilerSettings settings, StringBuilder sb, bool isLambda = false) {
			for (int i = 0; i < paramCol.Count; i++) {
				var paramDef = paramCol[i];
				if (paramDef.IsHiddenThisParameter)
					continue;

				ParameterDeclaration astParam = new ParameterDeclaration();
				astParam.AddAnnotation(paramDef);
				var typeWithoutModifiers = paramDef.Type.RemovePinnedAndModifiers();
				if (!(isLambda && typeWithoutModifiers.ContainsAnonymousType()))
					astParam.Type = ConvertType(paramDef.Type, sb, paramDef.ParamDef);
				astParam.NameToken = Identifier.Create(paramDef.Name).WithAnnotation(paramDef);

				if (typeWithoutModifiers is ByRefSig) {
					var pd = paramDef.ParamDef;
					if (pd == null)
						astParam.ParameterModifier = ParameterModifier.Ref;
					else if (!pd.IsIn && pd.IsOut)
						astParam.ParameterModifier = ParameterModifier.Out;
					else if (DnlibExtensions.HasIsReadOnlyAttribute(pd))
						astParam.ParameterModifier = ParameterModifier.In;
					else
						astParam.ParameterModifier = ParameterModifier.Ref;
					UndoRefSpecifier(astParam.Type);
				}

				if (paramDef.HasParamDef) {
					if (paramDef.ParamDef.IsDefined(systemString, paramArrayAttributeString))
						astParam.ParameterModifier = ParameterModifier.Params;
				}
				if (paramDef.HasParamDef && paramDef.ParamDef.IsOptional && TryGetConstant(paramDef.ParamDef, out var constant)) {
					var defExpr = CreateExpressionForConstant(constant, typeWithoutModifiers, sb);
					// NetSpy: when the parameter type is an enum whose definition can't be
					// resolved (eg. an Il2Cpp/Unity type with no reference), CreateExpressionForConstant
					// emits a bare integer like "0", which C# rejects as an enum default (CS1750).
					// Wrap it in an explicit cast so the exported code still compiles.
					if (defExpr is PrimitiveExpression && IsUnresolvedEnumLikeType(typeWithoutModifiers))
						defExpr = new CastExpression(ConvertType(typeWithoutModifiers, sb, paramDef.ParamDef), defExpr);
					astParam.DefaultExpression = defExpr;
				}

				ConvertCustomAttributes(metadataTextColorProvider, astParam, paramDef.ParamDef, settings, sb);
				yield return astParam;
			}
		}
		static readonly UTF8String paramArrayAttributeString = new UTF8String("ParamArrayAttribute");

		#region ConvertAttributes
		void ConvertAttributes(EntityDeclaration attributedNode, TypeDef typeDef)
		{
			ConvertCustomAttributes(Context.MetadataTextColorProvider, attributedNode, typeDef, context.Settings, stringBuilder);
		}

		void ConvertAttributes(EntityDeclaration attributedNode, MethodDef methodDef)
		{
			ConvertAttributes(attributedNode, methodDef, context.CurrentMethodIsAsync, context.CurrentMethodIsYieldReturn);
		}

		void ConvertAttributes(EntityDeclaration attributedNode, MethodDef methodDef, bool methodIsAsync, bool methodIsIterator)
		{
			var options = ConvertCustomAttributesFlags.None;
			if (methodIsAsync)
				options |= ConvertCustomAttributesFlags.IsAsync;
			if (methodIsIterator)
				options |= ConvertCustomAttributesFlags.IsYieldReturn;
			ConvertCustomAttributes(Context.MetadataTextColorProvider, attributedNode, methodDef, context.Settings, stringBuilder, options: options);
			ConvertAttributes(attributedNode, methodDef.Parameters.ReturnParameter);
		}

		void ConvertAttributes(EntityDeclaration attributedNode, Parameter methodReturnType)
		{
			ConvertCustomAttributes(Context.MetadataTextColorProvider, attributedNode, methodReturnType.ParamDef, context.Settings, stringBuilder, "return");
		}

		internal static void ConvertAttributes(MetadataTextColorProvider metadataTextColorProvider, EntityDeclaration attributedNode, FieldDef fieldDef, DecompilerSettings settings, StringBuilder sb, string attributeTarget = null)
		{
			ConvertCustomAttributes(metadataTextColorProvider, attributedNode, fieldDef, settings, sb, attributeTarget);
		}

		static IEnumerable<CustomAttribute> SortCustomAttributes(IHasCustomAttribute customAttributeProvider, bool sort, StringBuilder sb)
		{
			var cas = customAttributeProvider.GetCustomAttributes().ToList();
			if (customAttributeProvider is AssemblyDef) {
				// Always sort these pseudo custom attributes
				if (cas.Any(IsTypeForwardedToAttribute)) {
					var newCas = new List<CustomAttribute>(cas.Where(a => !IsTypeForwardedToAttribute(a)));
					var tft = new List<CustomAttribute>(cas.Where(IsTypeForwardedToAttribute));
					tft.Sort(CompareTypeForwardedToAttributes);
					newCas.AddRange(tft);
					cas = newCas;
				}
			}
			if (!sort)
				return cas;
			return cas.OrderBy(a => FullNameFactory.FullName(a.AttributeType, false, null, sb.Clear()));
		}

		static bool IsTypeForwardedToAttribute(CustomAttribute ca) => IsTypeForwardedToAttribute(ca, out _);
		static bool IsTypeForwardedToAttribute(CustomAttribute ca, out ITypeDefOrRef type) {
			type = null;
			if (ca.TypeFullName != "System.Runtime.CompilerServices.TypeForwardedToAttribute")
				return false;
			if (ca.ConstructorArguments.Count != 1)
				return false;
			return ca.ConstructorArguments[0].Value is TypeDefOrRefSig tdrs && !((type = tdrs.TypeDefOrRef) is null);
		}

		static int CompareTypeForwardedToAttributes(CustomAttribute x, CustomAttribute y) {
			Debug.Assert(IsTypeForwardedToAttribute(x));
			Debug.Assert(IsTypeForwardedToAttribute(y));
			return CompareExportedTypes(((TypeDefOrRefSig)x.ConstructorArguments[0].Value).TypeDefOrRef, ((TypeDefOrRefSig)y.ConstructorArguments[0].Value).TypeDefOrRef);
		}

		static int CompareExportedTypes(ITypeDefOrRef x, ITypeDefOrRef y) {
			var xasm = x.DefinitionAssembly;
			var yasm = y.DefinitionAssembly;
			var sb = new StringBuilder();
			int c = StringComparer.OrdinalIgnoreCase.Compare(FullNameFactory.AssemblyFullName(xasm, true, sb), FullNameFactory.AssemblyFullName(yasm, true, sb.Clear()));
			if (c != 0)
				return c;
			c = StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name);
			if (c != 0)
				return c;
			return x.MDToken.CompareTo(y.MDToken);
		}

		[Flags]
		enum ConvertCustomAttributesFlags {
			None = 0,
			IsAsync = 1,
			IsYieldReturn = 2,
		}
		static readonly UTF8String extensionAttributeString = new UTF8String("ExtensionAttribute");
		static readonly UTF8String systemDiagnosticsString = new UTF8String("System.Diagnostics");
		static readonly UTF8String debuggerStepThroughAttributeString = new UTF8String("DebuggerStepThroughAttribute");
		static readonly UTF8String debuggerHiddenAttributeString = new UTF8String("DebuggerHiddenAttribute");
		static readonly UTF8String asyncStateMachineAttributeString = new UTF8String("AsyncStateMachineAttribute");
		static readonly UTF8String iteratorStateMachineAttributeString = new UTF8String("IteratorStateMachineAttribute");
		static readonly UTF8String isReadOnlyAttributeString = new UTF8String("IsReadOnlyAttribute");
		static readonly UTF8String isByRefLikeAttributeString = new UTF8String("IsByRefLikeAttribute");
		static readonly UTF8String obsoleteAttributeString = new UTF8String("ObsoleteAttribute");

		// Roslyn owns a handful of attributes outright: it synthesises them itself and refuses the source that
		// writes them - CS8138 for TupleElementNames, CS8623 for Nullable, CS8335 for NullableContext /
		// NativeInteger / IsReadOnly / IsUnmanaged / RequiresLocation, CS1970 for Dynamic. None of them carries
		// anything a recompile needs: the tuple element names, the nullable annotations and the readonly/unmanaged
		// markers are all re-derived from the syntax. Resolving them is not an option either - Roslyn hides a type
		// marked [Microsoft.CodeAnalysis.Embedded] from every assembly but its own, so the Nullable pair that
		// Assembly-CSharp does declare still reads as CS0234 from a sibling batch, and IL2CPP stripped the rest.
		// Emitting one is therefore an error whatever the reference set holds, and an error on a member
		// declaration is class-level: it takes down every method in the type, not just its own line.
		// Contrast [StructLayout] and [MethodImpl], equally unresolvable here but carrying real semantics
		// (layout/size, inlining) - those want their attribute type injected, not the attribute dropped.
		static readonly bool DropReservedAttributes = Environment.GetEnvironmentVariable("CPP2IL_DROP_RESERVED_ATTRS") != "0";
		static readonly UTF8String tupleElementNamesAttributeString = new UTF8String("TupleElementNamesAttribute");
		static readonly UTF8String nullableAttributeString = new UTF8String("NullableAttribute");
		static readonly UTF8String nullableContextAttributeString = new UTF8String("NullableContextAttribute");
		static readonly UTF8String nativeIntegerAttributeString = new UTF8String("NativeIntegerAttribute");
		static readonly UTF8String isUnmanagedAttributeString = new UTF8String("IsUnmanagedAttribute");
		static readonly UTF8String requiresLocationAttributeString = new UTF8String("RequiresLocationAttribute");

		static bool IsCompilerReservedAttribute(ITypeDefOrRef attributeType)
		{
			return attributeType.Compare(systemRuntimeCompilerServicesString, tupleElementNamesAttributeString)
				|| attributeType.Compare(systemRuntimeCompilerServicesString, nullableAttributeString)
				|| attributeType.Compare(systemRuntimeCompilerServicesString, nullableContextAttributeString)
				|| attributeType.Compare(systemRuntimeCompilerServicesString, nativeIntegerAttributeString)
				|| attributeType.Compare(systemRuntimeCompilerServicesString, isReadOnlyAttributeString)
				|| attributeType.Compare(systemRuntimeCompilerServicesString, isUnmanagedAttributeString)
				|| attributeType.Compare(systemRuntimeCompilerServicesString, requiresLocationAttributeString)
				|| attributeType.Compare(systemRuntimeCompilerServicesString, dynamicAttributeString);
		}

		static void ConvertCustomAttributes(MetadataTextColorProvider metadataTextColorProvider, AstNode attributedNode, IHasCustomAttribute customAttributeProvider, DecompilerSettings settings, StringBuilder sb, string attributeTarget = null, ConvertCustomAttributesFlags options = ConvertCustomAttributesFlags.None)
		{
			if (customAttributeProvider != null) {
				EntityDeclaration entityDecl = attributedNode as EntityDeclaration;
				var attributes = new List<ICSharpCode.NRefactory.CSharp.Attribute>();
				bool isType = attributedNode is TypeDeclaration;
				bool isParameter = attributedNode is ParameterDeclaration;
				bool isMethod = attributedNode is MethodDeclaration || attributedNode is Accessor;
				bool isProperty = attributedNode is PropertyDeclaration;
				bool onePerLine = attributeTarget == "module" || attributeTarget == "assembly" || isParameter ||
					// Params ignore the option
					(settings.OneCustomAttributePerLine && entityDecl != null);
				bool isAsync = (options & ConvertCustomAttributesFlags.IsAsync) != 0;
				bool isYieldReturn = (options & ConvertCustomAttributesFlags.IsYieldReturn) != 0;
				bool isReturnTarget = attributeTarget == "return";
				bool removeObsoleteAttr = false;
				foreach (var customAttribute in SortCustomAttributes(customAttributeProvider, settings.SortCustomAttributes, sb)) {
					var attributeType = customAttribute.AttributeType;
					if (attributeType == null)
						continue;
					if (DropReservedAttributes && IsCompilerReservedAttribute(attributeType))
						continue;
					if (attributeType.Compare(systemRuntimeCompilerServicesString, extensionAttributeString)) {
						// don't show the ExtensionAttribute (it's converted to the 'this' modifier)
						continue;
					}
					if (attributeType.Compare(systemString, paramArrayAttributeString)) {
						// don't show the ParamArrayAttribute (it's converted to the 'params' modifier)
						continue;
					}
					if ((isYieldReturn || isAsync) && attributeType.Compare(systemDiagnosticsString, debuggerStepThroughAttributeString))
						continue;
					if ((isYieldReturn || isAsync) && attributeType.Compare(systemDiagnosticsString, debuggerHiddenAttributeString))
						continue;
					if (isParameter && (attributeType.Compare(systemRuntimeCompilerServicesString, dynamicAttributeString) || attributeType.Compare(systemRuntimeCompilerServicesString, isReadOnlyAttributeString)))
						continue;
					if (isMethod && (attributeType.Compare(systemRuntimeCompilerServicesString, iteratorStateMachineAttributeString) || attributeType.Compare(systemRuntimeCompilerServicesString, asyncStateMachineAttributeString)))
						continue;
					if (isType) {
						if (attributeType.Compare(systemRuntimeCompilerServicesString, isReadOnlyAttributeString))
							continue;
						if (attributeType.Compare(systemRuntimeCompilerServicesString, isByRefLikeAttributeString)) {
							removeObsoleteAttr = true;
							continue;
						}
						if (removeObsoleteAttr && attributeType.Compare(systemString, obsoleteAttributeString))
							continue;
					}
					if (isProperty && attributeType.Compare(systemRuntimeCompilerServicesString, isReadOnlyAttributeString))
						continue;
					if (isReturnTarget && attributeType.Compare(systemRuntimeCompilerServicesString, isReadOnlyAttributeString))
						continue;
					if (isMethod && attributeType.Compare(systemRuntimeCompilerServicesString, isReadOnlyAttributeString))
						continue;

					var attribute = new ICSharpCode.NRefactory.CSharp.Attribute();
					attribute.AddAnnotation(customAttribute);
					attribute.Type = ConvertType(attributeType, sb);
					attributes.Add(attribute);

					SimpleType st = attribute.Type as SimpleType;
					if (st != null && st.Identifier.EndsWith("Attribute", StringComparison.Ordinal)) {
						var id = Identifier.Create(st.Identifier.Substring(0, st.Identifier.Length - "Attribute".Length));
						id.AddAnnotationsFrom(st.IdentifierToken);
						st.IdentifierToken = id;
					}

					if (customAttribute.IsRawBlob) {
						var emptyExpression = new ErrorExpression();
						emptyExpression.AddChild(new Comment("Failed to decode CustomAttribute blob!", CommentType.MultiLine), Roles.Comment);
						attribute.Arguments.Add(emptyExpression);
					}
					else {
						if (customAttribute.HasConstructorArguments) {
							for (int i = 0; i < customAttribute.ConstructorArguments.Count; i++)
								attribute.Arguments.Add(ConvertArgumentValue(customAttribute.ConstructorArguments[i], sb));
						}
						if (customAttribute.HasNamedArguments) {
							TypeDef resolvedAttributeType = attributeType.ResolveTypeDef();

							for (var i = 0; i < customAttribute.NamedArguments.Count; i++) {
								var namedArgument = customAttribute.NamedArguments[i];

								IdentifierExpression memberName;
								if (namedArgument.IsField) {
									var fieldReference = GetField(resolvedAttributeType, namedArgument.Name);
									memberName = IdentifierExpression.Create(namedArgument.Name, metadataTextColorProvider.GetColor((object)fieldReference ?? BoxedTextColor.InstanceField), true).WithAnnotation(fieldReference);
								}
								else {
									var propertyReference = GetProperty(resolvedAttributeType, namedArgument.Name);
									memberName = IdentifierExpression.Create(namedArgument.Name, metadataTextColorProvider.GetColor((object)propertyReference ?? BoxedTextColor.InstanceProperty), true).WithAnnotation(propertyReference);
								}

								var argumentValue = ConvertArgumentValue(namedArgument.Argument, sb);
								attribute.Arguments.Add(new AssignmentExpression(memberName, argumentValue));
							}
						}
					}
				}

				if (onePerLine) {
					bool isAssembly = attributeTarget == "assembly";
					IAssembly lastAssembly = null;

					// use separate section for each attribute
					for (int i = 0; i < attributes.Count; i++) {
						var attribute = attributes[i];
						if (isAssembly && attribute.Annotation<CustomAttribute>() is CustomAttribute ca && IsTypeForwardedToAttribute(ca, out var exportedType)) {
							if (lastAssembly == null || !AssemblyNameComparer.CompareAll.Equals(exportedType.DefinitionAssembly, lastAssembly)) {
								lastAssembly = exportedType.DefinitionAssembly;
								sb.Clear();
								sb.Append(' ');
								var cmt = new Comment(FullNameFactory.AssemblyFullNameSB(lastAssembly, true, sb).ToString());
								attributedNode.AddChild(cmt, Roles.Comment);
							}
						}

						var section = new AttributeSection();
						section.AttributeTarget = attributeTarget;
						section.Attributes.Add(attribute);
						attributedNode.AddChild(section, EntityDeclaration.AttributeRole);
					}
				} else if (attributes.Count > 0) {
					// use single section for all attributes
					var section = new AttributeSection();
					section.AttributeTarget = attributeTarget;
					section.Attributes.AddRange(attributes);
					attributedNode.AddChild(section, EntityDeclaration.AttributeRole);
				}
			}
		}

		static PropertyDef GetProperty(TypeDef type, UTF8String name)
		{
			while (type != null) {
				for (int i = 0; i < type.Properties.Count; i++) {
					var pd = type.Properties[i];
					if (pd.Name == name)
						return pd;
				}
				type = type.BaseType.ResolveTypeDef();
			}
			return null;
		}

		static FieldDef GetField(TypeDef type, UTF8String name)
		{
			while (type != null) {
				for (int i = 0; i < type.Fields.Count; i++) {
					var fd = type.Fields[i];
					if (fd.Name == name)
						return fd;
				}
				type = type.BaseType.ResolveTypeDef();
			}
			return null;
		}

		private static Expression ConvertArgumentValue(CAArgument argument, StringBuilder sb)
		{
			if (argument.Value is IList<CAArgument> argumentValue) {
				ArrayInitializerExpression arrayInit = new ArrayInitializerExpression();
				for (int i = 0; i < argumentValue.Count; i++)
					arrayInit.Elements.Add(ConvertArgumentValue(argumentValue[i], sb));
				ArraySigBase arrayType = argument.Type as ArraySigBase;
				return new ArrayCreateExpression {
					Type = ConvertType(arrayType != null ? arrayType.Next : argument.Type, sb),
					AdditionalArraySpecifiers = { new ArraySpecifier() },
					Initializer = arrayInit
				};
			}
			if (argument.Value is CAArgument value) {
				// occurs with boxed arguments
				return ConvertArgumentValue(value, sb);
			}
			var type = argument.Type.Resolve();
			if (type != null && type.IsEnum && argument.Value != null) {
				try {
					object argVal;
					if (argument.Value is UTF8String utf8String2) {
						try {
							argVal = Convert.ToInt64(utf8String2.String);
						}
						catch (OverflowException) {
							argVal = Convert.ToUInt64(utf8String2.String);
						}
					}
					else
						argVal = argument.Value;
					long val = (long)CSharpPrimitiveCast.Cast(TypeCode.Int64, argVal, false);
					return MakePrimitive(val, type, sb);
				} catch (SystemException) {
				}
			}
			if (argument.Value is TypeSig sig)
				return CreateTypeOfExpression(sig.ToTypeDefOrRef(), sb);
			if (argument.Value is UTF8String utf8String)
				return new PrimitiveExpression(utf8String.String);
			// Cpp2IL: the blob keeps the real argument type - [DrawIf("JointType", 0L, 1, 3)] really does store
			// arg 3 as Quantum.Inspector.DrawIfHideType - but the module is loaded with no assembly resolver, so
			// an enum declared in a sibling DLL does not Resolve() and the value falls out of the enum branch
			// above as a bare int. C# has no implicit int -> enum conversion, and an attribute sits outside every
			// method body, so the CS1503 is a class-level error: it takes down the whole type, not just its line.
			// The cast is written to the blob's own type, so nothing is guessed at and nothing is lost; the same
			// trick already rescues an unresolved-enum default parameter value from CS1750 in CreateParameters.
			if (argument.Value != null && UnresolvedEnumCasts && IsUnresolvedEnumLikeType(argument.Type))
				return new CastExpression(ConvertType(argument.Type, sb), new PrimitiveExpression(argument.Value));
			return new PrimitiveExpression(argument.Value);
		}
		#endregion

		internal static Expression MakePrimitive(long val, ITypeDefOrRef type, StringBuilder sb)
		{
			if (val == 0 && type.IsSystemBoolean())
				return new Ast.PrimitiveExpression(false);
			else if (val == 1 && type.IsSystemBoolean())
				return new Ast.PrimitiveExpression(true);
			else if (val == 0 && type.TryGetPtrSig() != null)
				return new Ast.NullReferenceExpression();
			if (type != null)
			{ // cannot rely on type.IsValueType, it's not set for typerefs (but is set for typespecs)
				TypeDef enumDefinition = type.ResolveTypeDef();
				if (enumDefinition != null && enumDefinition.IsEnum) {
					TypeCode enumBaseTypeCode = TypeCode.Int32;
					for (int i = 0; i < enumDefinition.Fields.Count; i++) {
						var field = enumDefinition.Fields[i];
						if (field.IsStatic) {
							TryGetConstant(field, out var constant);
							TypeCode c = constant == null ? TypeCode.Empty : Type.GetTypeCode(constant.GetType());
							if (c >= TypeCode.Char && c <= TypeCode.Decimal &&
								object.Equals(CSharpPrimitiveCast.Cast(TypeCode.Int64, constant, false), val))
								return ConvertType(type, sb).Member(field.Name, field).WithAnnotation(field);
						} else if (!field.IsStatic)
							enumBaseTypeCode = TypeAnalysis.GetTypeCode(field.FieldType); // use primitive type of the enum
					}
					if (IsFlagsEnum(enumDefinition)) {
						long enumValue = val;
						Expression expr = null;
						long negatedEnumValue = ~val;
						// limit negatedEnumValue to the appropriate range
						switch (enumBaseTypeCode) {
							case TypeCode.Byte:
							case TypeCode.SByte:
								negatedEnumValue &= byte.MaxValue;
								break;
							case TypeCode.Char:
							case TypeCode.Int16:
							case TypeCode.UInt16:
								negatedEnumValue &= ushort.MaxValue;
								break;
							case TypeCode.Int32:
							case TypeCode.UInt32:
								negatedEnumValue &= uint.MaxValue;
								break;
						}
						Expression negatedExpr = null;
						foreach (FieldDef field in enumDefinition.Fields.Where(fld => fld.IsStatic)) {
							TryGetConstant(field, out var constant);
							TypeCode c = constant == null ? TypeCode.Empty : Type.GetTypeCode(constant.GetType());
							if (c < TypeCode.Char || c > TypeCode.Decimal)
								continue;
							long fieldValue = (long)CSharpPrimitiveCast.Cast(TypeCode.Int64, constant, false);
							if (fieldValue == 0)
								continue;	// skip None enum value

							if ((fieldValue & enumValue) == fieldValue) {
								var fieldExpression = ConvertType(type, sb).Member(field.Name, field).WithAnnotation(field);
								if (expr == null)
									expr = fieldExpression;
								else
									expr = new BinaryOperatorExpression(expr, BinaryOperatorType.BitwiseOr, fieldExpression);

								enumValue &= ~fieldValue;
							}
							if ((fieldValue & negatedEnumValue) == fieldValue) {
								var fieldExpression = ConvertType(type, sb).Member(field.Name, field).WithAnnotation(field);
								if (negatedExpr == null)
									negatedExpr = fieldExpression;
								else
									negatedExpr = new BinaryOperatorExpression(negatedExpr, BinaryOperatorType.BitwiseOr, fieldExpression);

								negatedEnumValue &= ~fieldValue;
							}
						}
						if (enumValue == 0 && expr != null) {
							if (!(negatedEnumValue == 0 && negatedExpr != null && negatedExpr.Descendants.Count() < expr.Descendants.Count())) {
								return expr;
							}
						}
						if (negatedEnumValue == 0 && negatedExpr != null) {
							return new UnaryOperatorExpression(UnaryOperatorType.BitNot, negatedExpr);
						}
					}
					if (enumBaseTypeCode < TypeCode.Char || enumBaseTypeCode > TypeCode.Decimal)
						enumBaseTypeCode = TypeCode.Int32;
					return new Ast.PrimitiveExpression(CSharpPrimitiveCast.Cast(enumBaseTypeCode, val, false)).CastTo(ConvertType(type, sb));
				}
			}
			TypeCode code = TypeAnalysis.GetTypeCode(type.ToTypeSig());
			if (code < TypeCode.Char || code > TypeCode.Decimal)
				code = TypeCode.Int32;
			return new Ast.PrimitiveExpression(CSharpPrimitiveCast.Cast(code, val, false));
		}

		static bool IsFlagsEnum(TypeDef type)
		{
			return type.IsDefined(systemString, flagsAttributeString);
		}
		static readonly UTF8String flagsAttributeString = new UTF8String("FlagsAttribute");

		/// <summary>
		/// Sets new modifier if the member hides some other member from a base type.
		/// </summary>
		/// <param name="member">The node of the member which new modifier state should be determined.</param>
		static void SetNewModifier(EntityDeclaration member)
		{
			try {
				bool addNewModifier = false;
				if (member is IndexerDeclaration) {
					var propertyDef = member.Annotation<PropertyDef>();
					var baseProperties =
						TypesHierarchyHelpers.FindBaseProperties(propertyDef);
					addNewModifier = baseProperties.Any();
				} else
					addNewModifier = HidesBaseMember(member);

				if (addNewModifier)
					member.Modifiers |= Modifiers.New;
			}
			catch (ResolveException) {
				// TODO: add some kind of notification (a comment?) about possible problems with decompiled code due to unresolved references.
			}
		}

		private static bool HidesBaseMember(EntityDeclaration member)
		{
			var memberDefinition = member.Annotation<IMemberDef>();
			bool addNewModifier = false;
			var methodDefinition = memberDefinition as MethodDef;
			if (methodDefinition != null) {
				addNewModifier = HidesByName(memberDefinition, includeBaseMethods: false);
				if (!addNewModifier)
					addNewModifier = TypesHierarchyHelpers.FindBaseMethods(methodDefinition, compareReturnType: false).Any();
			} else
				addNewModifier = HidesByName(memberDefinition, includeBaseMethods: true);
			return addNewModifier;
		}

		/// <summary>
		/// Determines whether any base class member has the same name as the given member.
		/// </summary>
		/// <param name="member">The derived type's member.</param>
		/// <param name="includeBaseMethods">true if names of methods declared in base types should also be checked.</param>
		/// <returns>true if any base member has the same name as given member, otherwise false.</returns>
		static bool HidesByName(IMemberDef member, bool includeBaseMethods)
		{
			if (member == null)
				return false;
			Debug.Assert(!(member is PropertyDef) || !((PropertyDef)member).IsIndexer());

			if (member.DeclaringType.BaseType != null) {
				var baseTypeRef = member.DeclaringType.BaseType;
				while (baseTypeRef != null) {
					var baseType = baseTypeRef.ResolveTypeDef();
					if (baseType == null)
						break;
					if (baseType.HasProperties && AnyIsHiddenBy(baseType.Properties, member, m => !m.IsIndexer()))
						return true;
					if (baseType.HasEvents && AnyIsHiddenBy(baseType.Events, member))
						return true;
					if (baseType.HasFields && AnyIsHiddenBy(baseType.Fields, member))
						return true;
					if (includeBaseMethods && baseType.HasMethods
					    && AnyIsHiddenBy(baseType.Methods, member, m => !m.IsSpecialName))
						return true;
					if (baseType.HasNestedTypes && AnyIsHiddenBy(baseType.NestedTypes, member))
						return true;
					baseTypeRef = baseType.BaseType;
				}
			}
			return false;
		}

		static bool AnyIsHiddenBy<T>(IEnumerable<T> members, IMemberDef derived, Predicate<T> condition = null)
			where T : IMemberDef
		{
			return members.Any(m => m.Name == derived.Name
			                   && (condition == null || condition(m))
			                   && TypesHierarchyHelpers.IsVisibleFromDerived(m, derived.DeclaringType));
		}
	}
}
