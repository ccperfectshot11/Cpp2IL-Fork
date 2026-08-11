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
using NetSpy.Contracts.Images;
using NetSpy.Contracts.Menus;
using NetSpy.Contracts.Settings.Dialog;

namespace NetSpy.Settings.Dialog {
	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_VIEW_GUID, Header = "res:OptionsCommand", Icon = DsImagesAttribute.Settings, Group = MenuConstants.GROUP_APP_MENU_VIEW_OPTSDLG, Order = 1000000)]
	sealed class ShowOptionsCommand : MenuItemBase {
		readonly Lazy<IAppSettingsService> appSettingsService;

		[ImportingConstructor]
		ShowOptionsCommand(Lazy<IAppSettingsService> appSettingsService) => this.appSettingsService = appSettingsService;

		public override void Execute(IMenuItemContext context) => appSettingsService.Value.Show();
	}
}
