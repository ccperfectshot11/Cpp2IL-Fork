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
using System.Windows.Interop;
using NetSpy.Contracts.App;
using NetSpy.Contracts.Debugger;
using NetSpy.Contracts.Debugger.CallStack;
using NetSpy.Contracts.Documents;
using NetSpy.Debugger.Native;
using NetSpy.Debugger.UI;

namespace NetSpy.Debugger.DbgUI {
	[Export(typeof(IDbgManagerStartListener))]
	sealed class WpfCurrentStatementUpdater : CurrentStatementUpdater {
		readonly UIDispatcher uiDispatcher;
		readonly Lazy<IAppWindow> appWindow;

		[ImportingConstructor]
		WpfCurrentStatementUpdater(UIDispatcher uiDispatcher, Lazy<IAppWindow> appWindow, DbgCallStackService dbgCallStackService, Lazy<ReferenceNavigatorService> referenceNavigatorService, Lazy<DebuggerSettings> debuggerSettings)
			: base(dbgCallStackService, referenceNavigatorService, debuggerSettings) {
			this.uiDispatcher = uiDispatcher;
			this.appWindow = appWindow;
		}

		protected override void ActivateMainWindow() => uiDispatcher.UI(() => ActivateMainWindow_UI());

		void ActivateMainWindow_UI() {
			uiDispatcher.VerifyAccess();
			if (mainWindowHandle == IntPtr.Zero)
				mainWindowHandle = new WindowInteropHelper(appWindow.Value.MainWindow).Handle;

			// SetForegroundWindow() must be called first or we won't get focus...
			NativeMethods.SetForegroundWindow(mainWindowHandle);
			NativeMethods.SetWindowPos(mainWindowHandle, IntPtr.Zero, 0, 0, 0, 0, 3);
			appWindow.Value.MainWindow.Activate();
		}
		IntPtr mainWindowHandle;
	}
}
