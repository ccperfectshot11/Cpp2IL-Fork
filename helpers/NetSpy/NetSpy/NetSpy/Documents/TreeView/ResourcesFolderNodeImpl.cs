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
using dnlib.DotNet;
using NetSpy.Contracts.Decompiler;
using NetSpy.Contracts.Documents.TreeView;
using NetSpy.Contracts.Images;
using NetSpy.Contracts.Text;
using NetSpy.Contracts.TreeView;
using NetSpy.Properties;

namespace NetSpy.Documents.TreeView {
	sealed class ResourcesFolderNodeImpl : ResourcesFolderNode {
		public override Guid Guid => new Guid(DocumentTreeViewConstants.RESOURCES_FOLDER_NODE_GUID);
		protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) => DsImages.FolderClosed;
		protected override ImageReference? GetExpandedIcon(IDotNetImageService dnImgMgr) => DsImages.FolderOpened;
		public override NodePathName NodePathName => new NodePathName(Guid);
		public override void Initialize() => TreeNode.LazyLoading = true;
		public override ITreeNodeGroup? TreeNodeGroup { get; }

		readonly ModuleDef module;

		public ResourcesFolderNodeImpl(ITreeNodeGroup treeNodeGroup, ModuleDef module) {
			TreeNodeGroup = treeNodeGroup;
			this.module = module;
		}

		protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler, DocumentNodeWriteOptions options) {
			output.Write(BoxedTextColor.Text, NetSpy_Resources.ResourcesFolder);
			if ((options & DocumentNodeWriteOptions.ToolTip) != 0) {
				output.WriteLine();
				WriteFilename(output);
			}
		}

		public override IEnumerable<TreeNodeData> CreateChildren() {
			var treeNodeGroup = Context.DocumentTreeView.DocumentTreeNodeGroups.GetGroup(DocumentTreeNodeGroupType.ResourceTreeNodeGroup);
			foreach (var resource in module.Resources)
				yield return Context.ResourceNodeFactory.Create(module, resource, treeNodeGroup);
		}

		public override FilterType GetFilterType(IDocumentTreeNodeFilter filter) {
			var res = filter.GetResult(this);
			if (res.FilterType != FilterType.Default)
				return res.FilterType;
			return FilterType.CheckChildren;
		}
	}
}
