/*
    Copyright (C) 2014-2019 de4dot@gmail.com

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
using NetSpy.AsmEditor.Properties;
using NetSpy.AsmEditor.SaveModule;
using NetSpy.Contracts.Documents.Tabs;
using NetSpy.Contracts.Hex;

namespace NetSpy.AsmEditor.Hex {
	[ExportTabSaverProvider(Order = TabConstants.ORDER_HEXTABSAVERPROVIDER)]
	sealed class HexTabSaverProvider : ITabSaverProvider {
		readonly Lazy<IDocumentSaver> documentSaver;

		[ImportingConstructor]
		HexTabSaverProvider(Lazy<IDocumentSaver> documentSaver) => this.documentSaver = documentSaver;

		public ITabSaver? Create(IDocumentTab tab) => HexTabSaver.TryCreate(documentSaver, tab);
	}

	sealed class HexTabSaver : ITabSaver {
		public bool CanSave => true;
		public string? MenuHeader => NetSpy_AsmEditor_Resources.Save;

		public static ITabSaver? TryCreate(Lazy<IDocumentSaver> documentSaver, IDocumentTab tab) {
			var uiContext = tab.UIContext as HexViewDocumentTabUIContext;
			if (uiContext is null)
				return null;
			var buffer = uiContext.HexView.Buffer;
			return new HexTabSaver(documentSaver, buffer);
		}

		readonly Lazy<IDocumentSaver> documentSaver;
		readonly HexBuffer buffer;

		HexTabSaver(Lazy<IDocumentSaver> documentSaver, HexBuffer buffer) {
			this.documentSaver = documentSaver;
			this.buffer = buffer;
		}

		public void Save() => documentSaver.Value.Save(new[] { buffer });
	}
}
