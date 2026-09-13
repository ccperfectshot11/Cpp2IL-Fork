using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Cpp2IL.StubBroken
{
	// O eroare de compilare, asa cum apare in fisierul de intrare.
	internal sealed class Eroare
	{
		public string Cale;
		public int Linie;
		public int Coloana;
		public string Cod;
		public string RandOriginal;
	}

	internal static class CititorErori
	{
		// Calea poate contine paranteze, asa ca lasam .+ lacom: se opreste la ULTIMUL
		// "(numar,numar):" de pe rand, care e mereu pozitia erorii.
		private static readonly Regex Tipar = new Regex(
			@"^\s*(?<cale>.+)\((?<linie>\d+)(?:,(?<col>\d+))?\)\s*:\s*(?:fatal\s+)?error\s+(?<cod>CS\d+)",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		public static List<Eroare> Citeste(string fisier, List<(string De, string La)> remapari, out int randuriNeparsate)
		{
			var rezultat = new List<Eroare>();
			randuriNeparsate = 0;

			foreach (var rand in File.ReadLines(fisier))
			{
				if (string.IsNullOrWhiteSpace(rand))
					continue;

				var m = Tipar.Match(rand);
				if (!m.Success)
				{
					randuriNeparsate++;
					continue;
				}

				var cale = m.Groups["cale"].Value.Trim();
				cale = Remapeaza(cale, remapari);

				rezultat.Add(new Eroare
				{
					Cale = cale,
					Linie = int.Parse(m.Groups["linie"].Value),
					Coloana = m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value) : 1,
					Cod = m.Groups["cod"].Value.ToUpperInvariant(),
					RandOriginal = rand,
				});
			}

			return rezultat;
		}

		private static string Remapeaza(string cale, List<(string De, string La)> remapari)
		{
			foreach (var (de, la) in remapari)
			{
				if (cale.StartsWith(de, StringComparison.OrdinalIgnoreCase))
					return la + cale.Substring(de.Length);
			}
			return cale;
		}

		// Cheia de grupare: caile pot veni cu / sau \ si cu litere mari/mici diferite.
		public static string Cheie(string cale)
		{
			try
			{
				return Path.GetFullPath(cale).Replace('/', Path.DirectorySeparatorChar).ToLowerInvariant();
			}
			catch
			{
				return cale.Replace('/', Path.DirectorySeparatorChar).ToLowerInvariant();
			}
		}
	}
}
