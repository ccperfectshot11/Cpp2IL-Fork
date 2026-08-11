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
using System.Windows.Input;
using NetSpy.Contracts.Debugger;
using NetSpy.Contracts.Debugger.DotNet.Mono;
using NetSpy.Contracts.Debugger.StartDebugging.Dialog;
using NetSpy.Contracts.MVVM;
using NetSpy.Debugger.DotNet.Mono.Properties;

namespace NetSpy.Debugger.DotNet.Mono.Dialogs.DebugProgram {
	sealed class UnityConnectStartDebuggingOptionsPage : MonoConnectStartDebuggingOptionsPageBase {
		public override Guid Guid => new Guid("D244A779-9A4B-401B-99D4-6546B313260A");
		public override double DisplayOrder => PredefinedStartDebuggingOptionsPageDisplayOrders.DotNetUnityConnect;
		// Shouldn't be localized
		public override string DisplayName => "Unity (" + NetSpy_Debugger_DotNet_Mono_Resources.DbgAsm_Connect_To_Process + ")";

		// Default port used by the patched mono.dll
		const ushort DEFAULT_PORT = 55555;

		public ICommand DebuggingUnityGamesCommand => new RelayCommand(a => DebuggingUnityGamesHelper.OpenDebuggingUnityGames());
		public string DebuggingUnityGamesText => DebuggingUnityGamesHelper.DebuggingUnityGamesText;

		public override void InitializePreviousOptions(StartDebuggingOptions options) {
			var dncOptions = options as UnityConnectStartDebuggingOptions;
			if (dncOptions is null)
				return;
			Initialize(dncOptions);
		}

		public override void InitializeDefaultOptions(string filename, string breakKind, StartDebuggingOptions? options) =>
			Initialize(GetDefaultOptions(filename, breakKind, options));

		UnityConnectStartDebuggingOptions GetDefaultOptions(string filename, string breakKind, StartDebuggingOptions? options) {
			if (options is UnityConnectStartDebuggingOptions connectOptions)
				return connectOptions;
			return CreateOptions(breakKind);
		}

		UnityConnectStartDebuggingOptions CreateOptions(string breakKind) {
			var options = InitializeDefault(new UnityConnectStartDebuggingOptions(), breakKind);
			options.Port = DEFAULT_PORT;
			return options;
		}

		void Initialize(UnityConnectStartDebuggingOptions options) {
			base.Initialize(options);
		}

		public override StartDebuggingOptionsInfo GetOptions() {
			var options = GetOptions(new UnityConnectStartDebuggingOptions());
			return new StartDebuggingOptionsInfo(options, null, StartDebuggingOptionsInfoFlags.None);
		}
	}
}
