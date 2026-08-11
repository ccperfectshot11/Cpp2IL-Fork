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
using NetSpy.Contracts.Debugger;
using NetSpy.Contracts.Debugger.Breakpoints.Modules;
using NetSpy.Contracts.Settings;
using NetSpy.Debugger.Impl;

namespace NetSpy.Debugger.Breakpoints.Modules {
	[Export(typeof(IDbgModuleBreakpointsServiceListener))]
	sealed class ModuleBreakpointsListSettingsListener : IDbgModuleBreakpointsServiceListener {
		readonly DbgDispatcherProvider dbgDispatcherProvider;
		readonly ISettingsService settingsService;

		[ImportingConstructor]
		ModuleBreakpointsListSettingsListener(DbgDispatcherProvider dbgDispatcherProvider, ISettingsService settingsService) {
			this.dbgDispatcherProvider = dbgDispatcherProvider;
			this.settingsService = settingsService;
		}

		void IDbgModuleBreakpointsServiceListener.Initialize(DbgModuleBreakpointsService dbgModuleBreakpointsService) =>
			new ModuleBreakpointsListSettings(dbgDispatcherProvider, settingsService, dbgModuleBreakpointsService);
	}

	sealed class ModuleBreakpointsListSettings {
		readonly DbgDispatcherProvider dbgDispatcherProvider;
		readonly ISettingsService settingsService;
		readonly DbgModuleBreakpointsService dbgModuleBreakpointsService;

		public ModuleBreakpointsListSettings(DbgDispatcherProvider dbgDispatcherProvider, ISettingsService settingsService, DbgModuleBreakpointsService dbgModuleBreakpointsService) {
			this.dbgDispatcherProvider = dbgDispatcherProvider ?? throw new ArgumentNullException(nameof(dbgDispatcherProvider));
			this.settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
			this.dbgModuleBreakpointsService = dbgModuleBreakpointsService ?? throw new ArgumentNullException(nameof(dbgModuleBreakpointsService));
			dbgModuleBreakpointsService.BreakpointsChanged += DbgModuleBreakpointsService_BreakpointsChanged;
			dbgModuleBreakpointsService.BreakpointsModified += DbgModuleBreakpointsService_BreakpointsModified;
			dbgDispatcherProvider.Dbg(() => Load());
		}

		void Load() {
			dbgDispatcherProvider.VerifyAccess();
			ignoreSave = true;
			dbgModuleBreakpointsService.Add(new BreakpointsSerializer(settingsService).Load());
			dbgDispatcherProvider.Dbg(() => ignoreSave = false);
		}

		void DbgModuleBreakpointsService_BreakpointsChanged(object? sender, DbgCollectionChangedEventArgs<DbgModuleBreakpoint> e) => Save();
		void DbgModuleBreakpointsService_BreakpointsModified(object? sender, DbgBreakpointsModifiedEventArgs e) => Save();

		void Save() {
			dbgDispatcherProvider.VerifyAccess();
			if (ignoreSave)
				return;
			new BreakpointsSerializer(settingsService).Save(dbgModuleBreakpointsService.Breakpoints);
		}
		bool ignoreSave;
	}
}
