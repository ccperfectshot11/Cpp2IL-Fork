/*
    Copyright (C) 2014-2026 de4dot@gmail.com

    This file is part of NetSpy / NetSpy

    NetSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    NetSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with NetSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.IO;
using dnlib.DotNet;
using de4dot.code;
using de4dot.code.AssemblyClient;
using de4dot.code.deobfuscators;
using de4dot.code.renamer;

namespace NetSpy.Deobfuscator {
	/// <summary>
	/// Result of a detect/deobfuscate run.
	/// </summary>
	sealed class DeobfuscationResult {
		/// <summary>de4dot short type, eg. "crx" for ConfuserEx, "un" for Unknown/none.</summary>
		public string ObfuscatorType { get; set; } = "un";
		/// <summary>Human-readable obfuscator name, eg. "ConfuserEx".</summary>
		public string ObfuscatorName { get; set; } = "Unknown";
		/// <summary>True when a specific (non-"un") obfuscator was recognized.</summary>
		public bool IsKnownObfuscator => ObfuscatorType != "un";
		/// <summary>Path of the cleaned file (only set after a successful deobfuscation).</summary>
		public string? OutputFile { get; set; }
		/// <summary>True if the file was successfully deobfuscated and saved.</summary>
		public bool Success { get; set; }
		/// <summary>Error message when something went wrong.</summary>
		public string? Error { get; set; }
		/// <summary>Set when symbol renaming was skipped (e.g. de4dot's renamer threw). The
		/// file is still deobfuscated and saved, just with the original obfuscated names.</summary>
		public string? RenameWarning { get; set; }
		/// <summary>True when the detected obfuscator is a commercial/paid product that NetSpy
		/// intentionally does not attempt to deobfuscate.</summary>
		public bool IsPaidObfuscator { get; set; }
	}

	/// <summary>
	/// Thin wrapper around de4dot's per-file pipeline so a single assembly can be
	/// detected and/or deobfuscated in-process. de4dot relies on global singletons
	/// (<see cref="TheAssemblyResolver"/>), so every public method here is serialized
	/// and resets that shared state after use.
	/// </summary>
	static class DeobfuscationEngine {
		static readonly object lockObj = new object();

		// Commercial/paid obfuscators (de4dot type codes). For these NetSpy shows a "can't
		// deobfuscate a paid obfuscator" message instead of attempting a (usually futile) clean.
		static readonly HashSet<string> PaidObfuscatorTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
			"dr",   // .NET Reactor
			"ef",   // Eazfuscator.NET
			"an",   // Agile.NET / CliSecure
			"bl",   // Babel.NET
			"co",   // Crypto Obfuscator
			"df",   // Dotfuscator
			"sa",   // SmartAssembly
			// "sn" (Spices.Net / 9Rays.Net) is intentionally NOT here: NetSpy fully deobfuscates it
			// (incl. its per-build-randomized string encryption via Spices5Emulator), so it is a
			// supported target even though it's a commercial obfuscator.
			"xc",   // Xenocode
			"cv",   // CodeVeil
			"cf",   // CodeFort
			"mc",   // MaxtoCode
			"il",   // ILProtector
			"vg",   // VirtualGuard
			"rm",   // Rummage
			"sk",   // Skater.NET
		};

		public static bool IsPaid(string obfuscatorType) => PaidObfuscatorTypes.Contains(obfuscatorType);

		// Same set of deobfuscators de4dot's CLI ships with (de4dot.cui/Program.cs).
		static List<IDeobfuscatorInfo> CreateDeobfuscatorInfos() => new List<IDeobfuscatorInfo> {
			new de4dot.code.deobfuscators.Unknown.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.Agile_NET.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.Babel_NET.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.CodeFort.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.CodeVeil.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.CodeWall.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.Confuser.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.ConfuserEx.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.CryptoObfuscator.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.DeepSea.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.Dotfuscator.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.dotNET_Reactor.v3.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.dotNET_Reactor.v4.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.Eazfuscator_NET.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.Goliath_NET.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.ILProtector.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.MaxtoCode.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.MPRESS.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.Obfuscar.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.Phoenix_Protector.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.RATMalware.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.Rummage.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.Skater_NET.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.SmartAssembly.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.Spices_Net.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.VirtualGuard.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.Xenocode.DeobfuscatorInfo(),
			new de4dot.code.deobfuscators.DoubleZero.DeobfuscatorInfo(),
		};

		static List<IDeobfuscator> CreateDeobfuscators(IEnumerable<IDeobfuscatorInfo> infos) {
			var list = new List<IDeobfuscator>();
			foreach (var info in infos)
				list.Add(info.CreateDeobfuscator());
			return list;
		}

		// The full de4dot default renamer behavior (rename everything + restore props/events).
		const RenamerFlags DefaultRenamerFlags =
			RenamerFlags.RenameNamespaces |
			RenamerFlags.RenameTypes |
			RenamerFlags.RenameProperties |
			RenamerFlags.RenameEvents |
			RenamerFlags.RenameFields |
			RenamerFlags.RenameMethods |
			RenamerFlags.RenameMethodArgs |
			RenamerFlags.RenameGenericParams |
			RenamerFlags.RestorePropertiesFromNames |
			RenamerFlags.RestoreEventsFromNames |
			RenamerFlags.RestoreProperties |
			RenamerFlags.RestoreEvents;

		/// <summary>
		/// Loads the file and reports which obfuscator (if any) de4dot recognizes,
		/// without modifying or saving anything.
		/// </summary>
		public static DeobfuscationResult Detect(string filename) {
			lock (lockObj) {
				var result = new DeobfuscationResult();

				// Zylofuscator isn't known to de4dot: check it with our dedicated detector first.
				if (IsZyloFile(filename)) {
					result.ObfuscatorType = "zylo";
					result.ObfuscatorName = "Zylofuscator";
					result.Success = true;
					return result;
				}

				// BitMono isn't known to de4dot, and its corrupt PE/COR20 headers make de4dot's own
				// load throw before detection — so check it before the de4dot path.
				if (BitMonoDeobfuscator.IsBitMono(filename)) {
					result.ObfuscatorType = "bitmono";
					result.ObfuscatorName = "BitMono";
					result.Success = true;
					return result;
				}

				// Custom/attribute-stripped ConfuserEx: recognize it structurally and force it, so
				// decoy attributes (Dotfuscator/Goliath/SmartAssembly/... classes planted in the
				// assembly to mislead de4dot's attribute-based detection) can't hijack the result.
				if (IsConfuserExFile(filename)) {
					result.ObfuscatorType = "crx";
					result.ObfuscatorName = "ConfuserEx";
					result.Success = true;
					return result;
				}

				try {
					var deobContext = new DeobfuscatorContext();
					var options = new ObfuscatedFile.Options {
						Filename = filename,
						ControlFlowDeobfuscation = true,
					};
					AddInputSearchDir(filename);
					var moduleContext = new ModuleContext(TheAssemblyResolver.Instance);
					using var file = new ObfuscatedFile(options, moduleContext, new NewAppDomainAssemblyClientFactory());
					file.DeobfuscatorContext = deobContext;
					file.Load(CreateDeobfuscators(CreateDeobfuscatorInfos()));

					var deob = file.Deobfuscator;
					result.ObfuscatorType = deob.Type;
					result.ObfuscatorName = string.IsNullOrEmpty(deob.Name) ? deob.TypeLong : deob.Name;
					result.IsPaidObfuscator = IsPaid(deob.Type);
					result.Success = true;
				}
				catch (Exception ex) {
					result.Error = ex.Message;
				}
				finally {
					ResetSharedState();
				}
				return result;
			}
		}

		/// <summary>
		/// Runs the full de4dot pipeline on <paramref name="filename"/> and writes the
		/// cleaned assembly to <paramref name="outputFilename"/>.
		/// </summary>
		/// <param name="renameSymbols">Rename obfuscated symbols to readable names.</param>
		public static DeobfuscationResult Deobfuscate(string filename, string outputFilename, bool renameSymbols = true) {
			lock (lockObj) {
				var result = new DeobfuscationResult();

				// Zylofuscator: use our dedicated cleaner (de4dot can't handle it).
				if (IsZyloFile(filename)) {
					result.ObfuscatorType = "zylo";
					result.ObfuscatorName = "Zylofuscator";
					try {
						if (ZyloDeobfuscator.Run(filename, outputFilename)) {
							result.OutputFile = outputFilename;
							result.Success = true;
						}
						else
							result.Error = "Zylo deobfuscation produced no output";
					}
					catch (Exception ex) {
						result.Error = ex.Message;
					}
					return result;
				}

				// BitMono: dedicated cleaner (de4dot can't handle it; corrupt headers block loading).
				if (BitMonoDeobfuscator.IsBitMono(filename)) {
					result.ObfuscatorType = "bitmono";
					result.ObfuscatorName = "BitMono";
					try {
						if (BitMonoDeobfuscator.Run(filename, outputFilename)) {
							result.OutputFile = outputFilename;
							result.Success = true;
						}
						else
							result.Error = "BitMono deobfuscation produced no output";
					}
					catch (Exception ex) {
						result.Error = ex.Message;
					}
					return result;
				}

				// ConfuserEx anti-tamper pre-pass: recover the encrypted method bodies BEFORE de4dot
				// runs (de4dot only stubs them). If it isn't ConfuserEx anti-tamper this is a no-op.
				string deobInput = filename;
				try {
					var atDir = Path.Combine(Path.GetTempPath(), "NetSpy-Deobfuscated");
					Directory.CreateDirectory(atDir);
					var atOut = Path.Combine(atDir, Path.GetFileNameWithoutExtension(filename) + "-unat" + Path.GetExtension(filename));
					if (ConfuserExAntiTamper.TryUnpack(filename, atOut)) {
						deobInput = atOut;
						DeobLog.Log($"ConfuserEx anti-tamper removed, recovered bodies: {atOut}");
					}
				}
				catch (Exception ex) {
					DeobLog.Log("Anti-tamper pre-pass error (continuing without it)", ex);
				}

				ObfuscatedFile? file = null;
				try {
					var deobContext = new DeobfuscatorContext();
					var options = new ObfuscatedFile.Options {
						Filename = deobInput,
						NewFilename = outputFilename,
						ControlFlowDeobfuscation = true,
						KeepObfuscatorTypes = false,
						PreserveTokens = false,
						RenamerFlags = DefaultRenamerFlags,
						// Force ConfuserEx past decoy attributes when the structural signature matches.
						ForcedObfuscatorType = IsConfuserExFile(deobInput) ? "crx" : null,
					};
					AddInputSearchDir(filename);
					AddInputSearchDir(deobInput);
					var moduleContext = new ModuleContext(TheAssemblyResolver.Instance);
					file = new ObfuscatedFile(options, moduleContext, new NewAppDomainAssemblyClientFactory());
					file.DeobfuscatorContext = deobContext;

					// 1) Load + auto-detect the obfuscator
					file.Load(CreateDeobfuscators(CreateDeobfuscatorInfos()));
					var deob = file.Deobfuscator;
					result.ObfuscatorType = deob.Type;
					result.ObfuscatorName = string.IsNullOrEmpty(deob.Name) ? deob.TypeLong : deob.Name;

					// 2) Static deobfuscation passes (strings, control flow, proxy calls, ...)
					//    Order mirrors de4dot: Begin -> Deobfuscate -> End -> CleanUp -> Rename -> Save
					file.DeobfuscateBegin();
					file.Deobfuscate();
					file.DeobfuscateEnd();
					file.DeobfuscateCleanUp();

					// 3) Rename obfuscated identifiers to readable names (optional).
					//    de4dot's renamer can throw on malformed obfuscated metadata; if it does,
					//    keep the deobfuscated result (strings/control-flow/anti-tamper already
					//    cleaned) and just skip renaming instead of failing the whole operation.
					if (renameSymbols) {
						try {
							var renamer = new Renamer(deobContext, new List<IObfuscatedFile> { file }, DefaultRenamerFlags);
							renamer.Rename();
						}
						catch (Exception renEx) {
							result.RenameWarning = renEx.Message;
						}
					}

					// 4) Write the cleaned assembly
					file.Save();

					result.OutputFile = outputFilename;
					result.Success = true;
				}
				catch (Exception ex) {
					result.Error = ex.Message;
					DeobLog.Log($"Deobfuscate FAILED for {filename}", ex);
				}
				finally {
					file?.Dispose();
					ResetSharedState();
				}
				if (result.Success)
					DeobLog.Log($"Deobfuscate OK: {result.ObfuscatorName} -> {result.OutputFile}" + (result.RenameWarning is null ? " (renamed)" : " (rename skipped)"));
				return result;
			}
		}

		/// <summary>Loads the module briefly to test for Zylofuscator markers.</summary>
		static bool IsZyloFile(string filename) {
			try {
				using var mod = ModuleDefMD.Load(filename);
				return ZyloDeobfuscator.IsZylo(mod);
			}
			catch {
				return false;
			}
		}

		// ConfuserEx renames the helper methods and nested types it injects into <Module> (proxies,
		// anti-tamper, control-flow) to _<16 hex digits>_ . Nothing else does this, and it survives
		// even when the ConfuserEx marker attribute is stripped or decoy attributes are added.
		static readonly System.Text.RegularExpressions.Regex confuserExName =
			new System.Text.RegularExpressions.Regex("^_[0-9A-Fa-f]{16}_$", System.Text.RegularExpressions.RegexOptions.Compiled);

		public static bool IsConfuserEx(ModuleDefMD module) {
			var gt = module.GlobalType;
			if (gt == null)
				return false;
			int hits = 0;
			foreach (var m in gt.Methods) {
				var n = m.Name?.String;
				if (n != null && confuserExName.IsMatch(n))
					hits++;
			}
			foreach (var t in gt.NestedTypes) {
				var n = t.Name?.String;
				if (n != null && confuserExName.IsMatch(n))
					hits++;
			}
			return hits >= 3;
		}

		static bool IsConfuserExFile(string filename) {
			try {
				using var mod = ModuleDefMD.Load(filename);
				return IsConfuserEx(mod);
			}
			catch {
				return false;
			}
		}

		/// <summary>Lets de4dot resolve the target's sibling assemblies (dependencies).</summary>
		static void AddInputSearchDir(string filename) {
			try {
				var dir = Path.GetDirectoryName(Path.GetFullPath(filename));
				if (!string.IsNullOrEmpty(dir))
					TheAssemblyResolver.Instance.AddSearchDirectory(dir);
			}
			catch { /* best effort */ }
		}

		/// <summary>Clears de4dot's global singleton state between runs.</summary>
		static void ResetSharedState() {
			try { TheAssemblyResolver.Instance.ClearAll(); }
			catch { /* best effort */ }
		}
	}
}
