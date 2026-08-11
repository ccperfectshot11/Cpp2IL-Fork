/*
    Copyright (C) 2022 ElektroKill

    This file is part of NetSpy

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
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using NetSpy.BamlDecompiler.Properties;
using NetSpy.Contracts.App;
using NetSpy.Contracts.Decompiler;
using NetSpy.Contracts.Documents.Tabs;
using NetSpy.Contracts.Documents.Tabs.DocViewer;
using NetSpy.Contracts.MVVM;
using NetSpy.Decompiler;

namespace NetSpy.BamlDecompiler {
	[ExportTabSaverProvider(Order = TabConstants.ORDER_BAMLTABSAVERPROVIDER)]
	sealed class BamlTabSaverProvider : ITabSaverProvider {
		readonly IMessageBoxService messageBoxService;
		readonly IPickSaveFilename pickSaveFilename;

		[ImportingConstructor]
		BamlTabSaverProvider(IMessageBoxService messageBoxService, IPickSaveFilename pickSaveFilename) {
			this.messageBoxService = messageBoxService;
			this.pickSaveFilename = pickSaveFilename;
		}

		public ITabSaver Create(IDocumentTab tab) => BamlTabSaver.TryCreate(tab, messageBoxService, pickSaveFilename);
	}

	sealed class BamlTabSaver : ITabSaver {
		public bool CanSave => !tab.IsAsyncExecInProgress;
		public string MenuHeader => bamlNode.DisassembleBaml ? NetSpy_BamlDecompiler_Resources.SaveBAML : NetSpy_BamlDecompiler_Resources.SaveXAML;

		public static BamlTabSaver TryCreate(IDocumentTab tab, IMessageBoxService messageBoxService, IPickSaveFilename pickSaveFilename) {
			if (tab.IsAsyncExecInProgress)
				return null;
			if (tab.UIContext is not IDocumentViewer uiContext)
				return null;
			if (tab.Content is null)
				return null;
			var nodes = tab.Content.Nodes.ToArray();
			if (nodes.Length != 1)
				return null;
			if (nodes[0] is not BamlResourceElementNode bamlNode)
				return null;
			return new BamlTabSaver(tab, bamlNode, uiContext, messageBoxService, pickSaveFilename);
		}

		readonly IDocumentTab tab;
		readonly BamlResourceElementNode bamlNode;
		readonly IDocumentViewer documentViewer;
		readonly IMessageBoxService messageBoxService;
		readonly IPickSaveFilename pickSaveFilename;

		BamlTabSaver(IDocumentTab tab, BamlResourceElementNode bamlNode, IDocumentViewer documentViewer, IMessageBoxService messageBoxService, IPickSaveFilename pickSaveFilename) {
			this.tab = tab;
			this.bamlNode = bamlNode;
			this.documentViewer = documentViewer;
			this.messageBoxService = messageBoxService;
			this.pickSaveFilename = pickSaveFilename;
		}

		sealed class DecompileContext : IDisposable {
			public TextWriter Writer;
			public TextWriterDecompilerOutput Output;
			public CancellationToken Token;

			public void Dispose() => Writer?.Dispose();
		}

		static DecompileContext CreateDecompileContext(string filename) {
			var decompileContext = new DecompileContext();
			try {
				decompileContext.Writer = new StreamWriter(filename);
				decompileContext.Output = new TextWriterDecompilerOutput(decompileContext.Writer);
				return decompileContext;
			}
			catch {
				decompileContext.Dispose();
				throw;
			}
		}

		DecompileContext CreateDecompileContext() {
			string ext, name;
			if (bamlNode.DisassembleBaml) {
				ext = "baml";
				name = NetSpy_BamlDecompiler_Resources.BAMLFile;
			}
			else {
				ext = "xaml";
				name = NetSpy_BamlDecompiler_Resources.XAMLFile;
			}
			var fileName = pickSaveFilename.GetFilename(FilenameUtils.CleanName(RemovePath(bamlNode.GetFilename())), ext, $"{name}|*.{ext}|{PickFilenameConstants.AnyFilenameFilter}");
			return fileName is null ? null : CreateDecompileContext(fileName);
		}

		static string RemovePath(string s) {
			int i = s.LastIndexOf('/');
			return i < 0 ? s : s.Substring(i + 1);
		}

		public void Save() {
			if (!CanSave)
				return;

			var ctx = CreateDecompileContext();
			if (ctx is null)
				return;

			tab.AsyncExec(cs => {
				ctx.Token = cs.Token;
				documentViewer.ShowCancelButton(NetSpy_BamlDecompiler_Resources.Saving, cs.Cancel);
			}, () => {
				bamlNode.Decompile(ctx.Output, ctx.Token);
			}, result => {
				ctx.Dispose();
				documentViewer.HideCancelButton();
				if (result.Exception is not null)
					messageBoxService.Show(result.Exception);
			});
		}
	}
}
