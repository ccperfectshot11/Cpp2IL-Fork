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

using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using NetSpy.Contracts.App;
using NetSpy.Contracts.Documents.Tabs;
using NetSpy.Contracts.Extension;
using NetSpy.Contracts.Images;
using NetSpy.Contracts.MVVM;
using NetSpy.Contracts.ToolBars;
using NetSpy.Contracts.Utilities;
using NetSpy.Properties;

namespace NetSpy.Documents.Tabs {
	[ExportToolBarButton(OwnerGuid = ToolBarConstants.APP_TB_GUID, Icon = DsImagesAttribute.Backwards, Group = ToolBarConstants.GROUP_APP_TB_MAIN_NAVIGATION, Order = 0)]
	sealed class BrowseBackCommand : ToolBarButtonCommand {
		public BrowseBackCommand()
			: base(NavigationCommands.BrowseBack) {
		}
		public override string? GetToolTip(IToolBarItemContext context) => ToolTipHelper.AddKeyboardShortcut(NetSpy_Resources.NavigateBackCommand, NetSpy_Resources.ShortCutKeyBackspace);
	}

	[ExportToolBarButton(OwnerGuid = ToolBarConstants.APP_TB_GUID, Icon = DsImagesAttribute.Forwards, Group = ToolBarConstants.GROUP_APP_TB_MAIN_NAVIGATION, Order = 10)]
	sealed class BrowseForwardCommand : ToolBarButtonCommand {
		public BrowseForwardCommand()
			: base(NavigationCommands.BrowseForward) {
		}
		public override string? GetToolTip(IToolBarItemContext context) => ToolTipHelper.AddKeyboardShortcut(NetSpy_Resources.NavigateForwardCommand, NetSpy_Resources.ShortCutKeyAltRightArrow);
	}

	[ExportAutoLoaded]
	sealed class NavigationCommandInstaller : IAutoLoaded {
		readonly IDocumentTabService documentTabService;

		[ImportingConstructor]
		NavigationCommandInstaller(IDocumentTabService documentTabService, IAppWindow appWindow) {
			this.documentTabService = documentTabService;
			Debug2.Assert(Application.Current is not null && Application.Current.MainWindow is not null);
			var cmds = appWindow.MainWindowCommands;
			cmds.Add(NavigationCommands.BrowseBack, new RelayCommand(a => BrowseBack(), a => CanBrowseBack));
			cmds.Add(NavigationCommands.BrowseForward, new RelayCommand(a => BrowseForward(), a => CanBrowseForward));
		}

		bool CanBrowseBack => documentTabService.ActiveTab?.CanNavigateBackward == true;

		void BrowseBack() {
			if (!CanBrowseBack)
				return;
			documentTabService.ActiveTab!.NavigateBackward();
		}

		bool CanBrowseForward => documentTabService.ActiveTab?.CanNavigateForward == true;

		void BrowseForward() {
			if (!CanBrowseForward)
				return;
			documentTabService.ActiveTab!.NavigateForward();
		}
	}
}
