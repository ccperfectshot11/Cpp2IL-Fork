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
using NetSpy.Contracts.Debugger.Breakpoints.Code;
using NetSpy.Contracts.Debugger.Engine;
using NetSpy.Debugger.Properties;

namespace NetSpy.Debugger.Impl {
	static class DbgEngineBoundCodeBreakpointMessageExtensions {
		public static DbgBoundCodeBreakpointMessage ToDbgBoundCodeBreakpointMessage(this DbgEngineBoundCodeBreakpointMessage message) {
			if (message.Arguments is not null) {
				switch (message.Kind) {
				case DbgEngineBoundCodeBreakpointMessageKind.None:
					return DbgBoundCodeBreakpointMessage.None;

				case DbgEngineBoundCodeBreakpointMessageKind.CustomWarning:
					if (message.Arguments.Length == 1 && message.Arguments[0] is not null)
						return new DbgBoundCodeBreakpointMessage(DbgBoundCodeBreakpointSeverity.Warning, message.Arguments[0]);
					break;

				case DbgEngineBoundCodeBreakpointMessageKind.CustomError:
					if (message.Arguments.Length == 1 && message.Arguments[0] is not null)
						return new DbgBoundCodeBreakpointMessage(DbgBoundCodeBreakpointSeverity.Error, message.Arguments[0]);
					break;

				case DbgEngineBoundCodeBreakpointMessageKind.FunctionNotFound:
					if (message.Arguments.Length == 1 && message.Arguments[0] is not null)
						return new DbgBoundCodeBreakpointMessage(DbgBoundCodeBreakpointSeverity.Error, string.Format(NetSpy_Debugger_Resources.BreakpointMessage_TheFunctionCanNotBeFound, message.Arguments[0]));
					break;

				case DbgEngineBoundCodeBreakpointMessageKind.CouldNotCreateBreakpoint:
					if (message.Arguments.Length == 0)
						return new DbgBoundCodeBreakpointMessage(DbgBoundCodeBreakpointSeverity.Error, NetSpy_Debugger_Resources.BreakpointMessage_CouldNotCreateBreakpoint);
					break;

				default:
					Debug.Fail($"Unknown message kind: {message.Kind}");
					break;
				}
			}
			Debug.Fail($"Invalid message");
			return new DbgBoundCodeBreakpointMessage(DbgBoundCodeBreakpointSeverity.Error, "???");
		}
	}
}
