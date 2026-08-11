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

using NetSpy.Contracts.Debugger.DotNet;
using NetSpy.Debugger.DotNet.Metadata;

namespace NetSpy.Contracts.Debugger {
	/// <summary>
	/// Extension methods
	/// </summary>
	public static class DbgModuleExtensions {
		/// <summary>
		/// Gets the reflection module or null if this isn't a managed module
		/// </summary>
		/// <param name="module">Debugger module</param>
		/// <returns></returns>
		public static DmdModule? GetReflectionModule(this DbgModule module) => (module.InternalModule as DbgDotNetInternalModule)?.ReflectionModule;

		/// <summary>
		/// Gets the internal .NET module or null if it's not a managed module
		/// </summary>
		/// <param name="module"></param>
		/// <returns></returns>
		public static DbgDotNetInternalModule? GetDotNetInternalModule(this DbgModule module) => module.InternalModule as DbgDotNetInternalModule;
	}
}
