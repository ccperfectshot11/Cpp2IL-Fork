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

using VST = Microsoft.VisualStudio.Text;

namespace NetSpy.Contracts.Hex.Formatting {
	/// <summary>
	/// Sequence element
	/// </summary>
	public abstract class HexSequenceElement {
		/// <summary>
		/// Constructor
		/// </summary>
		protected HexSequenceElement() { }

		/// <summary>
		/// true to show the text
		/// </summary>
		public abstract bool ShouldRenderText { get; }

		/// <summary>
		/// Line span
		/// </summary>
		public abstract VST.Span Span { get; }
	}
}
