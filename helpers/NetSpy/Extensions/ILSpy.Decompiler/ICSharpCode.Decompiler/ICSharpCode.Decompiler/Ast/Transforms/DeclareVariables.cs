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

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using NetSpy.Contracts.Text;
using ICSharpCode.Decompiler.ILAst;
using ICSharpCode.NRefactory.CSharp;
using ICSharpCode.NRefactory.CSharp.Analysis;

namespace ICSharpCode.Decompiler.Ast.Transforms {
	/// <summary>
	/// Moves variable declarations to improved positions.
	/// </summary>
	public sealed class DeclareVariables : IAstTransformPoolObject
	{
		sealed class VariableToDeclare
		{
			public AstType Type;
			public Modifiers Modifiers;
			public string Name;
			public ILVariable ILVariable;

			public AssignmentExpression ReplacedAssignment;
			public Statement InsertionPoint;
		}

		DecompilerContext context;
		CancellationToken cancellationToken;
		readonly List<VariableToDeclare> variablesToDeclare = new List<VariableToDeclare>();

		// Un local declarat fara initializator este singura sursa a lui CS0165 in exportul nostru: decompilatorul
		// reda fluxul de control asa cum e in IL, unde nu exista dovada de atribuire sigura pe care C# o cere.
		// Corpurile emise de Cpp2IL au fanionul `init` pus fara exceptie - IlGenerator.cs construieste fiecare
		// corp cu `new CilMethodBody { InitializeLocals = true }`, nu conditionat - iar fanionul ala inseamna ca
		// runtime-ul zeroeste toate localele inainte de intrarea in metoda. Deci `= default(T)` la declaratie nu
		// adauga o valoare pe care programul original nu o avea, o scrie pe cea pe care o avea deja: face
		// explicit exact ce facea `.locals init`.
		// Nu presupunem asta, o citim: BodyInitialisesLocals ia fanionul de pe corpul care detine chiar
		// declaratia, si daca nu e pus lasam CS0165 in picioare, fiindca acolo initializarea chiar ar ascunde o
		// diferenta de comportament.
		// Exista deja o reparatie pentru aceeasi problema la nivel de IL, CPP2IL_INIT_LOCALS, care emite
		// `initobj` per local; costa ~22% iesire in plus si de aceea e oprita implicit. Asta face acelasi lucru
		// in C#, fara sa atinga IL-ul. Cele doua nu se dubleaza: cand IL-ul contine `initobj`, decompilatorul
		// scoate o atribuire `obj = default(object);` pe care TryConvertAssignmentExpressionIntoVariableDeclaration
		// o promoveaza la declaratie, deci intrarea ajunge pe ramura ReplacedAssignment si nu trece pe aici.
		// CPP2IL_DECL_DEFAULT=0 opreste initializarea, pentru masurarea A/B.
		static readonly bool ZeroInitialiseDeclarations =
			System.Environment.GetEnvironmentVariable("CPP2IL_DECL_DEFAULT") != "0";

		public DeclareVariables(DecompilerContext context)
		{
			Reset(context);
		}

		public void Reset(DecompilerContext context)
		{
			this.context = context;
			this.cancellationToken = context.CancellationToken;
			this.variablesToDeclare.Clear();
		}

		public void Run(AstNode node)
		{
			Run(node, null);
			// Declare all the variables at the end, after all the logic has run.
			// This is done so that definite assignment analysis can work on a single representation and doesn't have to be updated
			// when we change the AST.
			for (int i = 0; i < variablesToDeclare.Count; i++) {
				var v = variablesToDeclare[i];
				if (v.ReplacedAssignment == null) {
					BlockStatement block = (BlockStatement)v.InsertionPoint.Parent;
					var decl = new VariableDeclarationStatement(
						v.ILVariable != null && v.ILVariable.IsParameter
							? BoxedTextColor.Parameter
							: BoxedTextColor.Local, (AstType)v.Type.Clone(), v.Name,
						BuildZeroInitialiser(v));
					if (v.ILVariable != null)
						decl.Variables.Single().AddAnnotation(v.ILVariable);
					block.Statements.InsertBefore(
						v.InsertionPoint,
						decl);
				}
			}

			// First do all the insertions, then do all the replacements. This is necessary because a replacement might remove our reference point from the AST.
			for (int i = 0; i < variablesToDeclare.Count; i++) {
				var v = variablesToDeclare[i];
				if (v.ReplacedAssignment != null) {
					var method = v.ReplacedAssignment.Right.Annotation<IMethod>().ResolveMethodDef();
					bool isRef = method != null && method.ReturnType.RemovePinnedAndModifiers().GetElementType() ==
						ElementType.ByRef;
					bool isReadOnly = isRef &&
									  DnlibExtensions.HasIsReadOnlyAttribute(method.Parameters.ReturnParameter
										  .ParamDef);

					// We clone the right expression so that it doesn't get removed from the old ExpressionStatement,
					// which might be still in use by the definite assignment graph.
					VariableInitializer initializer =
						new VariableInitializer(
								v.ILVariable != null && v.ILVariable.IsParameter
									? BoxedTextColor.Parameter
									: BoxedTextColor.Local, v.Name, v.ReplacedAssignment.Right.Detach())
							.CopyAnnotationsFrom(v.ReplacedAssignment).WithAnnotation(v.ILVariable);
					VariableDeclarationStatement varDecl = new VariableDeclarationStatement {
						Type = (AstType)v.Type.Clone(), Variables = { initializer }, Modifiers = v.Modifiers,
					};
					if (isRef)
						initializer.Modifiers |= Modifiers.Ref;
					if (isReadOnly)
						varDecl.Modifiers |= Modifiers.Readonly;
					ExpressionStatement es = v.ReplacedAssignment.Parent as ExpressionStatement;
					if (es != null) {
						// Note: if this crashes with 'Cannot replace the root node', check whether two variables were assigned the same name
						es.ReplaceWith(varDecl.CopyAnnotationsFrom(es));
						varDecl.AddAnnotation(es.GetAllRecursiveILSpans());
					}
					else {
						varDecl.AddAnnotation(v.ReplacedAssignment.GetAllRecursiveILSpans());
						v.ReplacedAssignment.ReplaceWith(varDecl);
					}
				}
			}

			variablesToDeclare.Clear();
		}

		/// <summary>
		/// Da initializatorul `default(T)` pentru o declaratie care altfel ar ramane goala, sau null cand nu
		/// avem voie sa o initializam. Se cheama numai pe ramura fara ReplacedAssignment, deci nu poate
		/// suprascrie o initializare existenta.
		/// </summary>
		Expression BuildZeroInitialiser(VariableToDeclare v)
		{
			if (!ZeroInitialiseDeclarations)
				return null;

			// Un parametru e deja atribuit de apelant: `= default(T)` i-ar sterge valoarea primita, iar pe un
			// `out` ar rupe si contractul metodei.
			if (v.ILVariable != null && v.ILVariable.IsParameter)
				return null;

			AstType type = v.Type;
			if (type == null || type.IsNull)
				return null;

			// Un byref nu are forma asta: `ref byte b = default(ref byte);` e invalid de doua ori. Exact tiparul
			// acesta, emis la nivel de IL, e cel care a stricat 1.907 linii cand CPP2IL_INIT_LOCALS punea
			// `initobj byte&` (vezi comentariul din IlGenerator.cs). Un byref nici nu are ce zero sa primeasca.
			if ((v.Modifiers & Modifiers.Ref) != 0)
				return null;
			if (type is ComposedType composed && composed.HasRefSpecifier)
				return null;

			// `var` ajunge aici cand tipul localului contine un tip anonim: AstMethodBodyBuilder pune atunci
			// SimpleType("var") in locul conversiei de tip. `default(var)` nu exista.
			if (type is SimpleType simple && simple.Identifier == "var" && simple.TypeArguments.Count == 0)
				return null;

			if (!BodyInitialisesLocals(v.InsertionPoint))
				return null;

			// Clona e obligatorie: un nod NRefactory are un singur parinte, iar `type` e deja legat de
			// declaratie prin cealalta clona de mai sus.
			return new DefaultValueExpression((AstType)type.Clone());
		}

		/// <summary>
		/// Spune daca metoda care contine nodul dat are `.locals init`, adica daca runtime-ul chiar zeroeste
		/// localele inainte de intrarea in corp.
		/// </summary>
		static bool BodyInitialisesLocals(AstNode node)
		{
			// Prima adnotare MethodDef urcand in arbore este chiar metoda care detine blocul: AstBuilder pune
			// MethodDef pe MethodDeclaration, ConstructorDeclaration si Accessor, iar DelegateConstruction il
			// pune pe AnonymousMethodExpression - deci un lambda inlinuit e judecat dupa fanionul lui, nu dupa
			// al metodei care il inconjoara.
			for (AstNode current = node; current != null; current = current.Parent) {
				MethodDef owner = current.Annotation<MethodDef>();
				if (owner != null)
					return owner.Body == null || owner.Body.InitLocals;
			}

			// Fara proprietar nu putem sti, si atunci nu initializam: un CS0165 ramas este o eroare onesta, o
			// initializare pusa pe nestiute ar fi o diferenta de comportament tacuta.
			return false;
		}

		void Run(AstNode node, DefiniteAssignmentAnalysis daa)
		{
			BlockStatement block = node as BlockStatement;
			if (block != null) {
				var variables = block.Statements.TakeWhile(stmt => stmt is VariableDeclarationStatement)
					.Cast<VariableDeclarationStatement>().ToList();
				if (variables.Count > 0) {
					// remove old variable declarations:
					for (int i = 0; i < variables.Count; i++) {
						var varDecl = variables[i];
						Debug.Assert(varDecl.Variables.Single().Initializer.IsNull);
						varDecl.Remove();
					}

					if (daa == null) {
						// If possible, reuse the DefiniteAssignmentAnalysis that was created for the parent block
						daa = new DefiniteAssignmentAnalysis(block, cancellationToken);
					}

					for (int i = 0; i < variables.Count; i++) {
						var varDecl = variables[i];
						VariableInitializer initializer = varDecl.Variables.Single();
						string variableName = initializer.Name;
						ILVariable v = initializer.Annotation<ILVariable>();
						bool allowPassIntoLoops =
							initializer.Annotation<DelegateConstruction.CapturedVariableAnnotation>() == null;
						DeclareVariableInBlock(daa, block, varDecl.Type, variableName, v, varDecl.Modifiers,
							allowPassIntoLoops);
					}
				}
			}
			for (AstNode child = node.FirstChild; child != null; child = child.NextSibling) {
				Run(child, daa);
			}
		}

		void DeclareVariableInBlock(DefiniteAssignmentAnalysis daa, BlockStatement block, AstType type, string variableName, ILVariable v, Modifiers modifiers, bool allowPassIntoLoops)
		{
			// declarationPoint: The point where the variable would be declared, if we decide to declare it in this block
			Statement declarationPoint = null;
			// Check whether we can move down the variable into the sub-blocks
			bool canMoveVariableIntoSubBlocks = FindDeclarationPoint(daa, variableName, allowPassIntoLoops, block, out declarationPoint, cancellationToken);
			if (declarationPoint == null) {
				// The variable isn't used at all
				return;
			}
			if (canMoveVariableIntoSubBlocks) {
				// Declare the variable within the sub-blocks
				foreach (Statement stmt in block.Statements) {
					ForStatement forStmt = stmt as ForStatement;
					if (forStmt != null && forStmt.Initializers.Count == 1) {
						// handle the special case of moving a variable into the for initializer
						if (TryConvertAssignmentExpressionIntoVariableDeclaration(forStmt.Initializers.Single(), type, variableName, modifiers))
							continue;
					}
					UsingStatement usingStmt = stmt as UsingStatement;
					if (usingStmt != null && usingStmt.ResourceAcquisition is AssignmentExpression) {
						// handle the special case of moving a variable into a using statement
						if (TryConvertAssignmentExpressionIntoVariableDeclaration((Expression)usingStmt.ResourceAcquisition, type, variableName, modifiers))
							continue;
					}
					IfElseStatement ies = stmt as IfElseStatement;
					if (ies != null) {
						foreach (var child in IfElseChainChildren(ies)) {
							BlockStatement subBlock = child as BlockStatement;
							if (subBlock != null)
								DeclareVariableInBlock(daa, subBlock, type, variableName, v, modifiers, allowPassIntoLoops);
						}
						continue;
					}
					foreach (AstNode child in stmt.Children) {
						BlockStatement subBlock = child as BlockStatement;
						if (subBlock != null) {
							DeclareVariableInBlock(daa, subBlock, type, variableName, v, modifiers, allowPassIntoLoops);
						} else if (HasNestedBlocks(child)) {
							foreach (BlockStatement nestedSubBlock in child.Children.OfType<BlockStatement>()) {
								DeclareVariableInBlock(daa, nestedSubBlock, type, variableName, v, modifiers, allowPassIntoLoops);
							}
						}
					}
				}
			} else {
				// Try converting an assignment expression into a VariableDeclarationStatement
				if (!TryConvertAssignmentExpressionIntoVariableDeclaration(declarationPoint, type, variableName, modifiers)) {
					// Declare the variable in front of declarationPoint
					variablesToDeclare.Add(new VariableToDeclare { Type = type, Name = variableName, ILVariable = v, InsertionPoint = declarationPoint, Modifiers = modifiers });
				}
			}
		}

		bool TryConvertAssignmentExpressionIntoVariableDeclaration(Statement declarationPoint, AstType type, string variableName, Modifiers modifiers)
		{
			// convert the declarationPoint into a VariableDeclarationStatement
			ExpressionStatement es = declarationPoint as ExpressionStatement;
			if (es != null) {
				return TryConvertAssignmentExpressionIntoVariableDeclaration(es.Expression, type, variableName, modifiers);
			}
			return false;
		}

		bool TryConvertAssignmentExpressionIntoVariableDeclaration(Expression expression, AstType type, string variableName, Modifiers modifiers)
		{
			AssignmentExpression ae = expression as AssignmentExpression;
			if (ae != null && ae.Operator == AssignmentOperatorType.Assign) {
				IdentifierExpression ident = ae.Left as IdentifierExpression;
				if (ident != null && ident.Identifier == variableName) {
					variablesToDeclare.Add(new VariableToDeclare { Type = type, Name = variableName, ILVariable = ident.Annotation<ILVariable>(), ReplacedAssignment = ae, Modifiers = modifiers });
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Finds the declaration point for the variable within the specified block.
		/// </summary>
		/// <param name="daa">
		/// Definite assignment analysis, must be prepared for 'block' or one of its parents.
		/// </param>
		/// <param name="varDecl">The variable to declare</param>
		/// <param name="block">The block in which the variable should be declared</param>
		/// <param name="declarationPoint">
		/// Output parameter: the first statement within 'block' where the variable needs to be declared.
		/// </param>
		/// <returns>
		/// Returns whether it is possible to move the variable declaration into sub-blocks.
		/// </returns>
		public static bool FindDeclarationPoint(DefiniteAssignmentAnalysis daa, VariableDeclarationStatement varDecl, BlockStatement block, out Statement declarationPoint, CancellationToken cancellationToken)
		{
			string variableName = varDecl.Variables.Single().Name;
			bool allowPassIntoLoops = varDecl.Variables.Single().Annotation<DelegateConstruction.CapturedVariableAnnotation>() == null;
			return FindDeclarationPoint(daa, variableName, allowPassIntoLoops, block, out declarationPoint, cancellationToken);
		}

		static bool FindDeclarationPoint(DefiniteAssignmentAnalysis daa, string variableName, bool allowPassIntoLoops, BlockStatement block, out Statement declarationPoint, CancellationToken cancellationToken)
		{
			// declarationPoint: The point where the variable would be declared, if we decide to declare it in this block
			declarationPoint = null;
			foreach (Statement stmt in block.Statements) {
				if (UsesVariable(stmt, variableName)) {
					if (declarationPoint == null)
						declarationPoint = stmt;
					if (!CanMoveVariableUseIntoSubBlock(stmt, variableName, allowPassIntoLoops)) {
						// If it's not possible to move the variable use into a nested block,
						// we need to declare the variable in this block
						return false;
					}
					// If we can move the variable into the sub-block, we need to ensure that the remaining code
					// does not use the value that was assigned by the first sub-block
					Statement nextStatement = stmt.GetNextStatement();
					if (nextStatement != null) {
						// Analyze the range from the next statement to the end of the block
						daa.SetAnalyzedRange(nextStatement, block);
						daa.Analyze(variableName, cancellationToken);
						if (daa.UnassignedVariableUses.Count > 0) {
							return false;
						}
					}
				}
			}
			return true;
		}

		static bool CanMoveVariableUseIntoSubBlock(Statement stmt, string variableName, bool allowPassIntoLoops)
		{
			if (!allowPassIntoLoops && (stmt is ForStatement || stmt is ForeachStatement || stmt is DoWhileStatement || stmt is WhileStatement))
				return false;

			ForStatement forStatement = stmt as ForStatement;
			if (forStatement != null && forStatement.Initializers.Count == 1) {
				// for-statement is special case: we can move variable declarations into the initializer
				ExpressionStatement es = forStatement.Initializers.Single() as ExpressionStatement;
				if (es != null) {
					AssignmentExpression ae = es.Expression as AssignmentExpression;
					if (ae != null && ae.Operator == AssignmentOperatorType.Assign) {
						IdentifierExpression ident = ae.Left as IdentifierExpression;
						if (ident != null && ident.Identifier == variableName) {
							return !UsesVariable(ae.Right, variableName);
						}
					}
				}
			}

			UsingStatement usingStatement = stmt as UsingStatement;
			if (usingStatement != null) {
				// using-statement is special case: we can move variable declarations into the initializer
				AssignmentExpression ae = usingStatement.ResourceAcquisition as AssignmentExpression;
				if (ae != null && ae.Operator == AssignmentOperatorType.Assign) {
					IdentifierExpression ident = ae.Left as IdentifierExpression;
					if (ident != null && ident.Identifier == variableName) {
						return !UsesVariable(ae.Right, variableName);
					}
				}
			}

			IfElseStatement ies = stmt as IfElseStatement;
			if (ies != null) {
				foreach (var child in IfElseChainChildren(ies)) {
					if (!(child is BlockStatement) && UsesVariable(child, variableName))
						return false;
				}
				return true;
			}

			// We can move the variable into a sub-block only if the variable is used in only that sub-block (and not in expressions such as the loop condition)
			for (AstNode child = stmt.FirstChild; child != null; child = child.NextSibling) {
				if (!(child is BlockStatement) && UsesVariable(child, variableName)) {
					if (HasNestedBlocks(child)) {
						// catch clauses/switch sections can contain nested blocks
						for (AstNode grandchild = child.FirstChild; grandchild != null; grandchild = grandchild.NextSibling) {
							if (!(grandchild is BlockStatement) && UsesVariable(grandchild, variableName))
								return false;
						}
					} else {
						return false;
					}
				}
			}
			return true;
		}

		static IEnumerable<AstNode> IfElseChainChildren(IfElseStatement ies)
		{
			IfElseStatement prev;
			do {
				yield return ies.Condition;
				yield return ies.TrueStatement;
				prev = ies;
				ies = ies.FalseStatement as IfElseStatement;
			} while (ies != null);
			if (!prev.FalseStatement.IsNull)
				yield return prev.FalseStatement;
		}

		static bool HasNestedBlocks(AstNode node)
		{
			return node is CatchClause || node is SwitchSection;
		}

		static bool UsesVariable(AstNode node, string variableName)
		{
			IdentifierExpression ie = node as IdentifierExpression;
			if (ie != null && ie.Identifier == variableName)
				return true;

			FixedStatement fixedStatement = node as FixedStatement;
			if (fixedStatement != null) {
				foreach (VariableInitializer v in fixedStatement.Variables) {
					if (v.Name == variableName)
						return false; // no need to introduce the variable here
				}
			}

			ForeachStatement foreachStatement = node as ForeachStatement;
			if (foreachStatement != null) {
				if (foreachStatement.VariableName == variableName)
					return false; // no need to introduce the variable here
			}

			UsingStatement usingStatement = node as UsingStatement;
			if (usingStatement != null) {
				VariableDeclarationStatement varDecl = usingStatement.ResourceAcquisition as VariableDeclarationStatement;
				if (varDecl != null) {
					foreach (VariableInitializer v in varDecl.Variables) {
						if (v.Name == variableName)
							return false; // no need to introduce the variable here
					}
				}
			}

			CatchClause catchClause = node as CatchClause;
			if (catchClause != null && catchClause.VariableName == variableName) {
				return false; // no need to introduce the variable here
			}

			for (AstNode child = node.FirstChild; child != null; child = child.NextSibling) {
				if (UsesVariable(child, variableName))
					return true;
			}
			return false;
		}
	}
}
