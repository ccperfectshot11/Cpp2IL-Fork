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

using System.Windows;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace NetSpy.Contracts.Text.Editor {
	/// <summary>
	/// NetSpy <see cref="IWpfTextView"/>
	/// </summary>
	public interface IDsWpfTextView : IWpfTextView, IDsTextView {
		/// <summary>
		/// Invalidates classifications
		/// </summary>
		/// <param name="span">Span to invalidate</param>
		void InvalidateClassifications(SnapshotSpan span);

		/// <summary>
		/// Sets the container control
		/// </summary>
		/// <param name="container">Container control</param>
		void SetContainer(UIElement container);
	}
}
