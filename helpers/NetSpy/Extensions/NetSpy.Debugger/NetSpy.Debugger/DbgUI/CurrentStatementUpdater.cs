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
using System.Diagnostics;
using NetSpy.Contracts.Debugger;
using NetSpy.Contracts.Debugger.CallStack;
using NetSpy.Contracts.Debugger.Code;
using NetSpy.Contracts.Documents;

namespace NetSpy.Debugger.DbgUI {
	abstract class CurrentStatementUpdater : IDbgManagerStartListener {
		readonly DbgCallStackService dbgCallStackService;
		readonly Lazy<ReferenceNavigatorService> referenceNavigatorService;
		readonly Lazy<DebuggerSettings> debuggerSettings;

		protected CurrentStatementUpdater(DbgCallStackService dbgCallStackService, Lazy<ReferenceNavigatorService> referenceNavigatorService, Lazy<DebuggerSettings> debuggerSettings) {
			this.dbgCallStackService = dbgCallStackService;
			this.referenceNavigatorService = referenceNavigatorService;
			this.debuggerSettings = debuggerSettings;
		}

		void IDbgManagerStartListener.OnStart(DbgManager dbgManager) => dbgManager.ProcessPaused += DbgManager_ProcessPaused;

		protected abstract void ActivateMainWindow();

		void DbgManager_ProcessPaused(object? sender, ProcessPausedEventArgs e) {
			Debug.Assert(dbgCallStackService.Thread == e.Thread);
			var info = GetLocation();
			if (info.location is not null) {
				dbgCallStackService.ActiveFrameIndex = info.frameIndex;
				referenceNavigatorService.Value.GoTo(info.location);
			}
			if (debuggerSettings.Value.FocusDebuggerWhenProcessBreaks)
				ActivateMainWindow();
		}

		(DbgCodeLocation? location, int frameIndex) GetLocation() {
			var frames = dbgCallStackService.Frames.Frames;
			for (int i = 0; i < frames.Count; i++) {
				var location = frames[i].Location;
				if (location is not null)
					return (location, i);
			}
			return (null, -1);
		}
	}
}
