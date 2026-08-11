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

using NetSpy.Contracts.Debugger.Code;
using NetSpy.Contracts.Metadata;

namespace NetSpy.Contracts.Debugger.DotNet.Code {
	/// <summary>
	/// .NET code location
	/// </summary>
	public abstract class DbgDotNetCodeLocation : DbgCodeLocation, IDbgDotNetCodeLocation {
		/// <summary>
		/// Gets the module
		/// </summary>
		public abstract ModuleId Module { get; }

		/// <summary>
		/// Gets the token of a method within the module
		/// </summary>
		public abstract uint Token { get; }

		/// <summary>
		/// Gets the IL offset within the method body
		/// </summary>
		public abstract uint Offset { get; }

		/// <summary>
		/// Gets the IL offset mapping
		/// </summary>
		public abstract DbgILOffsetMapping ILOffsetMapping { get; }

		/// <summary>
		/// Gets the debugger module or null
		/// </summary>
		public abstract DbgModule? DbgModule { get; }

		/// <summary>
		/// Gets the native address
		/// </summary>
		public abstract DbgDotNetNativeFunctionAddress NativeAddress { get; }
	}
}
