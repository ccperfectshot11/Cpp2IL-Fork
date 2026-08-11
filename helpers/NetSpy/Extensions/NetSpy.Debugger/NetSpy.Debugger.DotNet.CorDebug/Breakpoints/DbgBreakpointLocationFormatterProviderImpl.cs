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

using System;
using System.ComponentModel.Composition;
using NetSpy.Contracts.Debugger.Breakpoints.Code;
using NetSpy.Contracts.Debugger.Code;
using NetSpy.Debugger.DotNet.CorDebug.Code;

namespace NetSpy.Debugger.DotNet.CorDebug.Breakpoints {
	[ExportDbgBreakpointLocationFormatterProvider(PredefinedDbgCodeLocationTypes.DotNetCorDebugNative)]
	sealed class DbgBreakpointLocationFormatterProviderImpl : DbgBreakpointLocationFormatterProvider {
		readonly Lazy<BreakpointFormatterService> breakpointFormatterService;

		[ImportingConstructor]
		DbgBreakpointLocationFormatterProviderImpl(Lazy<BreakpointFormatterService> breakpointFormatterService) =>
			this.breakpointFormatterService = breakpointFormatterService;

		public override DbgBreakpointLocationFormatter? Create(DbgCodeLocation location) {
			switch (location) {
			case DbgDotNetNativeCodeLocationImpl nativeLoc:
				var formatter = nativeLoc.Formatter;
				if (formatter is not null)
					return formatter;
				formatter = breakpointFormatterService.Value.Create(nativeLoc);
				nativeLoc.Formatter = formatter;
				return formatter;
			}
			return null;
		}
	}
}
