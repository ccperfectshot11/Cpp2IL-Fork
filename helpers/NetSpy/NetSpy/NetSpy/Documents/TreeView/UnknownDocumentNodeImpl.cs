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
using NetSpy.Contracts.Documents;
using NetSpy.Contracts.Documents.TreeView;
using NetSpy.Contracts.Images;
using NetSpy.Contracts.Text;
using NetSpy.Contracts.TreeView;

namespace NetSpy.Documents.TreeView {
	sealed class UnknownDocumentNodeImpl : UnknownDocumentNode {
		public UnknownDocumentNodeImpl(IDsDocument document)
			: base(document) => Debug2.Assert(document.PEImage is null && document.ModuleDef is null);

		public override Guid Guid => new Guid(DocumentTreeViewConstants.UNKNOWN_DOCUMENT_NODE_GUID);
		protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) => DsImages.AssemblyError;

		public override IEnumerable<TreeNodeData> CreateChildren() {
			foreach (var document in Document.Children)
				yield return Context.DocumentTreeView.CreateNode(this, document);
		}

		protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler, DocumentNodeWriteOptions options) =>
			new NodeFormatter().Write(output, decompiler, Document);
	}
}
