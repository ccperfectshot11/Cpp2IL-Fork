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

using NetSpy.Analyzer.TreeNodes;
using NetSpy.Contracts.Decompiler;
using NetSpy.Contracts.Documents;
using NetSpy.Contracts.Images;
using NetSpy.Contracts.TreeView;
using NetSpy.Contracts.TreeView.Text;

namespace NetSpy.Analyzer {
	sealed class AnalyzerTreeNodeDataContext : IAnalyzerTreeNodeDataContext {
#pragma warning disable CS8618 // Non-nullable field is uninitialized.
		public IDotNetImageService DotNetImageService { get; set; }
		public ITreeView TreeView { get; set; }
		public IDecompiler Decompiler { get; set; }
		public ITreeViewNodeTextElementProvider TreeViewNodeTextElementProvider { get; set; }
		public IDsDocumentService DocumentService { get; set; }
		public IAnalyzerService AnalyzerService { get; set; }
		public bool ShowToken { get; set; }
		public bool SingleClickExpandsChildren { get; set; }
		public bool SyntaxHighlight { get; set; }
#pragma warning restore CS8618 // Non-nullable field is uninitialized.
	}
}
