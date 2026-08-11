/*
    Copyright (C) 2024 ElektroKill

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

using System.ComponentModel.Composition;
using NetSpy.Contracts.App;
using NetSpy.Contracts.Controls.ToolWindows;
using NetSpy.Contracts.Debugger;
using NetSpy.Contracts.Debugger.Dialogs;

namespace NetSpy.Debugger.Dialogs.EditEnvironment {
	[Export(typeof(IDbgEnvironmentEditorService))]
	class DbgEnvironmentEditorService : IDbgEnvironmentEditorService {
		readonly IAppWindow appWindow;
		readonly EditValueProviderService editValueProviderService;

		[ImportingConstructor]
		public DbgEnvironmentEditorService(IAppWindow appWindow, EditValueProviderService editValueProviderService) {
			this.appWindow = appWindow;
			this.editValueProviderService = editValueProviderService;
		}

		public bool ShowEditDialog(DbgEnvironment environment) {
			var editEnvironment = new EditEnvironmentVM(editValueProviderService, environment);
			var dlg = new EditEnvironmentDlg {
				DataContext = editEnvironment,
				Owner = appWindow.MainWindow
			};
			if (dlg.ShowDialog() != true)
				return false;
			editEnvironment.CopyTo(environment);
			return true;
		}
	}
}
