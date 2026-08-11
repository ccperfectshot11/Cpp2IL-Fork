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
using NetSpy.Contracts.Debugger.DotNet.Mono;
using NetSpy.Contracts.Debugger.StartDebugging.Dialog;
using NetSpy.Debugger.DotNet.Mono.Properties;

namespace NetSpy.Debugger.DotNet.Mono.Dialogs.DebugProgram {
	sealed class MonoConnectStartDebuggingOptionsPage : MonoConnectStartDebuggingOptionsPageBase {
		public override Guid Guid => new Guid("4B1A77AC-5FDB-4244-A847-0681801F7AA4");
		public override double DisplayOrder => PredefinedStartDebuggingOptionsPageDisplayOrders.DotNetMonoConnect;
		public override string DisplayName => "Mono (" + NetSpy_Debugger_DotNet_Mono_Resources.DbgAsm_Connect_To_Process + ")";

		public override void InitializePreviousOptions(StartDebuggingOptions options) {
			var dncOptions = options as MonoConnectStartDebuggingOptions;
			if (dncOptions is null)
				return;
			Initialize(dncOptions);
		}

		public override void InitializeDefaultOptions(string filename, string breakKind, StartDebuggingOptions? options) =>
			Initialize(GetDefaultOptions(filename, breakKind, options));

		MonoConnectStartDebuggingOptions GetDefaultOptions(string filename, string breakKind, StartDebuggingOptions? options) {
			if (options is MonoConnectStartDebuggingOptions connectOptions)
				return connectOptions;
			return CreateOptions(breakKind);
		}

		MonoConnectStartDebuggingOptions CreateOptions(string breakKind) =>
			InitializeDefault(new MonoConnectStartDebuggingOptions(), breakKind);

		void Initialize(MonoConnectStartDebuggingOptions options) {
			base.Initialize(options);
		}

		public override StartDebuggingOptionsInfo GetOptions() {
			var options = GetOptions(new MonoConnectStartDebuggingOptions());
			return new StartDebuggingOptionsInfo(options, null, StartDebuggingOptionsInfoFlags.None);
		}
	}
}
