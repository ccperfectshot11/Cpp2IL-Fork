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
using System.ComponentModel.Composition;
using NetSpy.Contracts.Settings;
using NetSpy.Scripting.Roslyn.Common;

namespace NetSpy.Scripting.Roslyn.VisualBasic {
	[Export]
	sealed class VisualBasicReplSettingsImpl : ReplSettingsImplBase {
		static readonly Guid SETTINGS_GUID = new Guid("EF6A2672-0D75-440B-9113-320EE04EBBB2");

		[ImportingConstructor]
		VisualBasicReplSettingsImpl(ISettingsService settingsService)
			: base(SETTINGS_GUID, settingsService) {
		}
	}
}
