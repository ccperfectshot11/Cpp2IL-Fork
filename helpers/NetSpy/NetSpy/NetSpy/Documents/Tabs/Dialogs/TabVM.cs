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

using System.Linq;
using NetSpy.Contracts.Documents;
using NetSpy.Contracts.Documents.Tabs;
using NetSpy.Contracts.Documents.TreeView;
using NetSpy.Contracts.MVVM;

namespace NetSpy.Documents.Tabs.Dialogs {
	sealed class TabVM : ViewModelBase {
		public IDocumentTab Tab { get; }
		public object NameObject => this;
		public object ModuleObject => this;
		public object PathObject => this;
		public string Name => Tab.Content.Title;
		public TabsVM Owner { get; }

		readonly IDsDocument? document;

		public string Module {
			get {
				if (document is null)
					return string.Empty;
				return System.IO.Path.GetFileName(document.Filename);
			}
		}

		public string Path {
			get {
				if (document is null)
					return string.Empty;
				return document.Filename;
			}
		}

		public TabVM(TabsVM owner, IDocumentTab tab) {
			Owner = owner;
			Tab = tab;
			var node = tab.Content.Nodes.FirstOrDefault().GetDocumentNode();
			document = node?.Document;
		}
	}
}
