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
using System.Threading;
using dnlib.DotNet;
using NetSpy.Contracts.Decompiler;
using NetSpy.Contracts.Documents.Tabs.DocViewer;
using NetSpy.Contracts.Documents.TreeView;
using NetSpy.Contracts.Documents.TreeView.Resources;
using NetSpy.Contracts.TreeView;
using NetSpy.Properties;

namespace NetSpy.Documents.TreeView.Resources {
	sealed class UnknownResourceNodeImpl : UnknownResourceNode, IDecompileSelf {
		public override Guid Guid => new Guid(DocumentTreeViewConstants.UNKNOWN_RESOURCE_NODE_GUID);

		public UnknownResourceNodeImpl(ITreeNodeGroup treeNodeGroup, Resource resource)
			: base(treeNodeGroup, resource) {
		}

		public override void WriteShort(IDecompilerOutput output, IDecompiler decompiler, bool showOffset) {
			base.WriteShort(output, decompiler, showOffset);
			if (output is IDocumentViewerOutput documentViewerOutput) {
				documentViewerOutput.AddButton(NetSpy_Resources.SaveResourceButton, () => Save());
				documentViewerOutput.WriteLine();
				documentViewerOutput.WriteLine();
			}
		}

		public override string? ToString(CancellationToken token, bool canDecompile) {
			if (Resource is EmbeddedResource er)
				return ResourceUtilities.TryGetString(er.CreateReader().AsStream());
			return null;
		}

		public bool Decompile(IDecompileNodeContext context) {
			if (Resource is EmbeddedResource er)
				return ResourceUtilities.Decompile(context, er.CreateReader().AsStream(), er.Name);
			return false;
		}
	}
}
