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

using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using NetSpy.Contracts.Documents;
using NetSpy.Contracts.Documents.TreeView;
using NetSpy.Contracts.Images;
using NetSpy.Contracts.Menus;
using NetSpy.Contracts.TreeView;

namespace NetSpy.Deobfuscator {
	/// <summary>
	/// "Deobfuscate (de4dot)" context-menu item shown on assembly / module tree nodes.
	/// </summary>
	[ExportMenuItem(Header = "Deobfuscate (de4dot)", Icon = DsImagesAttribute.Process,
		Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 100)]
	sealed class DeobfuscateDocumentCommand : MenuItemBase {
		readonly IDeobfuscationService deobfuscationService;

		[ImportingConstructor]
		DeobfuscateDocumentCommand(IDeobfuscationService deobfuscationService) =>
			this.deobfuscationService = deobfuscationService;

		public override bool IsVisible(IMenuItemContext context) => GetDocuments(context).Any();

		public override void Execute(IMenuItemContext context) {
			foreach (var doc in GetDocuments(context)) {
				// NetSpy only targets free / open-source obfuscators. Paid / commercial ones are out
				// of scope even from the explicit menu command - tell the user instead of attempting.
				var detection = DeobfuscationEngine.Detect(doc.Filename);
				if (detection.Success && detection.IsKnownObfuscator && detection.IsPaidObfuscator) {
					deobfuscationService.NotifyPaidObfuscator(doc.Filename, detection.ObfuscatorName);
					continue;
				}
				deobfuscationService.DeobfuscateAndReload(doc, silent: false);
			}
		}

		static IEnumerable<IDsDocument> GetDocuments(IMenuItemContext context) {
			if (context.CreatorObject.Guid != new System.Guid(MenuConstants.GUIDOBJ_DOCUMENTS_TREEVIEW_GUID))
				return Enumerable.Empty<IDsDocument>();
			var nodes = context.Find<TreeNodeData[]>();
			if (nodes is null)
				return Enumerable.Empty<IDsDocument>();
			return nodes.Select(n => n.GetDocumentNode()?.Document)
				.Where(d => d is not null && !string.IsNullOrEmpty(d.Filename))
				.Select(d => d!)
				.Distinct();
		}
	}
}
