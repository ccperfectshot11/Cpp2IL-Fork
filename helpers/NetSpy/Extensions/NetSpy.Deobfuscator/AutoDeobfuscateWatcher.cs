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
using System.ComponentModel.Composition;
using System.IO;
using System.Threading.Tasks;
using NetSpy.Contracts.Documents;
using NetSpy.Contracts.Extension;

namespace NetSpy.Deobfuscator {
	/// <summary>
	/// Watches for newly opened assemblies. When the user opens an obfuscated file,
	/// its obfuscator is auto-detected and, if recognized, the file is deobfuscated
	/// and the cleaned assembly replaces it in the tree.
	/// </summary>
	[ExportAutoLoaded]
	sealed class AutoDeobfuscateWatcher : IAutoLoaded {
		readonly IDeobfuscationService deobfuscationService;
		readonly DeobfuscatorSettings settings;
		readonly HashSet<string> processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		readonly object processedLock = new object();

		[ImportingConstructor]
		AutoDeobfuscateWatcher(IDsDocumentService documentService, IDeobfuscationService deobfuscationService, DeobfuscatorSettings settings) {
			this.deobfuscationService = deobfuscationService;
			this.settings = settings;
			documentService.CollectionChanged += DocumentService_CollectionChanged;
			DeobLog.Log("AutoDeobfuscateWatcher loaded and listening for opened assemblies.");
		}

		void DocumentService_CollectionChanged(object? sender, NotifyDocumentCollectionChangedEventArgs e) {
			// When a document leaves the tree, forget it so re-opening the same file later
			// gets analyzed again instead of being silently skipped.
			if (e.Type == NotifyDocumentCollectionType.Remove || e.Type == NotifyDocumentCollectionType.Clear) {
				lock (processedLock) {
					if (e.Type == NotifyDocumentCollectionType.Clear)
						processed.Clear();
					else {
						foreach (var doc in e.Documents)
							processed.Remove(GetFull(doc.Filename));
					}
				}
				return;
			}

			if (!settings.AutoDeobfuscateOnLoad)
				return;
			if (e.Type != NotifyDocumentCollectionType.Add)
				return;

			foreach (var doc in e.Documents) {
				if (!ShouldProcess(doc)) {
					DeobLog.Log($"Skipping (not a user-opened target): {doc.Filename}");
					continue;
				}
				DeobLog.Log($"Queueing auto-deobfuscation for: {doc.Filename}");
				var docToProcess = doc;
				Task.Run(() => DetectAndMaybeDeobfuscate(docToProcess));
			}
		}

		bool ShouldProcess(IDsDocument doc) {
			var filename = doc.Filename;
			if (string.IsNullOrEmpty(filename) || !File.Exists(filename))
				return false;
			// Don't touch dependency assemblies that NetSpy auto-loaded, only user-opened files.
			if (doc.IsAutoLoaded)
				return false;
			// Never analyze framework / runtime assemblies or NetSpy's own program files. They are
			// never obfuscated, and loading dozens of them on startup just churns the CPU (and would
			// needlessly hold file handles). This is the main source of "dependency" DLLs in the tree.
			if (IsFrameworkOrSelf(filename))
				return false;
			// Never re-process a file we just produced (the cleaned output keeps the original
			// name but lives under our temp folder, tracked via IsOurOutput).
			if (deobfuscationService.IsOurOutput(filename))
				return false;
			lock (processedLock) {
				if (!processed.Add(GetFull(filename)))
					return false;
			}
			return true;
		}

		// Framework/runtime/self assemblies that must never be auto-deobfuscated.
		static readonly string[] frameworkDirMarkers = {
			@"\dotnet\shared\", @"\dotnet\sdk\", @"\dotnet\packs\",
			@"\microsoft.net\", @"\reference assemblies\", @"\windows\assembly\",
		};
		static bool IsFrameworkOrSelf(string filename) {
			var full = GetFull(filename).Replace('/', '\\').ToLowerInvariant();
			foreach (var marker in frameworkDirMarkers) {
				if (full.Contains(marker))
					return true;
			}
			// NetSpy's own install directory (its extensions and their dependencies).
			var baseDir = AppContext.BaseDirectory;
			if (!string.IsNullOrEmpty(baseDir)) {
				baseDir = GetFull(baseDir).Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
				if (baseDir.Length > 0 && full.StartsWith(baseDir + "\\"))
					return true;
			}
			// The Windows directory (GAC / native images).
			try {
				var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
				if (!string.IsNullOrEmpty(win) && full.StartsWith(win.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant() + "\\"))
					return true;
			}
			catch { }
			return false;
		}

		void DetectAndMaybeDeobfuscate(IDsDocument doc) {
			try {
				var detection = DeobfuscationEngine.Detect(doc.Filename);
				DeobLog.Log($"Detection for {Path.GetFileName(doc.Filename)}: type='{detection.ObfuscatorType}' name='{detection.ObfuscatorName}' known={detection.IsKnownObfuscator} paid={detection.IsPaidObfuscator} success={detection.Success} error={detection.Error}");
				// NetSpy only targets free / open-source obfuscators. Paid / commercial ones are out
				// of scope (they are designed to resist static tools and often sit behind a license),
				// so leave those files untouched instead of attempting a partial best-effort pass.
				if (detection.Success && detection.IsKnownObfuscator && detection.IsPaidObfuscator)
					DeobLog.Log($"'{detection.ObfuscatorName}' is a paid/commercial obfuscator - out of scope, leaving {Path.GetFileName(doc.Filename)} untouched.");
				else if (detection.Success && detection.IsKnownObfuscator)
					deobfuscationService.DeobfuscateAndReload(doc, silent: true);
				else
					DeobLog.Log($"No known obfuscator on {Path.GetFileName(doc.Filename)} - leaving it untouched.");
			}
			catch (Exception ex) {
				// Auto-mode must never crash the app on a bad file.
				DeobLog.Log($"DetectAndMaybeDeobfuscate crashed for {doc.Filename}", ex);
			}
		}

		static string GetFull(string path) {
			try { return Path.GetFullPath(path); }
			catch { return path; }
		}
	}
}
