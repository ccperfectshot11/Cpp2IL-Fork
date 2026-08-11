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

using NetSpy.Contracts.Menus;

namespace NetSpy.Contracts.Hex.Editor {
	/// <summary>
	/// Creates a <see cref="IGuidObjectsProvider"/> that uses <see cref="IHexMarginContextMenuHandler"/>s
	/// to create objects.
	/// </summary>
	public abstract class HexMarginContextMenuService {
		/// <summary>
		/// Constructor
		/// </summary>
		protected HexMarginContextMenuService() { }

		/// <summary>
		/// Creates a <see cref="IGuidObjectsProvider"/>
		/// </summary>
		/// <param name="wpfHexViewHost">Hex view host</param>
		/// <param name="margin">Margin</param>
		/// <param name="marginName">Margin name</param>
		/// <returns></returns>
		public abstract IGuidObjectsProvider Create(WpfHexViewHost wpfHexViewHost, WpfHexViewMargin margin, string marginName);
	}
}
