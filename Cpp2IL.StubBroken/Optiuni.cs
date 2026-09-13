using System;
using System.Collections.Generic;
using System.IO;

namespace Cpp2IL.StubBroken
{
	// Optiunile din linia de comanda. Implicit unealta NU scrie nimic (dry-run),
	// tocmai ca sa nu se strice proiectul dintr-o rulare din greseala.
	internal sealed class Optiuni
	{
		public string FisierErori;
		public bool Aplica;
		public bool Forteaza;
		public string DirectorCopieSiguranta;
		public string DirectorRaport;
		public string FiltruCale;
		public int LimitaFisiere = int.MaxValue;
		public readonly List<(string De, string La)> Remapari = new List<(string, string)>();

		public static Optiuni Parseaza(string[] args)
		{
			var o = new Optiuni();

			for (var i = 0; i < args.Length; i++)
			{
				var a = args[i];
				switch (a)
				{
					case "--errors":
					case "--erori":
						o.FisierErori = Urmatorul(args, ref i, a);
						break;

					// --map "C:\vechi=C:\nou" rescrie inceputul cailor din fisierul de erori.
					// Asa poate rula peste alt export fara sa regenerezi lista de erori.
					case "--map":
						{
							var v = Urmatorul(args, ref i, a);
							var poz = v.IndexOf('=');
							if (poz <= 0)
								throw new ArgumentException("--map are nevoie de forma <de>=<la>, am primit: " + v);
							o.Remapari.Add((v.Substring(0, poz), v.Substring(poz + 1)));
							break;
						}

					case "--apply":
					case "--aplica":
						o.Aplica = true;
						break;

					// Trece peste oprirea automata cand lista de erori pare invechita.
					case "--force":
					case "--forteaza":
						o.Forteaza = true;
						break;

					// Copie de siguranta a fisierelor ORIGINALE, inainte de a fi rescrise.
					case "--backup":
					case "--copie":
						o.DirectorCopieSiguranta = Urmatorul(args, ref i, a);
						break;

					case "--report":
					case "--raport":
						o.DirectorRaport = Urmatorul(args, ref i, a);
						break;

					// Filtru pe cale, util cand testez doar pe un subset de fisiere.
					case "--only":
					case "--doar":
						o.FiltruCale = Urmatorul(args, ref i, a);
						break;

					case "--limit":
					case "--limita":
						o.LimitaFisiere = int.Parse(Urmatorul(args, ref i, a));
						break;

					case "-h":
					case "--help":
						Ajutor();
						Environment.Exit(0);
						break;

					default:
						throw new ArgumentException("Optiune necunoscuta: " + a);
				}
			}

			if (string.IsNullOrWhiteSpace(o.FisierErori))
				throw new ArgumentException("Lipseste --errors <cale catre fisierul cu erori>");
			if (!File.Exists(o.FisierErori))
				throw new FileNotFoundException("Nu gasesc fisierul cu erori: " + o.FisierErori);

			if (string.IsNullOrWhiteSpace(o.DirectorRaport))
				o.DirectorRaport = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(o.FisierErori)) ?? ".", "raport-ciot");

			return o;
		}

		private static string Urmatorul(string[] args, ref int i, string nume)
		{
			if (i + 1 >= args.Length)
				throw new ArgumentException(nume + " are nevoie de o valoare");
			return args[++i];
		}

		public static void Ajutor()
		{
			Console.WriteLine(@"Cpp2IL.StubBroken - inlocuieste DOAR corpul metodelor care nu compileaza.

  --errors <cale>     fisierul cu erori (obligatoriu), randuri de forma
                      C:\...\Foo.cs(111,35): error CS1525
  --map <de>=<la>     rescrie inceputul cailor (repetabil)
  --apply             scrie efectiv pe disc (implicit: doar analizeaza)
  --report <director> unde pune rapoartele (implicit: langa fisierul de erori)
  --only <fragment>   proceseaza doar fisierele a caror cale contine fragmentul
  --limit <n>         proceseaza cel mult n fisiere
  --force             scrie chiar daca lista de erori pare invechita (periculos)
  --backup <director> salveaza fisierele originale acolo inainte de a le rescrie

Fara --apply nu se modifica niciun fisier; se scriu doar rapoartele.
Unealta este idempotenta: un corp deja ciotit (contine marcajul) se sare.");
		}
	}
}
