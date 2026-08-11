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

using NetSpy.Debugger.Properties;

namespace NetSpy.Debugger.ToolWindows.Watch {
	class WatchWindowsHelper {
		public static readonly int NUMBER_OF_WATCH_WINDOWS = 4;

		public static string GetHeaderText(int i) {
			if (i == 9)
				return NetSpy_Debugger_Resources.Window_Watch_10_MenuItem;
			if (0 <= i && i <= 8)
				return string.Format(NetSpy_Debugger_Resources.Window_Watch_N_MenuItem, (i + 1) % 10);
			return string.Format(NetSpy_Debugger_Resources.Window_Watch_N_MenuItem2, i + 1);
		}
	}
}
