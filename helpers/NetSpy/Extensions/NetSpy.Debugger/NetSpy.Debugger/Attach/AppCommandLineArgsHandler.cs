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
using System.Linq;
using System.Threading;
using NetSpy.Contracts.App;
using NetSpy.Contracts.Debugger.Attach;

namespace NetSpy.Debugger.Attach {
	[Export(typeof(IAppCommandLineArgsHandler))]
	sealed class AppCommandLineArgsHandler : IAppCommandLineArgsHandler {
		readonly Lazy<AttachableProcessesService> attachableProcessesService;

		[ImportingConstructor]
		AppCommandLineArgsHandler(Lazy<AttachableProcessesService> attachableProcessesService) =>
			this.attachableProcessesService = attachableProcessesService;

		public double Order => 0;

		public async void OnNewArgs(IAppCommandLineArgs args) {
			if (args.DebugAttachPid is int pid && pid != 0) {
				var processes = await attachableProcessesService.Value.GetAttachableProcessesAsync(null, new[] { pid }, null, CancellationToken.None).ConfigureAwait(false);
				var process = processes.FirstOrDefault(p => p.ProcessId == pid);
				process?.Attach();
			}
			else if (args.DebugAttachProcess is string processName && !string.IsNullOrEmpty(processName)) {
				var processes = await attachableProcessesService.Value.GetAttachableProcessesAsync(processName, CancellationToken.None).ConfigureAwait(false);
				var process = processes.FirstOrDefault();
				process?.Attach();
			}
		}
	}
}
