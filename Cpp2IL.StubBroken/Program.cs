using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Cpp2IL.StubBroken
{
	internal static class Program
	{
		private static int Main(string[] args)
		{
			if (args.Length == 0)
			{
				Optiuni.Ajutor();
				return 1;
			}

			Optiuni opt;
			try
			{
				opt = Optiuni.Parseaza(args);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine("Eroare de argumente: " + ex.Message);
				Console.Error.WriteLine();
				Optiuni.Ajutor();
				return 1;
			}

			var cronometru = System.Diagnostics.Stopwatch.StartNew();

			var erori = CititorErori.Citeste(opt.FisierErori, opt.Remapari, out var randuriNeparsate);
			Console.WriteLine("Erori citite:            " + erori.Count);
			if (randuriNeparsate > 0)
				Console.WriteLine("Randuri neparsate:       " + randuriNeparsate);

			var peFisier = erori
				.GroupBy(e => CititorErori.Cheie(e.Cale), StringComparer.Ordinal)
				.ToList();

			if (!string.IsNullOrEmpty(opt.FiltruCale))
			{
				var filtru = opt.FiltruCale.Replace('/', Path.DirectorySeparatorChar).ToLowerInvariant();
				peFisier = peFisier.Where(g => g.Key.IndexOf(filtru, StringComparison.Ordinal) >= 0).ToList();
			}

			if (opt.LimitaFisiere < peFisier.Count)
				peFisier = peFisier.Take(opt.LimitaFisiere).ToList();

			Console.WriteLine("Fisiere cu erori:        " + peFisier.Count);
			Console.WriteLine("Mod:                     " + (opt.Aplica ? "APLIC (scriu pe disc)" : "analiza (nu scriu nimic)"));
			if (opt.Aplica && !string.IsNullOrEmpty(opt.DirectorCopieSiguranta))
				Console.WriteLine("Copie de siguranta in:   " + opt.DirectorCopieSiguranta);
			Console.WriteLine();

			// --- Faza 1: analizam tot, fara sa scriem nimic ---
			var rezultate = new List<RezultatFisier>(peFisier.Count);

			foreach (var grup in peFisier)
			{
				var cale = grup.First().Cale;
				try
				{
					rezultate.Add(ProcesorFisier.Analizeaza(cale, grup.ToList()));
				}
				catch (Exception ex)
				{
					rezultate.Add(new RezultatFisier
					{
						Cale = cale,
						Refuzat = true,
						MotivRefuz = ex.GetType().Name + ": " + ex.Message,
					});
				}
			}

			// --- Faza 2: lista de erori mai corespunde fisierelor de pe disc? ---
			var invechite = rezultate.Where(r => r.ListaInvechita).ToList();
			var opresteTot = false;

			if (invechite.Count > 0)
			{
				var procent = 100.0 * invechite.Count / Math.Max(rezultate.Count, 1);
				Console.WriteLine("!!! ATENTIE: lista de erori nu mai corespunde fisierelor de pe disc !!!");
				Console.WriteLine("Fisiere cu coordonate invechite: " + invechite.Count
					+ " din " + rezultate.Count + " (" + procent.ToString("0.0", CultureInfo.InvariantCulture) + "%)");
				Console.WriteLine("Aceste fisiere au fost SARITE in intregime (pozitiile deplasate ar ciotit metode sanatoase).");
				Console.WriteLine("Regenereaza lista de erori recompiland proiectul, apoi ruleaza din nou.");
				Console.WriteLine();

				if (procent > 5.0 && !opt.Forteaza)
				{
					opresteTot = true;
					Console.WriteLine(">>> Peste 5% din fisiere au coordonate invechite: NU SCRIU NIMIC.");
					Console.WriteLine(">>> Daca stii sigur ca e in regula, adauga --force.");
					Console.WriteLine();
				}
			}

			// --- Faza 3: scriem ---
			if (opt.Aplica && !opresteTot)
			{
				foreach (var r in rezultate.Where(r => r.TextNou != null))
				{
					try
					{
						ProcesorFisier.Scrie(r, opt.DirectorCopieSiguranta);
					}
					catch (Exception ex)
					{
						r.Refuzat = true;
						r.MotivRefuz = "scriere esuata: " + ex.Message;
					}
				}
			}

			cronometru.Stop();
			ScrieRapoarte(opt, rezultate);
			ScrieSumar(rezultate, cronometru.Elapsed, opt, opresteTot);
			return opresteTot ? 2 : 0;
		}

		private static void ScrieSumar(List<RezultatFisier> rezultate, TimeSpan durata, Optiuni opt, bool opresteTot)
		{
			var toateErorile = rezultate.SelectMany(r => r.Erori).ToList();

			var ciotiriNoi = rezultate.Sum(r => r.CiotiriNoi);
			var fisiereAtinse = rezultate.Count(r => r.CiotiriNoi > 0);
			var scrise = rezultate.Count(r => r.Modificat);
			var lipsa = rezultate.Count(r => r.Lipseste);
			var refuzate = rezultate.Where(r => r.Refuzat).ToList();
			var invechite = rezultate.Where(r => r.ListaInvechita).ToList();

			Console.WriteLine("=== CE SE POATE CIOTI ===");
			Console.WriteLine("Membri de ciotit:                " + ciotiriNoi);
			Console.WriteLine("Fisiere de atins:                " + fisiereAtinse);
			Console.WriteLine("Fisiere scrise efectiv:          " + scrise);
			if (invechite.Count > 0)
				Console.WriteLine("Fisiere sarite (lista veche):    " + invechite.Count);
			Console.WriteLine();

			Console.WriteLine("=== CLASIFICAREA CELOR " + toateErorile.Count + " DE ERORI ===");
			foreach (var g in toateErorile.GroupBy(e => e.Clasificare).OrderByDescending(g => g.Count()))
				Console.WriteLine("  " + g.Key.ToString().PadRight(18) + g.Count().ToString().PadLeft(7));
			Console.WriteLine();

			var nefixabile = toateErorile
				.Where(e => e.Clasificare == Clasificare.InSemnatura
					|| e.Clasificare == Clasificare.LaNivelDeTip
					|| e.Clasificare == Clasificare.FaraCorp)
				.ToList();

			if (nefixabile.Count > 0)
			{
				Console.WriteLine("=== ERORI CARE **NU** SE REZOLVA PRIN CIOT (" + nefixabile.Count + ") ===");
				Console.WriteLine("Pe fel de constructie:");
				foreach (var g in nefixabile.GroupBy(e => e.Clasificare + " / " + e.FelNod).OrderByDescending(g => g.Count()).Take(25))
					Console.WriteLine("  " + g.Key.PadRight(40) + g.Count().ToString().PadLeft(7));
				Console.WriteLine();
				Console.WriteLine("Pe cod de eroare (primele 15):");
				foreach (var g in nefixabile.GroupBy(e => e.Eroare.Cod).OrderByDescending(g => g.Count()).Take(15))
					Console.WriteLine("  " + g.Key.PadRight(12) + g.Count().ToString().PadLeft(7));
				Console.WriteLine();
			}

			if (lipsa > 0)
				Console.WriteLine("Fisiere din lista care nu exista pe disc: " + lipsa);

			if (refuzate.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine("=== FISIERE REFUZATE (nu s-a scris nimic in ele) ===");
				foreach (var r in refuzate.Take(20))
					Console.WriteLine("  " + r.Cale + "  -> " + r.MotivRefuz);
				if (refuzate.Count > 20)
					Console.WriteLine("  ... si inca " + (refuzate.Count - 20));
			}

			Console.WriteLine();
			Console.WriteLine("Rapoarte in: " + opt.DirectorRaport);
			Console.WriteLine("Durata: " + durata.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s");

			if (opresteTot)
				Console.WriteLine();
			else if (!opt.Aplica)
			{
				Console.WriteLine();
				Console.WriteLine(">>> Nu s-a modificat niciun fisier. Adauga --apply ca sa scrie efectiv.");
			}
		}

		private static void ScrieRapoarte(Optiuni opt, List<RezultatFisier> rezultate)
		{
			Directory.CreateDirectory(opt.DirectorRaport);
			var enc = new UTF8Encoding(false);

			// 1. Fiecare membru ciotit.
			using (var w = new StreamWriter(Path.Combine(opt.DirectorRaport, "ciotite.tsv"), false, enc))
			{
				w.WriteLine("fisier\tlinie\tfel\tmembru\tcoduri");
				foreach (var r in rezultate)
					foreach (var t in r.Tinte.OrderBy(t => t.LinieSemnatura))
						w.WriteLine(string.Join("\t", r.Cale, t.LinieSemnatura, t.Fel, t.Nume, string.Join(",", t.Coduri)));
			}

			// 2. Fiecare eroare care NU se rezolva prin ciot - de reparat altfel.
			using (var w = new StreamWriter(Path.Combine(opt.DirectorRaport, "nefixabile.tsv"), false, enc))
			{
				w.WriteLine("fisier\tlinie\tcoloana\tcod\tclasificare\tconstructie\tmembru");
				foreach (var r in rezultate)
					foreach (var e in r.Erori.Where(e => e.Clasificare != Clasificare.InCorp))
						w.WriteLine(string.Join("\t", r.Cale, e.Eroare.Linie, e.Eroare.Coloana, e.Eroare.Cod,
							e.Clasificare, e.FelNod, e.NumeMembru ?? ""));
			}

			// 3. Sumar pe fisier.
			using (var w = new StreamWriter(Path.Combine(opt.DirectorRaport, "pe-fisier.tsv"), false, enc))
			{
				w.WriteLine("fisier\terori\tciotite\tnefixabile\tstare");
				foreach (var r in rezultate.OrderByDescending(r => r.CiotiriNoi))
				{
					var stare = r.Lipseste ? "LIPSA"
						: r.ListaInvechita ? "SARIT: lista de erori invechita (" + r.SemnaleInvechire + " semnale)"
						: r.Refuzat ? "REFUZAT: " + r.MotivRefuz
						: r.Modificat ? "scris"
						: "analizat";
					w.WriteLine(string.Join("\t", r.Cale, r.Erori.Count, r.CiotiriNoi,
						r.Erori.Count(e => e.Clasificare != Clasificare.InCorp), stare));
				}
			}

			// 4. Matricea cod de eroare x clasificare - ca sa se vada daca vreun cod e prost clasificat.
			using (var w = new StreamWriter(Path.Combine(opt.DirectorRaport, "cod-x-clasificare.tsv"), false, enc))
			{
				w.WriteLine("cod\tInCorp\tInSemnatura\tLaNivelDeTip\tFaraCorp\tPozitieInvalida\ttotal");
				var toate = rezultate.SelectMany(r => r.Erori).ToList();
				foreach (var g in toate.GroupBy(e => e.Eroare.Cod).OrderByDescending(g => g.Count()))
				{
					w.WriteLine(string.Join("\t", g.Key,
						g.Count(e => e.Clasificare == Clasificare.InCorp),
						g.Count(e => e.Clasificare == Clasificare.InSemnatura),
						g.Count(e => e.Clasificare == Clasificare.LaNivelDeTip),
						g.Count(e => e.Clasificare == Clasificare.FaraCorp),
						g.Count(e => e.Clasificare == Clasificare.PozitieInvalida),
						g.Count()));
				}
			}
		}
	}
}
