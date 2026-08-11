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

namespace NetSpy.Contracts.Text.Editor {
	/// <summary>
	/// Indent style
	/// </summary>
	public enum IndentStyle {
		/// <summary>
		/// No indentation, always move caret to column 0
		/// </summary>
		None,

		/// <summary>
		/// Block, use same indentation as the previous line (or first non-empty line)
		/// </summary>
		Block,

		/// <summary>
		/// Use the language service to find the correct indentation
		/// </summary>
		Smart,
	}
}
