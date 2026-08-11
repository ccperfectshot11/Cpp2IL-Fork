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

namespace NetSpy.Contracts.TreeView {
	/// <summary>
	/// Selection changed event args
	/// </summary>
	public sealed class TreeViewSelectionChangedEventArgs : EventArgs {
		/// <summary>
		/// Added nodes
		/// </summary>
		public TreeNodeData[] Added { get; }

		/// <summary>
		/// Removed nodes
		/// </summary>
		public TreeNodeData[] Removed { get; }

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="added">Added nodes or null</param>
		/// <param name="removed">Removed nodes or null</param>
		public TreeViewSelectionChangedEventArgs(TreeNodeData[]? added, TreeNodeData[]? removed) {
			Added = added ?? Array.Empty<TreeNodeData>();
			Removed = removed ?? Array.Empty<TreeNodeData>();
		}
	}
}
