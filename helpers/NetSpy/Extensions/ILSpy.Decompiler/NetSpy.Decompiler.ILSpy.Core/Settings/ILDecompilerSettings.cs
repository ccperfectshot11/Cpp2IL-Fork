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
using System.Linq;
using NetSpy.Contracts.Decompiler;
using NetSpy.Decompiler.ILSpy.Core.Properties;
using NetSpy.Decompiler.Settings;

namespace NetSpy.Decompiler.ILSpy.Core.Settings {
	sealed class ILDecompilerSettings : DecompilerSettingsBase {
		public ILSettings Settings => ilSettings;
		readonly ILSettings ilSettings;

		public override int Version => ilSettings.SettingsVersion;
		public override event EventHandler? VersionChanged;

		public ILDecompilerSettings(ILSettings? ilSettings = null) {
			this.ilSettings = ilSettings ?? new ILSettings();
			options = CreateOptions().ToArray();
			this.ilSettings.SettingsVersionChanged += ILSettings_SettingsVersionChanged;
		}

		void ILSettings_SettingsVersionChanged(object? sender, EventArgs e) => VersionChanged?.Invoke(this, EventArgs.Empty);

		public override DecompilerSettingsBase Clone() => new ILDecompilerSettings(ilSettings.Clone());

		public override IEnumerable<IDecompilerOption> Options => options;
		readonly IDecompilerOption[] options;

		IEnumerable<IDecompilerOption> CreateOptions() {
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.ShowILComments_GUID,
						() => ilSettings.ShowILComments, a => ilSettings.ShowILComments = a) {
				Description = NetSpy_Decompiler_ILSpy_Core_Resources.DecompilerSettings_ShowILComments,
				Name = DecompilerOptionConstants.ShowILComments_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.ShowXmlDocumentation_GUID,
						() => ilSettings.ShowXmlDocumentation, a => ilSettings.ShowXmlDocumentation = a) {
				Description = NetSpy_Decompiler_ILSpy_Core_Resources.DecompilerSettings_ShowXMLDocComments,
				Name = DecompilerOptionConstants.ShowXmlDocumentation_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.ShowTokenAndRvaComments_GUID,
						() => ilSettings.ShowTokenAndRvaComments, a => ilSettings.ShowTokenAndRvaComments = a) {
				Description = NetSpy_Decompiler_ILSpy_Core_Resources.DecompilerSettings_ShowTokensRvasOffsets,
				Name = DecompilerOptionConstants.ShowTokenAndRvaComments_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.ShowILBytes_GUID,
						() => ilSettings.ShowILBytes, a => ilSettings.ShowILBytes = a) {
				Description = NetSpy_Decompiler_ILSpy_Core_Resources.DecompilerSettings_ShowILInstrBytes,
				Name = DecompilerOptionConstants.ShowILBytes_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.SortMembers_GUID,
						() => ilSettings.SortMembers, a => ilSettings.SortMembers = a) {
				Description = NetSpy_Decompiler_ILSpy_Core_Resources.DecompilerSettings_SortMethods,
				Name = DecompilerOptionConstants.SortMembers_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.ShowPdbInfo_GUID,
						() => ilSettings.ShowPdbInfo, a => ilSettings.ShowPdbInfo = a) {
				Description = NetSpy_Decompiler_ILSpy_Core_Resources.DecompilerSettings_ShowPdbInfo,
				Name = DecompilerOptionConstants.ShowPdbInfo_NAME,
			};
			yield return new DecompilerOption<int>(DecompilerOptionConstants.MaxStringLength_GUID,
						() => ilSettings.MaxStringLength, a => ilSettings.MaxStringLength = a) {
				Description = NetSpy_Decompiler_ILSpy_Core_Resources.DecompilerSettings_MaxStringLength,
				Name = DecompilerOptionConstants.MaxStringLength_NAME,
			};
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.HexadecimalNumbers_GUID,
						() => ilSettings.HexadecimalNumbers, a => ilSettings.HexadecimalNumbers = a) {
				Description = NetSpy_Decompiler_ILSpy_Core_Resources.DecompilerSettings_HexadecimalNumbers,
				Name = DecompilerOptionConstants.HexadecimalNumbers_NAME,
			};
		}

		public override bool Equals(object? obj) =>
			obj is ILDecompilerSettings && ilSettings.Equals(((ILDecompilerSettings)obj).ilSettings);
		public override int GetHashCode() => ilSettings.GetHashCode();
	}
}
