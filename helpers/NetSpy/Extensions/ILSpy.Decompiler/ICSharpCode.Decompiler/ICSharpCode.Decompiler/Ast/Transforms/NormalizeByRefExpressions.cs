using System.Linq;
using ICSharpCode.NRefactory.CSharp;

namespace ICSharpCode.Decompiler.Ast.Transforms {
	// `ldelema` / `ldflda` / `ldloca` produc un DirectionExpression (`ref x`), iar in C# asta nu e o
	// valoare: e o forma care are voie sa apara numai in cateva pozitii anume. Cat timp nodul sta acolo
	// unde l-a pus AstMethodBodyBuilder - argument de apel - totul e in regula. Problema apare cand un
	// transform de mai tarziu MUTA nodul intr-o pozitie in care limbajul cere o valoare.
	//
	// Cazul care a rupt exportul: ReplaceMethodCallsWithOperators reduce `String.Concat(a, b)` la `a + b`
	// si muta argumentele, asa cum sunt, in operanzii lui `+`. Cand unul din argumente era `ref items[i]`
	// a iesit `"Gameplay/" + (ref items[i])`, adica CS1525 - si fiindca e o eroare de PARSARE, Roslyn se
	// opreste inainte de legarea corpurilor si ascunde toate celelalte erori din proiect. Acelasi tipar de
	// mutare exista in transformul acela de sase ori (operatori binari, unari, op_Explicit, op_Implicit,
	// op_True, decrementarea pe decimal) si mai apare si in alte transforme, deci nu se repara la sursa
	// fiecarei mutari, ci o singura data, la sfarsit, cand fiecare nod e deja in pozitia lui finala.
	//
	// Regula e scrisa invers fata de cum ar fi tentant: nu enumeram pozitiile GRESITE, ci pe cele in care
	// `ref` e permis. Lista pozitiilor permise e scurta si se poate verifica pana la capat; lista celor
	// gresite nu ar fi niciodata completa, si fiecare pozitie uitata ar fi ramas o eroare de parsare.
	//
	// Ce NU atinge:
	//  - `ref` pe declaratii de local (`ref float x = ...`) - acolo `ref` face parte din tipul declarat
	//    (ComposedType.HasRefSpecifier), nu e DirectionExpression, deci nici nu ajunge pe aici;
	//  - `x = ref y` si `ref T x = ref y` - se parseaza corect ca atribuire prin referinta. Daca stanga nu
	//    e o variabila `ref`, eroarea e de LEGARE, nu de parsare, si nu blocheaza restul proiectului.
	sealed class NormalizeByRefExpressions : IAstTransformPoolObject
	{
		DecompilerContext context;

		public NormalizeByRefExpressions(DecompilerContext context)
		{
			Reset(context);
		}

		public void Reset(DecompilerContext context)
		{
			this.context = context;
		}

		public void Run(AstNode compilationUnit)
		{
			// Snapshot: dezambalarea modifica arborele chiar in timp ce il parcurgem.
			foreach (var direction in compilationUnit.Descendants.OfType<DirectionExpression>().ToArray()) {
				if (direction.Parent == null)
					continue;
				// GetChildByRole intoarce obiectul Null, nu null: fara verificarea asta un nod schilod ar
				// arunca, si ar opri decompilarea a 34.000 de corpuri ca sa repare unul singur.
				if (direction.Expression == null || direction.Expression.IsNull)
					continue;
				if (IsByRefPosition(direction))
					continue;
				var value = direction.Expression.Detach();
				if (context.CalculateILSpans)
					direction.AddAllRecursiveILSpansTo(value);
				direction.ReplaceWith(value);
			}
		}

		static bool IsByRefPosition(DirectionExpression direction)
		{
			// Parantezele nu schimba pozitia sintactica, deci urcam peste ele.
			AstNode node = direction;
			AstNode parent = node.Parent;
			while (parent is ParenthesizedExpression) {
				node = parent;
				parent = parent.Parent;
			}

			// Nod detasat, sau arbore de tipar (PatternStatementTransform isi tine tiparele in arbori
			// separati, cu DirectionExpression inauntru): nu avem context, deci nu atingem nimic.
			if (parent == null)
				return true;

			// `f(ref x)`, `new T(ref x)`, `x[ref i]`, argument de atribut.
			if (node.Role == Roles.Argument)
				return true;

			// `f(nume: ref x)`
			if (parent is NamedArgumentExpression && node.Role == Roles.Expression)
				return true;

			// `ref T x = ref y;`
			if (parent is VariableInitializer && node.Role == Roles.Expression)
				return true;

			// `x = ref y;`. Numai atribuirea simpla: `x += ref y` nu exista in limbaj.
			// Atentie: AssignmentExpression.RightRole SI BinaryOperatorExpression.RightRole sunt acelasi
			// obiect Role, deci aici rolul singur nu ar distinge intre atribuire si operator binar -
			// tipul parintelui e cel care decide.
			if (parent is AssignmentExpression assignment)
				return assignment.Operator == AssignmentOperatorType.Assign
					&& node.Role == AssignmentExpression.RightRole;

			// `return ref x;`
			if (parent is ReturnStatement && node.Role == Roles.Expression)
				return true;

			// `c ? ref a : ref b` e valid, dar numai cu `ref` pe AMANDOUA ramurile; conditia insasi cere
			// o valoare.
			if (parent is ConditionalExpression conditional)
				return (node.Role == ConditionalExpression.TrueRole || node.Role == ConditionalExpression.FalseRole)
					&& StripParentheses(conditional.TrueExpression) is DirectionExpression
					&& StripParentheses(conditional.FalseExpression) is DirectionExpression;

			// `__refvalue(ref x, T)`, `__makeref(ref x)` - forme nedocumentate, cu reguli proprii.
			if (parent is UndocumentedExpression)
				return true;

			// `ref ref x` nu se scrie in C#, dar lantul se rezolva de la exterior spre interior: cand cel
			// din afara e dezambalat, cel dinauntru ajunge in pozitia lui si e judecat atunci.
			if (parent is DirectionExpression)
				return true;

			return false;
		}

		static Expression StripParentheses(Expression expression)
		{
			while (expression is ParenthesizedExpression parenthesized)
				expression = parenthesized.Expression;
			return expression;
		}
	}
}
