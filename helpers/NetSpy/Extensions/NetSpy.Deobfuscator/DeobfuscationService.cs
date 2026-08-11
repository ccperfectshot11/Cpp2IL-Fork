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
using System.Windows.Threading;
using NetSpy.Contracts.App;
using NetSpy.Contracts.Documents;
using NetSpy.Contracts.Documents.Tabs;

namespace NetSpy.Deobfuscator {
	interface IDeobfuscationService {
		/// <summary>Deobfuscates the given document (off the UI thread) and, on success,
		/// replaces it in the tree with the cleaned assembly.</summary>
		/// <param name="silent">When true, no dialogs are shown (used by auto-on-load).</param>
		void DeobfuscateAndReload(IDsDocument document, bool silent);

		/// <summary>Marks a path as produced by us so the auto-watcher ignores it.</summary>
		bool IsOurOutput(string filename);

		/// <summary>Tells the user the assembly is protected by a paid obfuscator we don't handle.</summary>
		void NotifyPaidObfuscator(string filename, string obfuscatorName);
	}

	[Export(typeof(IDeobfuscationService))]
	sealed class DeobfuscationService : IDeobfuscationService {
		readonly IDsDocumentService documentService;
		readonly IDocumentTabService documentTabService;
		readonly IMessageBoxService messageBoxService;
		readonly IAppWindow appWindow;
		readonly HashSet<string> ourOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		readonly object outputsLock = new object();

		[ImportingConstructor]
		DeobfuscationService(IDsDocumentService documentService, IDocumentTabService documentTabService, IMessageBoxService messageBoxService, IAppWindow appWindow) {
			this.documentService = documentService;
			this.documentTabService = documentTabService;
			this.messageBoxService = messageBoxService;
			this.appWindow = appWindow;
		}

		readonly HashSet<string> notifiedPaid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		public void NotifyPaidObfuscator(string filename, string obfuscatorName) {
			// Only tell the user once per file so auto-mode doesn't nag on every reload.
			lock (outputsLock) {
				if (!notifiedPaid.Add(GetFull(filename)))
					return;
			}
			DeobLog.Log($"Paid obfuscator '{obfuscatorName}' detected on {filename} - not deobfuscating.");
			GetDispatcher().BeginInvoke(DispatcherPriority.Background, new Action(() =>
				messageBoxService.Show(
					$"'{Path.GetFileName(filename)}' is protected by a paid obfuscator ({obfuscatorName}).\n\n" +
					"NetSpy does not deobfuscate commercial obfuscators, so the decompiled code will not be clean.",
					ownerWindow: appWindow.MainWindow)));
		}

		public bool IsOurOutput(string filename) {
			if (string.IsNullOrEmpty(filename))
				return false;
			lock (outputsLock)
				return ourOutputs.Contains(GetFull(filename));
		}

		static string GetFull(string path) {
			try { return Path.GetFullPath(path); }
			catch { return path; }
		}

		static string GetOutputPath(string inputFilename) {
			// Keep the ORIGINAL file name (so it opens like a normal, non-obfuscated assembly),
			// but put it in a per-file temp subfolder so we don't overwrite the original.
			var name = Path.GetFileNameWithoutExtension(inputFilename);
			var dir = Path.Combine(Path.GetTempPath(), "NetSpy-Deobfuscated", MakeSafe(name));
			Directory.CreateDirectory(dir);
			return Path.Combine(dir, Path.GetFileName(inputFilename));
		}

		static string MakeSafe(string name) {
			foreach (var c in Path.GetInvalidFileNameChars())
				name = name.Replace(c, '_');
			return string.IsNullOrEmpty(name) ? "asm" : name;
		}

		public void DeobfuscateAndReload(IDsDocument document, bool silent) {
			var filename = document.Filename;
			DeobLog.Log($"DeobfuscateAndReload called (silent={silent}) for: {filename}");
			if (string.IsNullOrEmpty(filename) || !File.Exists(filename)) {
				DeobLog.Log($"Aborting: file missing/empty: {filename}");
				return;
			}

			var outputPath = GetOutputPath(filename);
			lock (outputsLock)
				ourOutputs.Add(GetFull(outputPath));

			// Deobfuscation is CPU heavy: run it off the UI thread, then marshal back.
			Task.Run(() => {
				var result = DeobfuscationEngine.Deobfuscate(filename, outputPath);
				GetDispatcher().BeginInvoke(DispatcherPriority.Background, new Action(() => OnDone(document, result, silent)));
			});
		}

		void OnDone(IDsDocument original, DeobfuscationResult result, bool silent) {
			if (!result.Success || result.OutputFile is null) {
				DeobLog.Log($"Reload skipped, deobfuscation not successful for {original.Filename}: {result.Error}");
				// Always surface a real failure (even in auto mode) so it isn't silently lost.
				messageBoxService.Show($"NetSpy could not deobfuscate:\n{original.Filename}\n\n{result.Error ?? "unknown error"}\n\nSee log: {DeobLog.LogPath}", ownerWindow: appWindow.MainWindow);
				return;
			}

			try {
				// Swap the tree node: load the cleaned assembly first, and only drop the obfuscated
				// original once the clean one is really in the tree. Doing it in this order means a
				// failed load can never make the document vanish — the original just stays put.
				var info = DsDocumentInfo.CreateDocument(result.OutputFile);
				var newDoc = documentService.TryGetOrCreate(info);
				if (newDoc is null) {
					DeobLog.Log($"Cleaned assembly failed to load, keeping the original in the tree: {result.OutputFile}");
					return;
				}
				documentService.Remove(original.Key);
				DeobLog.Log($"Reloaded cleaned assembly into tree: {result.OutputFile}");
				object? @ref = (object?)newDoc.AssemblyDef ?? newDoc.ModuleDef;
				if (@ref is not null)
					documentTabService.FollowReference(@ref);
			}
			catch (Exception ex) {
				DeobLog.Log("Reload of cleaned assembly failed", ex);
				messageBoxService.Show(ex, "Could not reload the cleaned assembly", appWindow.MainWindow);
				return;
			}

			// Auto mode stays seamless (no dialog) — it just opens the clean assembly like a
			// normal decompile. Only the manual command reports what it did.
			if (!silent) {
				var what = result.IsKnownObfuscator ? result.ObfuscatorName : "no known obfuscator (generic cleanup applied)";
				var note = result.RenameWarning is null ? "" : "\n\nNote: symbol renaming was skipped (obfuscated metadata); the assembly is deobfuscated but keeps its original names.";
				messageBoxService.Show($"NetSpy deobfuscated with: {what}{note}", ownerWindow: appWindow.MainWindow);
			}
		}

		Dispatcher GetDispatcher() => appWindow.MainWindow?.Dispatcher ?? Dispatcher.CurrentDispatcher;
	}
}
