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

#if DEBUG
using System;
using System.Collections.Generic;
using NetSpy.Contracts.Decompiler;

namespace NetSpy.Decompiler.ILSpy.Core.Settings {
	sealed class ILAstDecompilerSettings : DecompilerSettingsBase {
		public override int Version => 0;
		public override event EventHandler? VersionChanged { add { } remove { } }

		public ILAstDecompilerSettings() {
		}

		ILAstDecompilerSettings(ILAstDecompilerSettings other) {
		}

		public override DecompilerSettingsBase Clone() => new ILAstDecompilerSettings(this);

		public override IEnumerable<IDecompilerOption> Options {
			get { yield break; }
		}

		public override bool Equals(object? obj) => obj is ILAstDecompilerSettings;
		public override int GetHashCode() => 0;
	}
}
#endif
