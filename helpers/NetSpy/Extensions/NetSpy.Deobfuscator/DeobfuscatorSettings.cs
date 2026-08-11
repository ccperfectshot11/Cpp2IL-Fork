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
using System.ComponentModel.Composition;
using NetSpy.Contracts.Settings;

namespace NetSpy.Deobfuscator {
	/// <summary>Persisted options for the deobfuscator extension.</summary>
	[Export]
	sealed class DeobfuscatorSettings {
		static readonly Guid SETTINGS_GUID = new Guid("B2D1F6A4-3C7E-4E1B-9F2A-7C4D5E6F8A90");

		readonly ISettingsService settingsService;
		bool autoDeobfuscateOnLoad = true;

		/// <summary>Auto-detect and deobfuscate obfuscated assemblies as they are opened.</summary>
		public bool AutoDeobfuscateOnLoad {
			get => autoDeobfuscateOnLoad;
			set {
				if (autoDeobfuscateOnLoad == value)
					return;
				autoDeobfuscateOnLoad = value;
				Save();
			}
		}

		[ImportingConstructor]
		DeobfuscatorSettings(ISettingsService settingsService) {
			this.settingsService = settingsService;
			var sect = settingsService.GetOrCreateSection(SETTINGS_GUID);
			autoDeobfuscateOnLoad = sect.Attribute<bool?>(nameof(AutoDeobfuscateOnLoad)) ?? autoDeobfuscateOnLoad;
		}

		void Save() {
			var sect = settingsService.RecreateSection(SETTINGS_GUID);
			sect.Attribute(nameof(AutoDeobfuscateOnLoad), AutoDeobfuscateOnLoad);
		}
	}
}
