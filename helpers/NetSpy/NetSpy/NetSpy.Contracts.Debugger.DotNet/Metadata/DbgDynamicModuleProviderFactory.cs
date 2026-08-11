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

namespace NetSpy.Contracts.Debugger.DotNet.Metadata {
	/// <summary>
	/// Creates <see cref="DbgDynamicModuleProvider"/>s
	/// </summary>
	public abstract class DbgDynamicModuleProviderFactory {
		/// <summary>
		/// Creates a <see cref="DbgDynamicModuleProvider"/> or returns null
		/// </summary>
		/// <param name="runtime">Runtime</param>
		/// <returns></returns>
		public abstract DbgDynamicModuleProvider? Create(DbgRuntime runtime);
	}
}
