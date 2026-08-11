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
using System.Collections.Generic;
using System.IO;

namespace NetSpy.Contracts.App {
	/// <summary>
	/// Application directories
	/// </summary>
	public static class AppDirectories {
		const string NETSPY_SETTINGS_FILENAME = "NetSpy.xml";

		/// <summary>
		/// Base directory of NetSpy binaries. If all files have been moved to a 'bin' sub dir,
		/// this is the path of the bin sub dir.
		/// </summary>
		public static string BinDirectory { get; }

		/// <summary>
		/// Base directory of data directory. Usually %APPDATA%\NetSpy but could be identical to
		/// <see cref="BinDirectory"/>.
		/// </summary>
		public static string DataDirectory { get; }

		/// <summary>
		/// NetSpy settings filename
		/// </summary>
		public static string SettingsFilename => settingsFilename;
		static string settingsFilename;

		internal static void SetSettingsFilename(string? filename) {
			if (hasCalledSetSettingsFilename)
				throw new InvalidOperationException();
			hasCalledSetSettingsFilename = true;
			if (!string2.IsNullOrEmpty(filename))
				settingsFilename = filename;
		}
		static bool hasCalledSetSettingsFilename = false;

		static AppDirectories() {
			// This assembly is always in the bin sub dir if one exists
			BinDirectory = Path.GetDirectoryName(typeof(AppDirectories).Assembly.Location)!;
			settingsFilename = Path.Combine(BinDirectory, NETSPY_SETTINGS_FILENAME);
			if (File.Exists(settingsFilename))
				DataDirectory = BinDirectory;
			else {
				DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetSpy");
				settingsFilename = Path.Combine(DataDirectory, NETSPY_SETTINGS_FILENAME);
			}
		}

		/// <summary>
		/// Returns directories relative to <see cref="BinDirectory"/> and <see cref="DataDirectory"/>
		/// in that order. If they're identical, only one path is returned.
		/// </summary>
		/// <param name="subDir">Sub directory</param>
		/// <returns></returns>
		public static IEnumerable<string> GetDirectories(string subDir) {
			yield return Path.Combine(BinDirectory, subDir);
			if (!StringComparer.OrdinalIgnoreCase.Equals(BinDirectory, DataDirectory))
				yield return Path.Combine(DataDirectory, subDir);
		}
	}
}
