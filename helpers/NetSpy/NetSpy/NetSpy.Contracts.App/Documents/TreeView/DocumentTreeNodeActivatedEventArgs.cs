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

namespace NetSpy.Contracts.Documents.TreeView {
	/// <summary>
	/// <see cref="DocumentTreeNodeData"/> activated event args
	/// </summary>
	public sealed class DocumentTreeNodeActivatedEventArgs : EventArgs {
		/// <summary>
		/// Activated node
		/// </summary>
		public DocumentTreeNodeData Node { get; }

		/// <summary>
		/// Set it to true if the event was handled
		/// </summary>
		public bool Handled { get; set; }

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="node">Node</param>
		public DocumentTreeNodeActivatedEventArgs(DocumentTreeNodeData node) => Node = node ?? throw new ArgumentNullException(nameof(node));
	}
}
