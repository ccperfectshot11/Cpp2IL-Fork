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
using NetSpy.Contracts.Menus;
using Microsoft.VisualStudio.Text.Editor;

namespace NetSpy.Contracts.Text.Editor {
	/// <summary>
	/// <see cref="IWpfTextView"/> creator options
	/// </summary>
	public class TextViewCreatorOptions {
		/// <summary>
		/// Guid of context menu or null
		/// </summary>
		public Guid? MenuGuid { get; set; }

		/// <summary>
		/// Creates <see cref="GuidObject"/>s, can be null
		/// </summary>
		public Func<GuidObjectsProviderArgs, IEnumerable<GuidObject>>? CreateGuidObjects { get; set; }

		/// <summary>
		/// true to enable undo/redo history. Default value is true
		/// </summary>
		public bool EnableUndoHistory { get; set; }

		/// <summary>
		/// Clones this
		/// </summary>
		/// <returns></returns>
		public TextViewCreatorOptions Clone() => CopyTo(new TextViewCreatorOptions());

		/// <summary>
		/// Constructor
		/// </summary>
		public TextViewCreatorOptions() => EnableUndoHistory = true;

		/// <summary>
		/// Copy this to <paramref name="other"/>
		/// </summary>
		/// <param name="other">Other instance</param>
		/// <returns></returns>
		public TextViewCreatorOptions CopyTo(TextViewCreatorOptions other) {
			if (other is null)
				throw new ArgumentNullException(nameof(other));
			other.MenuGuid = MenuGuid;
			other.CreateGuidObjects = CreateGuidObjects;
			other.EnableUndoHistory = EnableUndoHistory;
			return other;
		}
	}
}
