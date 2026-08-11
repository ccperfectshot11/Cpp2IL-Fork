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

using System.Threading;

namespace NetSpy.Decompiler.ILSpy.Core.Settings {
	class DecompilerSettingsService {
		/// <summary>
		/// Should only be used indirectly by NetSpy.Console.exe
		/// </summary>
		public static DecompilerSettingsService __Instance_DONT_USE {
			get {
				if (__instance_DONT_USE is null)
					Interlocked.CompareExchange(ref __instance_DONT_USE, new DecompilerSettingsService(), null);
				return __instance_DONT_USE!;
			}
		}
		static DecompilerSettingsService? __instance_DONT_USE;

		protected DecompilerSettingsService() {
			CSharpVBDecompilerSettings = new CSharpVBDecompilerSettings();
			ILDecompilerSettings = new ILDecompilerSettings();
		}

		public CSharpVBDecompilerSettings CSharpVBDecompilerSettings { get; protected set; }
		public ILDecompilerSettings ILDecompilerSettings { get; protected set; }

#if DEBUG
		public ILAstDecompilerSettings ILAstDecompilerSettings { get; } = new ILAstDecompilerSettings();
#endif
	}
}
