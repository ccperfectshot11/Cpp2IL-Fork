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
using NetSpy.Roslyn.Internal.QuickInfo;
using NetSpy.Roslyn.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;

namespace NetSpy.Roslyn.Intellisense.QuickInfo {
	readonly struct QuickInfoState {
		public QuickInfoService QuickInfoService { get; }
		public Document Document { get; }
		public SourceText SourceText { get; }
		public ITextSnapshot Snapshot { get; }

		QuickInfoState(QuickInfoService quickInfoService, Document document, SourceText sourceText, ITextSnapshot snapshot) {
			QuickInfoService = quickInfoService;
			Document = document;
			SourceText = sourceText;
			Snapshot = snapshot;
		}

		public static QuickInfoState? Create(ITextSnapshot snapshot) {
			if (snapshot is null)
				throw new ArgumentNullException(nameof(snapshot));
			var sourceText = snapshot.AsText();
			var document = sourceText.GetOpenDocumentInCurrentContextWithChanges();
			if (document is null)
				return null;
			var quickInfoService = QuickInfoService.GetService(document);
			if (quickInfoService is null)
				return null;
			return new QuickInfoState(quickInfoService, document, sourceText, snapshot);
		}
	}
}
