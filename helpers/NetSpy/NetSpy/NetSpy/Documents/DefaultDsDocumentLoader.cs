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

using System.Collections.Generic;
using NetSpy.Contracts.Documents;

namespace NetSpy.Documents {
	sealed class DefaultDsDocumentLoader : IDsDocumentLoader {
		readonly IDsDocumentService documentService;

		public DefaultDsDocumentLoader(IDsDocumentService documentService) => this.documentService = documentService;

		public IDsDocument[] Load(IEnumerable<DocumentToLoad> documents) {
			var loadedDocuments = new List<IDsDocument>();
			var hash = new HashSet<IDsDocument>();
			foreach (var doc in documents) {
				if (doc.Info.Type == DocumentConstants.DOCUMENTTYPE_FILE && string.IsNullOrEmpty(doc.Info.Name))
					continue;
				var document = documentService.TryGetOrCreate(doc.Info, doc.IsAutoLoaded);
				if (document is not null && !hash.Contains(document)) {
					hash.Add(document);
					loadedDocuments.Add(document);
				}
			}
			return loadedDocuments.ToArray();
		}
	}
}
