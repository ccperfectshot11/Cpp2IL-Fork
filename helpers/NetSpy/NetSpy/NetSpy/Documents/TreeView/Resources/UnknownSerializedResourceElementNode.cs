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

using dnlib.DotNet;
using dnlib.DotNet.Resources;
using NetSpy.Contracts.Documents.TreeView;
using NetSpy.Contracts.Documents.TreeView.Resources;
using NetSpy.Contracts.TreeView;

namespace NetSpy.Documents.TreeView.Resources {
	[ExportResourceNodeProvider(Order = DocumentTreeViewConstants.ORDER_RSRCPROVIDER_UNKNOWNSERIALIZEDRSRCELEM)]
	sealed class UnknownSerializedResourceElementNodeProvider : IResourceNodeProvider {
		public DocumentTreeNodeData? Create(ModuleDef module, Resource resource, ITreeNodeGroup treeNodeGroup) => null;

		public DocumentTreeNodeData? Create(ModuleDef module, ResourceElement resourceElement, ITreeNodeGroup treeNodeGroup) {
			if (resourceElement.ResourceData is BinaryResourceData)
				return new UnknownSerializedResourceElementNode(treeNodeGroup, resourceElement);
			return null;
		}
	}
}
