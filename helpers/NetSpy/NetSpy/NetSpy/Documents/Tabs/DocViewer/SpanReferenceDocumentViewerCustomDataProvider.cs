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

using System.Diagnostics;
using NetSpy.Contracts.Decompiler;
using NetSpy.Contracts.Documents.Tabs.DocViewer;
using NetSpy.Contracts.Text;
using Microsoft.VisualStudio.Text;

namespace NetSpy.Documents.Tabs.DocViewer {
	[ExportDocumentViewerCustomDataProvider]
	sealed class SpanReferenceDocumentViewerCustomDataProvider : IDocumentViewerCustomDataProvider {
		public void OnCustomData(IDocumentViewerCustomDataContext context) {
			SpanDataCollection<ReferenceAndId> result;
			var data = context.GetData<SpanReference>(PredefinedCustomDataIds.SpanReference);
			if (data.Length == 0)
				result = SpanDataCollection<ReferenceAndId>.Empty;
			else {
				var builder = SpanDataCollectionBuilder<ReferenceAndId>.CreateBuilder(data.Length);
				int prevEnd = 0;
				foreach (var d in data) {
					// The data should already be sorted. We don't support overlaps at the moment.
					Debug.Assert(prevEnd <= d.Span.Start);
					if (prevEnd <= d.Span.Start) {
						builder.Add(new Span(d.Span.Start, d.Span.Length), new ReferenceAndId(d.Reference, d.Id));
						prevEnd = d.Span.End;
					}
				}
				result = builder.Create();
			}
			context.AddCustomData(DocumentViewerContentDataIds.SpanReference, result);
		}
	}
}
