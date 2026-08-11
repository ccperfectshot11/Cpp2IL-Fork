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
using NetSpy.Contracts.Controls;
using NetSpy.Contracts.Decompiler;
using NetSpy.Contracts.Documents;
using NetSpy.Contracts.Documents.TreeView;
using NetSpy.Contracts.Documents.TreeView.Resources;
using NetSpy.Contracts.Images;
using NetSpy.Contracts.Menus;
using NetSpy.Contracts.TreeView;
using NetSpy.Contracts.TreeView.Text;

namespace NetSpy.Documents.TreeView {
	[Export(typeof(IDocumentTreeViewProvider))]
	sealed class DocumentTreeViewProvider : IDocumentTreeViewProvider {
		readonly ITreeViewService treeViewService;
		readonly IDecompilerService decompilerService;
		readonly IDsDocumentServiceProvider documentServiceProvider;
		readonly IDocumentTreeViewSettings documentTreeViewSettings;
		readonly IMenuService menuService;
		readonly IDotNetImageService dotNetImageService;
		readonly IWpfCommandService wpfCommandService;
		readonly IResourceNodeFactory resourceNodeFactory;
		readonly Lazy<IDsDocumentNodeProvider, IDsDocumentNodeProviderMetadata>[] dsDocumentNodeProviders;
		readonly Lazy<IDocumentTreeNodeDataFinder, IDocumentTreeNodeDataFinderMetadata>[] mefFinders;
		readonly ITreeViewNodeTextElementProvider treeViewNodeTextElementProvider;

		[ImportingConstructor]
		DocumentTreeViewProvider(ITreeViewService treeViewService, IDecompilerService decompilerService, IDsDocumentServiceProvider documentServiceProvider, IDocumentTreeViewSettings documentTreeViewSettings, IMenuService menuService, IDotNetImageService dotNetImageService, IWpfCommandService wpfCommandService, IResourceNodeFactory resourceNodeFactory, [ImportMany] IEnumerable<Lazy<IDsDocumentNodeProvider, IDsDocumentNodeProviderMetadata>> dsDocumentNodeProviders, [ImportMany] IEnumerable<Lazy<IDocumentTreeNodeDataFinder, IDocumentTreeNodeDataFinderMetadata>> mefFinders, ITreeViewNodeTextElementProvider treeViewNodeTextElementProvider) {
			this.treeViewService = treeViewService;
			this.decompilerService = decompilerService;
			this.documentServiceProvider = documentServiceProvider;
			this.documentTreeViewSettings = documentTreeViewSettings;
			this.menuService = menuService;
			this.dotNetImageService = dotNetImageService;
			this.wpfCommandService = wpfCommandService;
			this.resourceNodeFactory = resourceNodeFactory;
			this.dsDocumentNodeProviders = dsDocumentNodeProviders.ToArray();
			this.mefFinders = mefFinders.ToArray();
			this.treeViewNodeTextElementProvider = treeViewNodeTextElementProvider;
		}

		public IDocumentTreeView Create(IDocumentTreeNodeFilter? filter) =>
			new DocumentTreeView(false, filter, treeViewService, decompilerService, documentServiceProvider.Create(), documentTreeViewSettings, menuService, dotNetImageService, wpfCommandService, resourceNodeFactory, dsDocumentNodeProviders.ToArray(), mefFinders.ToArray(), treeViewNodeTextElementProvider, null);
	}
}
