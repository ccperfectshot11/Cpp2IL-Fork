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

namespace NetSpy.Contracts.Disassembly.Viewer {
	/// <summary>
	/// Shows disassembled code in a disassembly viewer
	/// </summary>
	public abstract class DisassemblyViewerService {
		/// <summary>
		/// Gets the settings
		/// </summary>
		public abstract DisassemblyViewerServiceSettings Settings { get; }

		/// <summary>
		/// Shows the disassembly in a viewer
		/// </summary>
		/// <param name="contentProvider">Content provider</param>
		public void Show(DisassemblyContentProvider contentProvider) => Show(contentProvider, Settings.OpenNewTab);

		/// <summary>
		/// Shows the disassembly in a viewer
		/// </summary>
		/// <param name="contentProvider">Content provider</param>
		/// <param name="newTab">true to always create a new tab, false to re-use an existing disassembly viewer</param>
		public abstract void Show(DisassemblyContentProvider contentProvider, bool newTab);
	}
}
