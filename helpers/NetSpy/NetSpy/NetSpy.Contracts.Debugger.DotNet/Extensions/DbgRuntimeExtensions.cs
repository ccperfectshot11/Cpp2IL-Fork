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
	public static class DbgRuntimeExtensions {
		/// <summary>
		/// Gets the reflection runtime or null if this isn't a managed runtime
		/// </summary>
		/// <param name="runtime">Debugger runtime</param>
		/// <returns></returns>
		public static DmdRuntime? GetReflectionRuntime(this DbgRuntime runtime) => (runtime.InternalRuntime as DbgDotNetInternalRuntime)?.ReflectionRuntime;

		/// <summary>
		/// Gets the internal .NET runtime or null if it's not a managed runtime
		/// </summary>
		/// <param name="runtime"></param>
		/// <returns></returns>
		public static DbgDotNetInternalRuntime? GetDotNetInternalRuntime(this DbgRuntime runtime) => runtime.InternalRuntime as DbgDotNetInternalRuntime;
	}
}
