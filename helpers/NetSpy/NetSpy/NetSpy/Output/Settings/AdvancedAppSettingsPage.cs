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
using NetSpy.Contracts.Settings.Dialog;
using NetSpy.Text.Settings;

namespace NetSpy.Output.Settings {
	sealed class AdvancedAppSettingsPage : AdvancedAppSettingsPageBase {
		public override Guid ParentGuid => new Guid(AppSettingsConstants.GUID_OUTPUT);
		public override Guid Guid => new Guid("E1F8E0A8-5F5A-42E4-B845-4E3885E09D69");
		public override double Order => AppSettingsConstants.ORDER_OUTPUT_DEFAULT_ADVANCED;

		public AdvancedAppSettingsPage(IOutputWindowOptions options)
			: base(options) {
		}
	}
}
