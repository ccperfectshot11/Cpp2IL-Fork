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

using NetSpy.Contracts.MVVM;

namespace NetSpy.Documents.Tabs.Dialogs {
	sealed class DocumentListVM : ViewModelBase {
		public object NameObject => this;
		public object DocumentCountObject => this;
		public string Name => Filter(DocumentList.Name);
		public int DocumentCount => DocumentList.Documents.Count;
		public OpenDocumentListVM Owner => owner;
		public DocumentList DocumentList { get; }
		public bool IsExistingList { get; }
		public bool IsUserList { get; }

		readonly OpenDocumentListVM owner;

		public DocumentListVM(OpenDocumentListVM owner, DocumentList documentList, bool isExistingList, bool isUserList) {
			this.owner = owner;
			DocumentList = documentList;
			IsExistingList = isExistingList;
			IsUserList = isUserList;
		}

		static string Filter(string s) {
			if (s is null)
				return string.Empty;
			const int MAX = 512;
			if (s.Length > MAX)
				s = s.Substring(0, MAX);
			return s;
		}
	}
}
