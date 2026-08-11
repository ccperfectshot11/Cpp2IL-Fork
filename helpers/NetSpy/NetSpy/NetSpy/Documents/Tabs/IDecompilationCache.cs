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
using NetSpy.Contracts.Decompiler;
using NetSpy.Contracts.Documents;
using NetSpy.Contracts.Documents.Tabs.DocViewer;
using NetSpy.Contracts.Documents.TreeView;
using Microsoft.VisualStudio.Utilities;

namespace NetSpy.Documents.Tabs {
	/// <summary>
	/// Caches decompiled code
	/// </summary>
	interface IDecompilationCache {
		/// <summary>
		/// Looks up cached output
		/// </summary>
		/// <param name="decompiler">Decompiler</param>
		/// <param name="nodes">Nodes</param>
		/// <param name="contentType">Content type</param>
		/// <returns></returns>
		DocumentViewerContent? Lookup(IDecompiler decompiler, DocumentTreeNodeData[] nodes, out IContentType? contentType);

		/// <summary>
		/// Cache decompiled output
		/// </summary>
		/// <param name="decompiler">Decompiler</param>
		/// <param name="nodes">Nodes</param>
		/// <param name="content">Content</param>
		/// <param name="contentType">Content type</param>
		void Cache(IDecompiler decompiler, DocumentTreeNodeData[] nodes, DocumentViewerContent content, IContentType contentType);

		/// <summary>
		/// Clear the cache
		/// </summary>
		void ClearAll();

		/// <summary>
		/// Clear everything referencing <paramref name="modules"/>
		/// </summary>
		/// <param name="modules">Module</param>
		void Clear(HashSet<IDsDocument?> modules);
	}
}
