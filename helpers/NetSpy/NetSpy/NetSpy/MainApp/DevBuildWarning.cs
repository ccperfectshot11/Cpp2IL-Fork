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

using System.ComponentModel.Composition;
using System.IO;
using System.Reflection;
using NetSpy.Contracts.App;
using NetSpy.Contracts.Extension;

namespace NetSpy.MainApp {
	[ExportAutoLoaded]
	sealed class DevBuildWarning : IAutoLoaded {
		[ImportingConstructor]
		DevBuildWarning(IMessageBoxService messageBoxService) {
			if (IsCIBuild())
				messageBoxService.Show("This is a dev build of NetSpy and is missing features!\r\n\r\nDownload the latest master branch build from\r\n\r\nhttps://github.com/NetSpyEx/NetSpy/actions\r\n\r\nPress Ctrl+C to copy this text.");
		}

		bool IsCIBuild() {
			var startDir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
			var dir = Path.GetFileName(startDir);
			if (dir != "Debug" && dir != "Release")
				return true;
			if (!Directory.Exists(startDir + @"\..\..\..\..\.git"))
				return true;
			if (!Directory.Exists(startDir + @"\..\..\..\..\.vs"))
				return true;
			return false;
		}
	}
}
