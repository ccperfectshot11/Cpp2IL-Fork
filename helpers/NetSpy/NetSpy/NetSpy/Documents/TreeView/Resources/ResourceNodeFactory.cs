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
using System.ComponentModel.Composition;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Resources;
using NetSpy.Contracts.Documents.TreeView;
using NetSpy.Contracts.Documents.TreeView.Resources;
using NetSpy.Contracts.TreeView;

namespace NetSpy.Documents.TreeView.Resources {
	[Export(typeof(IResourceNodeFactory))]
	sealed class ResourceNodeFactory : IResourceNodeFactory {
		readonly Lazy<IResourceNodeProvider, IResourceNodeProviderMetadata>[] resourceNodeProviders;

		[ImportingConstructor]
		public ResourceNodeFactory([ImportMany] IEnumerable<Lazy<IResourceNodeProvider, IResourceNodeProviderMetadata>> resourceNodeProviders) => this.resourceNodeProviders = resourceNodeProviders.OrderBy(a => a.Metadata.Order).ToArray();

		public DocumentTreeNodeData Create(ModuleDef module, Resource resource, ITreeNodeGroup treeNodeGroup) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			if (resource is null)
				throw new ArgumentNullException(nameof(resource));
			if (treeNodeGroup is null)
				throw new ArgumentNullException(nameof(treeNodeGroup));
			var node = CreateNode(module, resource, treeNodeGroup);
			ResourceNode.AddResource(node, resource);
			return node;
		}

		DocumentTreeNodeData CreateNode(ModuleDef module, Resource resource, ITreeNodeGroup treeNodeGroup) {
			foreach (var provider in resourceNodeProviders) {
				try {
					var node = provider.Value.Create(module, resource, treeNodeGroup);
					if (node is not null)
						return node;
				}
				catch {
				}
			}
			return new UnknownResourceNodeImpl(treeNodeGroup, resource);
		}

		public DocumentTreeNodeData Create(ModuleDef module, ResourceElement resourceElement, ITreeNodeGroup treeNodeGroup) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			if (resourceElement is null)
				throw new ArgumentNullException(nameof(resourceElement));
			if (treeNodeGroup is null)
				throw new ArgumentNullException(nameof(treeNodeGroup));
			var node = CreateNode(module, resourceElement, treeNodeGroup);
			ResourceElementNode.AddResourceElement(node, resourceElement);
			return node;
		}

		DocumentTreeNodeData CreateNode(ModuleDef module, ResourceElement resourceElement, ITreeNodeGroup treeNodeGroup) {
			foreach (var provider in resourceNodeProviders) {
				try {
					var node = provider.Value.Create(module, resourceElement, treeNodeGroup);
					if (node is not null)
						return node;
				}
				catch {
				}
			}
			return new BuiltInResourceElementNodeImpl(treeNodeGroup, resourceElement);
		}
	}
}
