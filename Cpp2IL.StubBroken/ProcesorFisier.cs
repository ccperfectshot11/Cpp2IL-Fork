using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Cpp2IL.StubBroken
{
	internal sealed class RezultatFisier
	{
		public string Cale;
		public bool Lipseste;
		public bool Refuzat;              // parsarea dupa modificare a iesit mai rau -> nu scriem nimic
		public string MotivRefuz;
		public bool Modificat;
		public bool ListaInvechita;       // coordonatele erorilor nu mai corespund fisierului de pe disc
		public int SemnaleInvechire;
		public int CiotiriNoi;
		public int DejaCiotite;
		public List<RezultatEroare> Erori = new List<RezultatEroare>();
		public List<TintaCiot> Tinte = new List<TintaCiot>();
		public string TextNou;
		public bool AreBom;
	}

	internal static class ProcesorFisier
	{
		// Analizeaza un fisier si pregateste textul nou. NU scrie nimic pe disc:
		// scrierea o decide Program, dupa ce verifica global daca lista de erori e proaspata.
		public static RezultatFisier Analizeaza(string cale, List<Eroare> erori)
		{
			var rez = new RezultatFisier { Cale = cale };

			if (!File.Exists(cale))
			{
				rez.Lipseste = true;
				return rez;
			}

			var octeti = File.ReadAllBytes(cale);
			var areBom = octeti.Length >= 3 && octeti[0] == 0xEF && octeti[1] == 0xBB && octeti[2] == 0xBF;
			rez.AreBom = areBom;
			var textOriginal = new UTF8Encoding(false).GetString(octeti, areBom ? 3 : 0, octeti.Length - (areBom ? 3 : 0));

			var nl = DetecteazaNewline(textOriginal);
			var unitateIndent = DetecteazaUnitateIndent(textOriginal);

			var sursa = SourceText.From(textOriginal);
			var arbore = CSharpSyntaxTree.ParseText(sursa, Ciotitor.OptiuniParsare, cale);
			var radacina = arbore.GetRoot();
			var eroriSintaxaInainte = arbore.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);

			// Grupam erorile pe membru: o metoda cu cinci erori se ciotoieste o singura data.
			var tinte = new Dictionary<int, TintaCiot>();

			foreach (var er in erori)
			{
				var r = Ciotitor.Clasifica(radacina, sursa, er, out var tinta);
				rez.Erori.Add(r);

				// Semnal de invechire: eroarea arata spre o linie care nici nu mai exista.
				if (r.Clasificare == Clasificare.PozitieInvalida)
					rez.SemnaleInvechire++;

				if (tinta == null)
					continue;

				if (!tinte.TryGetValue(tinta.SpanDeInlocuit.Start, out var existent))
				{
					tinte[tinta.SpanDeInlocuit.Start] = tinta;
					existent = tinta;
				}
				existent.Coduri.Add(er.Cod);
			}

			// Aplicam de la coada spre cap, ca sa nu se mute pozitiile celor dinainte.
			var listaTinte = tinte.Values.OrderByDescending(t => t.SpanDeInlocuit.Start).ToList();
			var text = textOriginal;
			var ultimulStart = int.MaxValue;

			foreach (var t in listaTinte)
			{
				var span = t.SpanDeInlocuit;

				// Plasa de siguranta: tintele nu ar trebui sa se suprapuna niciodata.
				if (span.End > ultimulStart)
					continue;

				var vechi = textOriginal.Substring(span.Start, span.Length);
				if (vechi.Contains(Ciotitor.Marcaj))
				{
					// O eroare arata spre un corp deja ciotit. Un ciot compileaza, deci lista
					// de erori e dinainte de ciotire: inca un semnal de invechire.
					rez.DejaCiotite++;
					rez.SemnaleInvechire++;
					ultimulStart = span.Start;
					continue;
				}

				var indentBaza = IndentLinie(textOriginal, span.Start);
				var ciot = Ciotitor.ConstruiesteCiot(t, indentBaza, unitateIndent, nl);

				text = text.Substring(0, span.Start) + ciot + text.Substring(span.End);
				rez.Tinte.Add(t);
				rez.CiotiriNoi++;
				ultimulStart = span.Start;
			}

			// Daca lista nu mai corespunde fisierului, nu ne atingem DELOC de el:
			// pozitiile deplasate ar ciotit metode perfect sanatoase.
			if (rez.SemnaleInvechire > 0)
			{
				rez.ListaInvechita = true;
				rez.CiotiriNoi = 0;
				rez.Tinte.Clear();
				rez.TextNou = null;
				return rez;
			}

			if (rez.CiotiriNoi == 0)
				return rez;

			// Verificare obligatorie: textul nou trebuie sa se parseze cel putin la fel de bine ca cel vechi.
			var arboreNou = CSharpSyntaxTree.ParseText(SourceText.From(text), Ciotitor.OptiuniParsare, cale);
			var diagNoi = arboreNou.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
			var eroriSintaxaDupa = diagNoi.Count;

			if (eroriSintaxaDupa > eroriSintaxaInainte)
			{
				rez.Refuzat = true;
				var primele = string.Join(" | ", diagNoi.Take(3).Select(d =>
					d.Id + "@" + (d.Location.GetLineSpan().StartLinePosition.Line + 1) + " " + d.GetMessage()));
				rez.MotivRefuz = "erori de sintaxa dupa modificare: " + eroriSintaxaInainte + " -> " + eroriSintaxaDupa
					+ "  [" + primele + "]";
				rez.CiotiriNoi = 0;
				rez.Tinte.Clear();
				return rez;
			}

			rez.TextNou = text;
			return rez;
		}

		public static void Scrie(RezultatFisier rez, string directorCopie)
		{
			if (rez.TextNou == null) return;

			// Copie de siguranta a originalului, cu structura de directoare pastrata.
			if (!string.IsNullOrEmpty(directorCopie))
			{
				var caleCopie = Path.Combine(directorCopie, CaleRelativa(rez.Cale));
				Directory.CreateDirectory(Path.GetDirectoryName(caleCopie));
				File.Copy(rez.Cale, caleCopie, true);
			}

			File.WriteAllText(rez.Cale, rez.TextNou, new UTF8Encoding(rez.AreBom));
			rez.Modificat = true;
		}

		// "C:\proiect\Foo.cs" -> "C\proiect\Foo.cs", ca sa poata fi pus sub directorul de copii.
		private static string CaleRelativa(string cale)
		{
			var plin = Path.GetFullPath(cale);
			var radacina = Path.GetPathRoot(plin);
			var rest = plin.Substring(radacina.Length);
			var litera = radacina.Replace(":", "").Replace(Path.DirectorySeparatorChar.ToString(), "").Trim();
			return string.IsNullOrEmpty(litera) ? rest : Path.Combine(litera, rest);
		}

		private static string DetecteazaNewline(string text)
		{
			var crlf = 0;
			var lf = 0;
			for (var i = 0; i < text.Length; i++)
			{
				if (text[i] != '\n') continue;
				if (i > 0 && text[i - 1] == '\r') crlf++;
				else lf++;
			}
			return crlf >= lf ? "\r\n" : "\n";
		}

		private static string DetecteazaUnitateIndent(string text)
		{
			var minSpatii = int.MaxValue;
			var inceputLinie = true;
			var spatii = 0;

			for (var i = 0; i < text.Length; i++)
			{
				var c = text[i];
				if (c == '\n') { inceputLinie = true; spatii = 0; continue; }
				if (!inceputLinie) continue;

				if (c == '\t')
					return "\t"; // fisierul foloseste taburi

				if (c == ' ') { spatii++; continue; }
				if (c == '\r') continue;

				if (spatii > 0 && spatii < minSpatii) minSpatii = spatii;
				inceputLinie = false;
				spatii = 0;
			}

			return minSpatii == int.MaxValue ? "\t" : new string(' ', minSpatii);
		}

		// Spatiul alb de la inceputul liniei pe care se afla pozitia data.
		private static string IndentLinie(string text, int pozitie)
		{
			var inceput = pozitie;
			while (inceput > 0 && text[inceput - 1] != '\n')
				inceput--;

			var i = inceput;
			while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
				i++;

			return text.Substring(inceput, i - inceput);
		}
	}
}
