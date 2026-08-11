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

using System.Collections.Generic;
using NetSpy.Contracts.Extension;
using NetSpy.Scripting.Roslyn.Properties;

namespace NetSpy.Scripting.Roslyn {
	[ExportExtension]
	sealed class TheExtension : IExtension {
		public IEnumerable<string> MergedResourceDictionaries {
			get { yield break; }
		}

		public ExtensionInfo ExtensionInfo {
			get {
				return new ExtensionInfo {
					ShortDescription = NetSpy_Scripting_Roslyn_Resources.Plugin_ShortDescription,
				};
			}
		}

		public void OnEvent(ExtensionEvent @event, object? obj) {
		}
	}
}
