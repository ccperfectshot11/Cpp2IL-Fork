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

using NetSpy.Contracts.Debugger.DotNet.Evaluation;
using NetSpy.Debugger.DotNet.Metadata;

namespace NetSpy.Contracts.Debugger.DotNet {
	/// <summary>
	/// Base class of a .NET runtime object implemented by the .NET debug engine. It must implement <see cref="IDbgDotNetRuntime"/>
	/// </summary>
	public abstract class DbgDotNetInternalRuntime : DbgInternalRuntime {
		/// <summary>
		/// Gets the reflection runtime
		/// </summary>
		public abstract DmdRuntime ReflectionRuntime { get; }
	}
}
