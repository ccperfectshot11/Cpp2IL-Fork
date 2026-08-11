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

using System.ComponentModel.Composition;
using NetSpy.Contracts.Documents.Tabs.DocViewer;
using NetSpy.Contracts.Metadata;
using NetSpy.Contracts.Text.Editor;

namespace NetSpy.Documents.Tabs.DocViewer {
	[ExportDocumentViewerListener(DocumentViewerListenerConstants.ORDER_GLYPHTEXTMARKERSERVICE)]
	sealed class GlyphTextMarkerServiceDocumentViewerListener : IDocumentViewerListener {
		readonly IGlyphTextMarkerService glyphTextMarkerService;
		readonly IModuleIdProvider moduleIdProvider;

		[ImportingConstructor]
		GlyphTextMarkerServiceDocumentViewerListener(IGlyphTextMarkerService glyphTextMarkerService, IModuleIdProvider moduleIdProvider) {
			this.glyphTextMarkerService = glyphTextMarkerService;
			this.moduleIdProvider = moduleIdProvider;
		}

		void IDocumentViewerListener.OnEvent(DocumentViewerEventArgs e) {
			if (e.EventType == DocumentViewerEvent.GotNewContent)
				glyphTextMarkerService.SetDotNetSpanMap(e.DocumentViewer.TextView, new DocumentViewerDotNetSpanMap(moduleIdProvider, e.DocumentViewer.Content.MethodDebugInfos, e.DocumentViewer.ReferenceCollection));
		}
	}
}
