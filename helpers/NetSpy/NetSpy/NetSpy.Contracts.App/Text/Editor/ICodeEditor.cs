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
using NetSpy.Contracts.Controls;
using Microsoft.VisualStudio.Text;

namespace NetSpy.Contracts.Text.Editor {
	/// <summary>
	/// Code text editor
	/// </summary>
	public interface ICodeEditor : IUIObjectProvider2, IDisposable {
		/// <summary>
		/// Gets the <see cref="ITextBuffer"/> instance
		/// </summary>
		ITextBuffer TextBuffer { get; }

		/// <summary>
		/// Gets the text view
		/// </summary>
		IDsWpfTextView TextView { get; }

		/// <summary>
		/// Gets the text view host
		/// </summary>
		IDsWpfTextViewHost TextViewHost { get; }
	}
}
