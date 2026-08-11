/*
    Copyright (C) 2014-2026 de4dot@gmail.com

    This file is part of NetSpy / NetSpy

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
using System.IO;

namespace NetSpy.Deobfuscator {
	/// <summary>Simple file logger so deobfuscation activity/errors are visible even when
	/// the auto path runs silently. Writes to %APPDATA%\NetSpy\deobfuscator.log.</summary>
	static class DeobLog {
		static readonly object sync = new object();
		static readonly string logPath = GetLogPath();

		public static string LogPath => logPath;

		static string GetLogPath() {
			try {
				var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetSpy");
				Directory.CreateDirectory(dir);
				return Path.Combine(dir, "deobfuscator.log");
			}
			catch {
				return Path.Combine(Path.GetTempPath(), "NetSpy-deobfuscator.log");
			}
		}

		public static void Log(string message) {
			try {
				lock (sync)
					File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
			}
			catch { /* logging must never throw */ }
		}

		public static void Log(string message, Exception ex) =>
			Log($"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
	}
}
