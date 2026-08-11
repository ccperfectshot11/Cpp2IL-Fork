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
using System.IO;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using NetSpy.Contracts.ETW;
using NetSpy.Properties;

namespace NetSpy.MainApp {
	public static class StartUpClass {
		[STAThread]
		public static void Main() {
			NetSpyEventSource.Log.StartupStart();
			var sw = Stopwatch.StartNew();

			// Use multicore JIT.
			// Simple test: x86: ~18% faster startup, x64: ~12% faster startup.
			try {
				var profileDir = BGJitUtils.GetFolder();
				Directory.CreateDirectory(profileDir);
				ProfileOptimization.SetProfileRoot(profileDir);
				ProfileOptimization.StartProfile("startup.profile");
			}
			catch {
			}

			if (!dnlib.Settings.IsThreadSafe)
				ErrorNotThreadSafe();

			bool readSettings = (Keyboard.Modifiers & ModifierKeys.Shift) == 0;
			if (!readSettings)
				readSettings = AskReadSettings();

			new App(readSettings, sw).Run();
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		static void ErrorNotThreadSafe() {
			MessageBox.Show("dnlib wasn't compiled with THREAD_SAFE defined.");
			Environment.Exit(1);
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		static bool AskReadSettings() {
			bool readSettings;
			// Need to use DefaultDesktopOnly or the dlg box is shown in the background...
			var res = MessageBox.Show(NetSpy_Resources.AskReadSettings, Constants.NetSpy, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes, MessageBoxOptions.DefaultDesktopOnly);
			readSettings = res != MessageBoxResult.No;
			return readSettings;
		}
	}
}
