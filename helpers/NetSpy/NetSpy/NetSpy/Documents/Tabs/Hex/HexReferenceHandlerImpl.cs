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
using System.ComponentModel.Composition;
using NetSpy.Contracts.Documents.Tabs;
using NetSpy.Contracts.Hex.Editor;
using VSUTIL = Microsoft.VisualStudio.Utilities;

namespace NetSpy.Documents.Tabs.Hex {
	[Export(typeof(HexReferenceHandler))]
	[VSUTIL.Name(PredefinedHexReferenceHandlerNames.DefaultApplicationHandler)]
	sealed class HexReferenceHandlerImpl : HexReferenceHandler {
		readonly IDocumentTabService documentTabService;

		[ImportingConstructor]
		HexReferenceHandlerImpl(IDocumentTabService documentTabService) => this.documentTabService = documentTabService;

		public override bool Handle(HexView hexView, object reference, IList<string>? tags) {
			bool newTab = tags?.Contains(PredefinedHexReferenceHandlerTags.NewTab) == true;
			documentTabService.FollowReference(reference, newTab: newTab);
			return true;
		}
	}
}
