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
	/// Creates <see cref="DisassemblyContentProvider"/>s
	/// </summary>
	public abstract class DisassemblyContentProviderFactory {
		/// <summary>
		/// Creates a <see cref="DisassemblyContentProvider"/> that can be passed to <see cref="DisassemblyViewerService.Show(DisassemblyContentProvider)"/>
		/// </summary>
		/// <param name="code">Native code</param>
		/// <param name="formatterOptions">Options</param>
		/// <param name="symbolResolver">Symbol resolver or null</param>
		/// <param name="header">Header comment added at the top of the document or null. This can contain multiple lines</param>
		/// <returns></returns>
		public abstract DisassemblyContentProvider Create(NativeCode code, DisassemblyContentFormatterOptions formatterOptions, ISymbolResolver? symbolResolver, string? header);
	}
}
