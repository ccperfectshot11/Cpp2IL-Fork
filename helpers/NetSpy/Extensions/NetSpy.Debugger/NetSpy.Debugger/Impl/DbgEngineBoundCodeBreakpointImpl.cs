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
using NetSpy.Contracts.Debugger;
using NetSpy.Contracts.Debugger.Breakpoints.Code;
using NetSpy.Contracts.Debugger.Engine;

namespace NetSpy.Debugger.Impl {
	sealed class DbgEngineBoundCodeBreakpointImpl : DbgEngineBoundCodeBreakpoint {
		public override DbgBoundCodeBreakpoint BoundCodeBreakpoint => boundCodeBreakpoint;
		readonly DbgBoundCodeBreakpointImpl boundCodeBreakpoint;

		public DbgEngineBoundCodeBreakpointImpl(DbgBoundCodeBreakpointImpl boundCodeBreakpoint) =>
			this.boundCodeBreakpoint = boundCodeBreakpoint ?? throw new ArgumentNullException(nameof(boundCodeBreakpoint));

		public override void Remove() => Remove(new[] { this });

		public override void Remove(DbgEngineBoundCodeBreakpoint[] breakpoints) {
			if (breakpoints is null)
				throw new ArgumentNullException(nameof(breakpoints));
			var bpImpls = new DbgEngineBoundCodeBreakpointImpl[breakpoints.Length];
			for (int i = 0; i < breakpoints.Length; i++) {
				if (!(breakpoints[i] is DbgEngineBoundCodeBreakpointImpl bpImpl))
					throw new ArgumentException();
				if (bpImpl.BoundCodeBreakpoint.Runtime != boundCodeBreakpoint.Runtime)
					throw new ArgumentException();
				bpImpls[i] = bpImpl;
			}
			boundCodeBreakpoint.Remove(bpImpls);
		}

		public override void Update(UpdateOptions options, DbgModule? module, ulong address, DbgEngineBoundCodeBreakpointMessage message) => boundCodeBreakpoint.Process.DbgManager.Dispatcher.BeginInvoke(() => {
			if (boundCodeBreakpoint.IsClosed)
				return;
			if ((options & UpdateOptions.Module) != 0)
				boundCodeBreakpoint.UpdateModule_DbgThread(module);
			if ((options & UpdateOptions.Address) != 0)
				boundCodeBreakpoint.UpdateAddress_DbgThread(address);
			if ((options & UpdateOptions.Message) != 0)
				boundCodeBreakpoint.UpdateMessage_DbgThread(message.ToDbgBoundCodeBreakpointMessage());
		});
	}
}
