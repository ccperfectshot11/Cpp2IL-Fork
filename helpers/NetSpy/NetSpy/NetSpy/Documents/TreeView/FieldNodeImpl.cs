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
using dnlib.DotNet;
using NetSpy.Contracts.Decompiler;
using NetSpy.Contracts.Documents.TreeView;
using NetSpy.Contracts.Images;
using NetSpy.Contracts.Text;
using NetSpy.Contracts.TreeView;

namespace NetSpy.Documents.TreeView {
	sealed class FieldNodeImpl : FieldNode {
		public override Guid Guid => new Guid(DocumentTreeViewConstants.FIELD_NODE_GUID);
		public override NodePathName NodePathName => new NodePathName(Guid, FieldDef.FullName);
		protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) => dnImgMgr.GetImageReference(FieldDef);
		public override ITreeNodeGroup? TreeNodeGroup { get; }

		public FieldNodeImpl(ITreeNodeGroup treeNodeGroup, FieldDef field)
			: base(field) => TreeNodeGroup = treeNodeGroup;

		protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler, DocumentNodeWriteOptions options) {
			if ((options & DocumentNodeWriteOptions.ToolTip) != 0) {
				WriteMemberRef(output, decompiler, FieldDef);
				output.WriteLine();
				WriteFilename(output);
			}
			else
				new NodeFormatter().Write(output, decompiler, FieldDef, GetShowToken(options));
		}

		public override FilterType GetFilterType(IDocumentTreeNodeFilter filter) {
			var res = filter.GetResult(FieldDef);
			if (res.FilterType != FilterType.Default)
				return res.FilterType;
			if (Context.Decompiler.ShowMember(FieldDef))
				return FilterType.Visible;
			return FilterType.Hide;
		}
	}
}
