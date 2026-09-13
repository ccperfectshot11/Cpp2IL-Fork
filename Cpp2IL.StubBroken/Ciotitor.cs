using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Cpp2IL.StubBroken
{
	internal enum Clasificare
	{
		InCorp,          // eroarea e in corpul unui membru -> se poate ciotit
		InSemnatura,     // eroarea e in semnatura membrului (tip de retur, parametri, : base(...)) -> NU se rezolva prin ciot
		LaNivelDeTip,    // camp, using, lista de baza, enum... -> NU exista corp de inlocuit
		FaraCorp,        // membru abstract/extern/auto-property -> nu are corp
		PozitieInvalida, // linia/coloana nu exista in fisier (fisier reexportat intre timp)
	}

	internal sealed class RezultatEroare
	{
		public Eroare Eroare;
		public Clasificare Clasificare;
		public string FelNod;
		public string NumeMembru;
	}

	internal sealed class TintaCiot
	{
		public SyntaxNode Membru;
		public TextSpan SpanDeInlocuit;
		public bool EraExpresie;
		public string Fel;
		public string Nume;
		public int LinieSemnatura;
		public SortedSet<string> Coduri = new SortedSet<string>(StringComparer.Ordinal);
	}

	internal static class Ciotitor
	{
		// Marcajul pus in fiecare corp inlocuit. Se numara cu:
		//   grep -r "// CIOT:" <proiect> | wc -l
		public const string Marcaj = "// CIOT:";

		// Coduri raportate pe semnatura, dar cauzate de corp: inlocuirea corpului chiar le rezolva.
		// CS0161 = nu toate caile returneaza o valoare
		// CS0177 = parametrul out nu este atribuit pe toate caile
		// CS0171 = campul unui struct nu e atribuit complet in constructor
		private static readonly HashSet<string> CoduriDeCorpDarPeSemnatura = new HashSet<string>(StringComparer.Ordinal)
		{
			"CS0161", "CS0177", "CS0171",
		};

		public static readonly CSharpParseOptions OptiuniParsare =
			new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.None);

		// ---------- gasirea membrului care contine eroarea ----------

		private static SyntaxNode GasesteMembru(SyntaxNode nod)
		{
			foreach (var a in nod.AncestorsAndSelf())
			{
				// Accesorii se iau individual (get separat de set), cum s-a cerut.
				if (a is AccessorDeclarationSyntax)
					return a;

				// metoda / constructor / destructor / operator / operator de conversie
				if (a is BaseMethodDeclarationSyntax)
					return a;

				// proprietate sau indexator scris cu => expresie (fara accesori)
				if (a is PropertyDeclarationSyntax p && p.ExpressionBody != null)
					return a;
				if (a is IndexerDeclarationSyntax ix && ix.ExpressionBody != null)
					return a;

				// Am urcat pana la tip sau pana la radacina fara sa gasim un membru cu corp.
				// Lambda-urile si functiile locale NU opresc urcarea: vrem metoda care le contine.
				if (a is BaseTypeDeclarationSyntax || a is CompilationUnitSyntax || a is BaseNamespaceDeclarationSyntax)
					return null;
			}
			return null;
		}

		private static SyntaxNode CorpulMembrului(SyntaxNode membru, out SyntaxToken punctSiVirgula, out bool eraExpresie)
		{
			punctSiVirgula = default;
			eraExpresie = false;

			switch (membru)
			{
				case BaseMethodDeclarationSyntax m:
					if (m.Body != null) return m.Body;
					if (m.ExpressionBody != null) { eraExpresie = true; punctSiVirgula = m.SemicolonToken; return m.ExpressionBody; }
					return null;

				case AccessorDeclarationSyntax acc:
					if (acc.Body != null) return acc.Body;
					if (acc.ExpressionBody != null) { eraExpresie = true; punctSiVirgula = acc.SemicolonToken; return acc.ExpressionBody; }
					return null;

				case PropertyDeclarationSyntax p when p.ExpressionBody != null:
					eraExpresie = true; punctSiVirgula = p.SemicolonToken; return p.ExpressionBody;

				case IndexerDeclarationSyntax ix when ix.ExpressionBody != null:
					eraExpresie = true; punctSiVirgula = ix.SemicolonToken; return ix.ExpressionBody;
			}
			return null;
		}

		public static string FelMembru(SyntaxNode membru)
		{
			switch (membru)
			{
				case MethodDeclarationSyntax _: return "metoda";
				case ConstructorDeclarationSyntax _: return "constructor";
				case DestructorDeclarationSyntax _: return "destructor";
				case OperatorDeclarationSyntax _: return "operator";
				case ConversionOperatorDeclarationSyntax _: return "operator-conversie";
				case AccessorDeclarationSyntax acc: return "accesor-" + acc.Keyword.ValueText;
				case PropertyDeclarationSyntax _: return "proprietate-expresie";
				case IndexerDeclarationSyntax _: return "indexator-expresie";
			}
			return membru.Kind().ToString();
		}

		public static string NumeMembru(SyntaxNode membru)
		{
			switch (membru)
			{
				case MethodDeclarationSyntax m: return m.Identifier.ValueText;
				case ConstructorDeclarationSyntax c: return c.Identifier.ValueText + "..ctor";
				case DestructorDeclarationSyntax d: return "~" + d.Identifier.ValueText;
				case OperatorDeclarationSyntax o: return "operator " + o.OperatorToken.ValueText;
				case ConversionOperatorDeclarationSyntax _: return "operator-conversie";
				case AccessorDeclarationSyntax acc:
					{
						var parinte = acc.Parent?.Parent;
						var numeParinte = parinte is PropertyDeclarationSyntax pp ? pp.Identifier.ValueText
							: parinte is IndexerDeclarationSyntax ? "this[]"
							: parinte is EventDeclarationSyntax ev ? ev.Identifier.ValueText
							: "?";
						return numeParinte + "." + acc.Keyword.ValueText;
					}
				case PropertyDeclarationSyntax p: return p.Identifier.ValueText;
				case IndexerDeclarationSyntax _: return "this[]";
			}
			return "?";
		}

		// Ce fel de constructie contine eroarea, cand nu e vorba de un membru cu corp.
		public static string FelNodDeTip(SyntaxNode nod)
		{
			foreach (var a in nod.AncestorsAndSelf())
			{
				switch (a)
				{
					case UsingDirectiveSyntax _: return "using";
					case FieldDeclarationSyntax _: return "camp";
					case EventFieldDeclarationSyntax _: return "eveniment-camp";
					case PropertyDeclarationSyntax _: return "proprietate-auto";
					case IndexerDeclarationSyntax _: return "indexator";
					case EnumMemberDeclarationSyntax _: return "membru-enum";
					case DelegateDeclarationSyntax _: return "delegat";
					case BaseListSyntax _: return "lista-de-baza";
					case AttributeListSyntax _: return "atribut";
					case TypeParameterConstraintClauseSyntax _: return "constrangere-generica";
					case ParameterSyntax _: return "parametru";
					case BaseTypeDeclarationSyntax _: return "declaratie-tip";
				}
			}
			return nod.Kind().ToString();
		}

		// ---------- clasificarea unei erori ----------

		public static RezultatEroare Clasifica(SyntaxNode radacina, SourceText text, Eroare er, out TintaCiot tinta)
		{
			tinta = null;
			var rez = new RezultatEroare { Eroare = er };

			if (er.Linie < 1 || er.Linie > text.Lines.Count || text.Length == 0)
			{
				rez.Clasificare = Clasificare.PozitieInvalida;
				rez.FelNod = "linie-inexistenta";
				return rez;
			}

			var linie = text.Lines[er.Linie - 1];
			var pozitie = Math.Min(linie.Start + Math.Max(er.Coloana - 1, 0), linie.End);
			pozitie = Math.Min(pozitie, text.Length - 1);

			var token = radacina.FindToken(pozitie);
			var nod = (SyntaxNode)(token.Parent ?? radacina);

			var membru = GasesteMembru(nod);
			if (membru == null)
			{
				rez.Clasificare = Clasificare.LaNivelDeTip;
				rez.FelNod = FelNodDeTip(nod);
				return rez;
			}

			rez.NumeMembru = NumeMembru(membru);
			rez.FelNod = FelMembru(membru);

			var corp = CorpulMembrului(membru, out var punctSiVirgula, out var eraExpresie);
			if (corp == null)
			{
				rez.Clasificare = Clasificare.FaraCorp;
				return rez;
			}

			var spanCorp = corp.Span;
			var inCorp = pozitie >= spanCorp.Start && pozitie <= spanCorp.End;

			// CS0161/CS0177/CS0171 sunt raportate pe numele membrului, dar cauza e in corp.
			if (!inCorp && CoduriDeCorpDarPeSemnatura.Contains(er.Cod))
				inCorp = true;

			if (!inCorp)
			{
				rez.Clasificare = Clasificare.InSemnatura;
				return rez;
			}

			rez.Clasificare = Clasificare.InCorp;

			var spanInlocuit = eraExpresie
				? TextSpan.FromBounds(corp.SpanStart, punctSiVirgula.Span.End)
				: spanCorp;

			tinta = new TintaCiot
			{
				Membru = membru,
				SpanDeInlocuit = spanInlocuit,
				EraExpresie = eraExpresie,
				Fel = rez.FelNod,
				Nume = rez.NumeMembru,
				LinieSemnatura = text.Lines.GetLinePosition(membru.SpanStart).Line + 1,
			};
			return rez;
		}

		// ---------- construirea ciotului ----------

		private static TypeSyntax TipDeIntors(SyntaxNode membru)
		{
			switch (membru)
			{
				case MethodDeclarationSyntax m: return m.ReturnType;
				case OperatorDeclarationSyntax o: return o.ReturnType;
				case ConversionOperatorDeclarationSyntax c: return c.Type;
				case PropertyDeclarationSyntax p: return p.Type;
				case IndexerDeclarationSyntax ix: return ix.Type;

				case AccessorDeclarationSyntax acc:
					if (!acc.Keyword.IsKind(SyntaxKind.GetKeyword))
						return null; // set / init / add / remove se comporta ca void
					switch (acc.Parent?.Parent)
					{
						case PropertyDeclarationSyntax pp: return pp.Type;
						case IndexerDeclarationSyntax ii: return ii.Type;
					}
					return null;
			}
			return null; // constructor, destructor
		}

		private static bool EsteVoid(TypeSyntax t)
			=> t is PredefinedTypeSyntax pt && pt.Keyword.IsKind(SyntaxKind.VoidKeyword);

		// Numele tipului care contine membrul, cu parametrii generici: Foo<T, U>.
		private static string NumeTipContinator(SyntaxNode membru)
		{
			var tip = membru.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
			if (tip == null) return null;

			var nume = tip.Identifier.ValueText;
			if (tip.TypeParameterList != null && tip.TypeParameterList.Parameters.Count > 0)
				nume += "<" + string.Join(", ", tip.TypeParameterList.Parameters.Select(p => p.Identifier.ValueText)) + ">";
			return nume;
		}

		private static bool EsteInStruct(SyntaxNode membru)
			=> membru.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is StructDeclarationSyntax;

		public static string ConstruiesteCiot(TintaCiot tinta, string indentBaza, string unitateIndent, string nl)
		{
			var membru = tinta.Membru;
			var indentIntern = indentBaza + unitateIndent;
			var linii = new List<string>();

			linii.Add(Marcaj + " logica de rescris (" + string.Join(", ", tinta.Coduri) + ")");

			// Parametrii out trebuie atribuiti, altfel apare CS0177 in locul erorii vechi.
			if (membru is BaseMethodDeclarationSyntax bm && bm.ParameterList != null)
			{
				foreach (var p in bm.ParameterList.Parameters)
				{
					if (!p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.OutKeyword)))
						continue;
					if (p.Type == null)
						continue;
					linii.Add(p.Identifier.ValueText + " = default(" + p.Type.ToString() + ");");
				}
			}

			if (membru is ConstructorDeclarationSyntax && EsteInStruct(membru))
			{
				// Intr-un struct, constructorul trebuie sa atribuie toate campurile (CS0171).
				// "this = default(T);" le atribuie pe toate dintr-o data.
				var numeTip = NumeTipContinator(membru);
				if (numeTip != null)
					linii.Add("this = default(" + numeTip + ");");
			}
			else
			{
				var tip = TipDeIntors(membru);
				if (tip is RefTypeSyntax)
				{
					// Un membru care intoarce "ref T" nu poate returna default(T).
					linii.Add("throw new global::System.NotImplementedException();");
				}
				else if (tip != null && !EsteVoid(tip))
				{
					linii.Add("return default(" + tip.ToString() + ");");
				}
			}

			var sb = new System.Text.StringBuilder();

			// O proprietate sau un indexator scris cu "=> expresie" nu poate primi un bloc de
			// instructiuni: corpul unei proprietati trebuie sa contina accesori. Il invelim in "get".
			var areNevoieDeGet = membru is PropertyDeclarationSyntax || membru is IndexerDeclarationSyntax;

			if (areNevoieDeGet)
			{
				var indentGet = indentBaza + unitateIndent;
				var indentCorpGet = indentGet + unitateIndent;

				sb.Append('{');
				sb.Append(nl).Append(indentGet).Append("get");
				sb.Append(nl).Append(indentGet).Append('{');
				foreach (var l in linii)
					sb.Append(nl).Append(indentCorpGet).Append(l);
				sb.Append(nl).Append(indentGet).Append('}');
				sb.Append(nl).Append(indentBaza).Append('}');
				return sb.ToString();
			}

			sb.Append('{');
			foreach (var l in linii)
			{
				sb.Append(nl);
				sb.Append(indentIntern);
				sb.Append(l);
			}
			sb.Append(nl);
			sb.Append(indentBaza);
			sb.Append('}');
			return sb.ToString();
		}
	}
}
