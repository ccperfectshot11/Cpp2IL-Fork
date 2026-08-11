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

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using NetSpy.Contracts.Debugger;
using NetSpy.Contracts.Debugger.DotNet.CorDebug;

namespace NetSpy.Debugger.DotNet.CorDebug.AntiAntiDebug {
	static class CorDebugUtils {
		public static bool TryGetInternalRuntime(DbgProcess process, [NotNullWhen(true)] out DbgCorDebugInternalRuntime? runtime) {
			runtime = null;
			var dbgRuntime = process.Runtimes.FirstOrDefault();
			Debug2.Assert(dbgRuntime is not null);
			if (dbgRuntime is null)
				return false;

			runtime = dbgRuntime.InternalRuntime as DbgCorDebugInternalRuntime;
			return runtime is not null;
		}
	}
}
