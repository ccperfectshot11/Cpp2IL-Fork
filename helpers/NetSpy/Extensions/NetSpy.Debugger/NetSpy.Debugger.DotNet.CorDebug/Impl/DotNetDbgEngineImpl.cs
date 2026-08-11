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
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using dndbg.Engine;
using NetSpy.Contracts.Debugger;
using NetSpy.Contracts.Debugger.DotNet;
using NetSpy.Contracts.Debugger.DotNet.CorDebug;
using NetSpy.Contracts.Debugger.Engine;
using NetSpy.Debugger.DotNet.CorDebug.Impl.Attach;
using NetSpy.Debugger.DotNet.CorDebug.Properties;
using NetSpy.Debugger.DotNet.CorDebug.Utilities;

namespace NetSpy.Debugger.DotNet.CorDebug.Impl {
	sealed class DotNetDbgEngineImpl : DbgEngineImpl {
		protected override CorDebugRuntimeKind CorDebugRuntimeKind => CorDebugRuntimeKind.DotNet;
		public override string[] Debugging => debugging;
		static readonly string[] debugging = new[] { "CoreCLR" };

		public override DbgEngineRuntimeInfo RuntimeInfo {
			get {
				Debug2.Assert(runtimeInfo is not null);
				return runtimeInfo;
			}
		}
		DbgEngineRuntimeInfo? runtimeInfo;

		int Bitness => IntPtr.Size * 8;

		public DotNetDbgEngineImpl(DbgEngineImplDependencies deps, DbgManager dbgManager, DbgStartKind startKind)
			: base(deps, dbgManager, startKind) {
		}

		string GetDbgShimAndVerify() {
			var dbgShimFilename = DotNetHelpers.GetDebugShimFilename(Bitness);
			if (!File.Exists(dbgShimFilename))
				throw new Exception("Couldn't find dbgshim.dll: " + dbgShimFilename);
			return dbgShimFilename;
		}

		protected override CLRTypeDebugInfo CreateDebugInfo(CorDebugStartDebuggingOptions options) {
			var dncOptions = (DotNetStartDebuggingOptions)options;
			string? hostFilename;
			if (!dncOptions.UseHost)
				hostFilename = null;
			else if (string.IsNullOrWhiteSpace(dncOptions.Host)) {
				hostFilename = DotNetHelpers.GetPathToDotNetExeHost(Bitness);
				if (!File.Exists(hostFilename))
					throw new Exception(string.Format(NetSpy_Debugger_DotNet_CorDebug_Resources.Error_CouldNotFindDotNetCoreHost, DotNetHelpers.DotNetExeName));
			}
			else
				hostFilename = dncOptions.Host;
			var hostCommandLine = dncOptions.HostArguments ?? string.Empty;
			return new CoreCLRTypeDebugInfo(GetDbgShimAndVerify(), hostFilename, hostCommandLine, dncOptions.ConnectionTimeout);
		}

		protected override CLRTypeAttachInfo CreateAttachInfo(CorDebugAttachToProgramOptions options) {
			var dncOptions = (DotNetAttachToProgramOptions)options;
			return new CoreCLRTypeAttachInfo(dncOptions.ClrModuleVersion, GetDbgShimAndVerify(), dncOptions.CoreCLRFilename);
		}

		protected override void OnDebugProcess(DnDebugger dnDebugger) =>
			runtimeInfo = new DbgEngineRuntimeInfo(PredefinedDbgRuntimeGuids.DotNet_Guid, PredefinedDbgRuntimeKindGuids.DotNet_Guid, "CoreCLR", new DotNetRuntimeId(dnDebugger.OtherVersion), runtimeTags);
		static readonly ReadOnlyCollection<string> runtimeTags = new ReadOnlyCollection<string>(new[] {
			PredefinedDotNetDbgRuntimeTags.DotNetBase,
			PredefinedDotNetDbgRuntimeTags.DotNet,
		});
	}

	sealed class DotNetRuntimeId : RuntimeId {
		readonly string? version;
		public DotNetRuntimeId(string? version) => this.version = version;
		public override bool Equals(object? obj) => obj is DotNetRuntimeId other && StringComparer.Ordinal.Equals(version ?? string.Empty, other.version ?? string.Empty);
		public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(version ?? string.Empty);
	}
}
