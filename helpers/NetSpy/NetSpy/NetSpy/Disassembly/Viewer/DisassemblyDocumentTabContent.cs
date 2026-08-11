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
using System.Collections.Generic;
using System.Diagnostics;
using NetSpy.Contracts.Decompiler;
using NetSpy.Contracts.Disassembly.Viewer;
using NetSpy.Contracts.Documents.Tabs;
using NetSpy.Contracts.Documents.Tabs.DocViewer;
using NetSpy.Documents.Tabs.DocViewer;
using NetSpy.Properties;
using Microsoft.VisualStudio.Utilities;

namespace NetSpy.Disassembly.Viewer {
	sealed class DisassemblyDocumentTabContent : DocumentTabContent, IDisposable {
		public override string Title { get; }
		public override object? ToolTip { get; }

		readonly IDocumentViewerContentFactoryProvider documentViewerContentFactoryProvider;
		readonly DisassemblyContentProvider contentProvider;
		readonly IContentType asmContentType;
		DocumentViewerContent? cachedDocumentViewerContent;
		IContentType? cachedContentType;
		bool isVisible;
		bool disposed;

		public DisassemblyDocumentTabContent(IDocumentViewerContentFactoryProvider documentViewerContentFactoryProvider, IContentType contentType, DisassemblyContentProvider contentProvider) {
			this.documentViewerContentFactoryProvider = documentViewerContentFactoryProvider;
			this.contentProvider = contentProvider;
			asmContentType = contentType;
			Title = contentProvider.Title ?? NetSpy_Resources.Disassembly_TabTitle;
			ToolTip = contentProvider.Description;
			contentProvider.OnContentChanged += DisassemblyContentProvider_OnContentChanged;
		}

		void DisassemblyContentProvider_OnContentChanged(object? sender, EventArgs e) {
			if (disposed)
				return;
			cachedDocumentViewerContent = null;
			if (isVisible)
				Refresh();
		}

		void Refresh() {
			var tab = DocumentTab;
			tab?.DocumentTabService.Refresh(new[] { tab });
		}

		public override DocumentTabContent Clone() =>
			new DisassemblyDocumentTabContent(documentViewerContentFactoryProvider, asmContentType, contentProvider.Clone());
		public override DocumentTabUIContext CreateUIContext(IDocumentTabUIContextLocator locator) =>
			(DocumentTabUIContext)locator.Get<IDocumentViewer>();

		public override void OnShow(IShowContext ctx) {
			Debug.Assert(!isVisible);
			isVisible = true;
			var documentViewer = (IDocumentViewer)ctx.UIContext;
			if (cachedDocumentViewerContent is null)
				(cachedDocumentViewerContent, cachedContentType) = CreateContent(documentViewer);
			documentViewer.SetContent(cachedDocumentViewerContent, cachedContentType);
		}

		(DocumentViewerContent content, IContentType contentType) CreateContent(IDocumentViewer documentViewer) {
			var factory = documentViewerContentFactoryProvider.Create();
			var disasmContent = contentProvider.GetContent();
			var output = factory.Output;
			var bracesStack = new Stack<(int pos, char brace)>();
			foreach (var info in disasmContent.Text) {
				var text = info.Text;
				if (info.Reference is not null)
					output.Write(text, info.Reference, ToDecompilerReferenceFlags(info.ReferenceFlags), info.Color);
				else
					output.Write(text, info.Color);

				for (int i = 0; i < text.Length; i++) {
					char c = text[i];
					if (c == '\n')
						bracesStack.Clear();
					int pos = output.NextPosition - text.Length + i;
					char opening = default;
					CodeBracesRangeFlags flags = default;
					switch (c) {
					case '(':
					case '{':
					case '[':
					case '<':
						bracesStack.Push((pos, c));
						break;

					case ')':
						opening = '(';
						flags = CodeBracesRangeFlags.Parentheses;
						break;

					case '}':
						opening = '{';
						flags = CodeBracesRangeFlags.CurlyBraces;
						break;

					case ']':
						opening = '[';
						flags = CodeBracesRangeFlags.SquareBrackets;
						break;

					case '>':
						opening = '<';
						flags = CodeBracesRangeFlags.AngleBrackets;
						break;
					}
					if (bracesStack.Count > 0 && bracesStack.Peek().brace == opening) {
						int startPos = bracesStack.Pop().pos;
						output.AddBracePair(new TextSpan(startPos, 1), new TextSpan(pos, 1), flags);
					}
				}
			}
			var contentType = GetContentType(disasmContent.Kind);
			return (factory.CreateContent(documentViewer, contentType), contentType);
		}

		IContentType GetContentType(DisassemblyContentKind kind) => asmContentType;

		static DecompilerReferenceFlags ToDecompilerReferenceFlags(DisassemblyReferenceFlags referenceFlags) {
			var flags = DecompilerReferenceFlags.None;
			if ((referenceFlags & DisassemblyReferenceFlags.Definition) != 0)
				flags |= DecompilerReferenceFlags.Definition;
			if ((referenceFlags & DisassemblyReferenceFlags.Local) != 0)
				flags |= DecompilerReferenceFlags.Local;
			if ((referenceFlags & DisassemblyReferenceFlags.IsWrite) != 0)
				flags |= DecompilerReferenceFlags.IsWrite;
			if ((referenceFlags & DisassemblyReferenceFlags.Hidden) != 0)
				flags |= DecompilerReferenceFlags.Hidden;
			if ((referenceFlags & DisassemblyReferenceFlags.NoFollow) != 0)
				flags |= DecompilerReferenceFlags.NoFollow;
			return flags;
		}

		public override void OnHide() {
			Debug.Assert(isVisible);
			isVisible = false;
		}

		public void Dispose() {
			disposed = true;
			contentProvider.OnContentChanged -= DisassemblyContentProvider_OnContentChanged;
			contentProvider.Dispose();
		}
	}
}
