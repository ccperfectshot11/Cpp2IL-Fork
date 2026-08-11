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
using NetSpy.Contracts.Debugger.StartDebugging;
using NetSpy.Debugger.DotNet.CorDebug.Impl;

namespace NetSpy.Debugger.DotNet.CorDebug.Dialogs.DebugProgram {
	[ExportGenericDebugEngineGuidProvider(PredefinedGenericDebugEngineGuidProviderOrders.DotNet)]
	sealed class DotNetGenericDebugEngineGuidProvider : GenericDebugEngineGuidProvider {
		public override Guid? GetEngineGuid(string filename) {
			if (!IsDotNetAppHostFilename(filename))
				return null;
			return PredefinedGenericDebugEngineGuids.DotNet;
		}

		internal static bool IsDotNetAppHostFilename(string filename) {
			if (!File.Exists(filename))
				return false;
			return
				AppHostUtils.IsDotNetBundleV1(filename) ||
				AppHostUtils.IsDotNetBundleV2_or_AppHost(filename) ||
				IsKnownDotNetAppHost(filename) ||
				AppHostUtils.IsDotNetAppHostV1(filename, out _);
		}

		static bool IsKnownDotNetAppHost(string filename) {
			if (AppHostUtils.TryGetAppHostEmbeddedDotNetDllPath(filename, out var couldBeAppHost, out _))
				return true;
			Debug.Assert(!couldBeAppHost, $"Looks like an unsupported apphost, update {nameof(AppHostInfoData)} table");
			return false;
		}
	}
}
